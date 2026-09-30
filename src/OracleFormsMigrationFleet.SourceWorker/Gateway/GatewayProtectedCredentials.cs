using System.Security.Cryptography;
using System.Text;

namespace OracleFormsMigrationFleet.SourceWorker.Gateway;

/// <summary>
/// Where one resolved Oracle connect string came from, or why there is none.
///
/// <see cref="Value"/> is the only place a credential appears, and it is handed straight to a child
/// process environment by the caller. <see cref="Source"/> and <see cref="Failure"/> are written for
/// operators and are constructed from variable names and file paths only, never from a decrypted byte.
/// </summary>
public sealed record GatewayCredentialResolution(string? Value, string Source, string? Failure)
{
    public static GatewayCredentialResolution Refused(string source, string failure) => new(null, source, failure);

    public static GatewayCredentialResolution Resolved(string value, string source) => new(value, source, null);
}

/// <summary>
/// Resolves the Oracle connect string a registry entry names, preferring a DPAPI-protected file over the
/// gateway host's process environment.
///
/// The registry still holds a variable NAME and nothing else. This adds a second place that name can be
/// satisfied from, so an unattended service does not need the credential sitting in its own environment
/// block where any process reading the service's environment could recover it. A protected file is
/// encrypted with <see cref="DataProtectionScope.CurrentUser"/>, so only the service account that wrote
/// it can read it back; copying the file to another host or another account yields nothing.
///
/// Three rules make this safe to run unattended:
///
/// 1. <b>The file name is derived, never configured per credential.</b> The locator is one directory. The
///    file for variable <c>OFM_GATEWAY_ORACLE_X</c> is <c>OFM_GATEWAY_ORACLE_X.dpapi</c> beneath it, so
///    no registry document and no request can point the gateway at an arbitrary protected blob.
/// 2. <b>The blob is bound to the variable name.</b> The DPAPI entropy includes the variable name, so a
///    protected file renamed to a sibling's name fails to decrypt rather than serving the wrong database.
/// 3. <b>There is no silent fallback.</b> A protected file that exists but cannot be read, decrypted or
///    parsed is a refusal. The environment is consulted only when no protected file was provisioned at
///    all, which is the explicit, compatible case that keeps the existing development path working.
/// </summary>
public sealed class GatewayCredentialProvider
{
    /// <summary>Extension of a protected credential file. The stem is always the variable name.</summary>
    public const string ProtectedFileExtension = ".dpapi";

    /// <summary>
    /// Non-secret DPAPI entropy prefix. It is a domain separator, not a key: it binds a blob to this
    /// product and to one variable name, so it is safe in source and would be useless to an attacker who
    /// does not already hold the service account's profile.
    /// </summary>
    private const string EntropyPrefix = "OracleFormsMigrationFleet.SourceGateway.OracleCredential.v1:";

    /// <summary>A connect string longer than this is a configuration mistake, not a credential.</summary>
    internal const int MaxCredentialCharacters = 4096;

    /// <summary>DPAPI overhead is small; anything this large is not a protected connect string.</summary>
    internal const int MaxProtectedBlobBytes = 32 * 1024;

    private readonly string? _protectedRoot;
    private readonly Func<string, string?> _readEnvironment;
    private readonly Func<string, byte[]?> _readProtectedFile;
    private readonly Func<byte[], byte[], byte[]> _unprotect;

    public GatewayCredentialProvider(
        string? protectedRoot,
        Func<string, string?>? readEnvironment = null,
        Func<string, byte[]?>? readProtectedFile = null,
        Func<byte[], byte[], byte[]>? unprotect = null)
    {
        _protectedRoot = protectedRoot;
        _readEnvironment = readEnvironment ?? Environment.GetEnvironmentVariable;
        _readProtectedFile = readProtectedFile ?? ReadFileOrNull;
        _unprotect = unprotect ?? UnprotectForCurrentUser;
    }

    /// <summary>Non-secret description of where credentials are read from, safe to print at startup.</summary>
    public string Description =>
        _protectedRoot is null
            ? "Oracle connect strings are read from this host's process environment under the variable each registry entry names."
            : $"Oracle connect strings are read from DPAPI-protected files beneath '{_protectedRoot}', falling back to this " +
              "host's process environment only when no protected file was provisioned for the variable.";

    /// <summary>The file a protected credential for <paramref name="variableName"/> would occupy.</summary>
    public string? ProtectedPathFor(string variableName) =>
        _protectedRoot is null || !GatewayOracleCredential.IsReadableVariable(variableName)
            ? null
            : Path.Combine(_protectedRoot, variableName + ProtectedFileExtension);

    public GatewayCredentialResolution Resolve(string variableName)
    {
        if (!GatewayOracleCredential.IsReadableVariable(variableName))
        {
            // Unreachable through a parsed registry, which validates the same rule. Checked again because
            // this is the boundary that turns a name into a filesystem path.
            return GatewayCredentialResolution.Refused(
                "rejected",
                "The Oracle connection variable name is not one this gateway will read, so nothing was resolved.");
        }

        string? path = ProtectedPathFor(variableName);
        if (path is not null)
        {
            byte[]? blob = _readProtectedFile(path);
            if (blob is not null)
            {
                return Unprotect(variableName, path, blob);
            }
        }

        return _readEnvironment(variableName) is { Length: > 0 } value && value.Trim().Length > 0
            ? GatewayCredentialResolution.Resolved(value.Trim(), "environment")
            : GatewayCredentialResolution.Refused(
                "environment",
                path is null
                    ? $"The Oracle connection variable this source environment names ({variableName}) is unset on the " +
                      "gateway host, so no worker was started and no connection was opened."
                    : $"The Oracle connection variable this source environment names ({variableName}) has neither a " +
                      $"protected credential file at '{path}' nor a value in this host's environment, so no worker was " +
                      "started and no connection was opened.");
    }

    private GatewayCredentialResolution Unprotect(string variableName, string path, byte[] blob)
    {
        if (blob.Length is 0 or > MaxProtectedBlobBytes)
        {
            return GatewayCredentialResolution.Refused(
                "protected-file",
                $"The protected credential file at '{path}' is empty, unreadable, or larger than the accepted protected " +
                "credential limit, so it was refused rather than falling back to the environment.");
        }

        if (!OperatingSystem.IsWindows())
        {
            return GatewayCredentialResolution.Refused(
                "protected-file",
                $"A protected credential file exists at '{path}', but DPAPI is a Windows facility and this host is not " +
                "Windows. The gateway refuses rather than falling back to an unprotected value.");
        }

        byte[] plaintext;
        try
        {
            plaintext = _unprotect(blob, Entropy(variableName));
        }
        catch (CryptographicException)
        {
            // Wrong account, wrong machine, a blob written for a different variable, or a corrupted file.
            // DPAPI cannot tell these apart and neither should the message. There is deliberately no
            // fallback: an operator who provisioned a protected file must have it work or be told.
            return GatewayCredentialResolution.Refused(
                "protected-file",
                $"The protected credential file at '{path}' could not be decrypted by this service account. It is " +
                "refused rather than falling back to the environment; re-protect it under the account the service runs as.");
        }

        try
        {
            string? value = Decode(plaintext);
            return value is null
                ? GatewayCredentialResolution.Refused(
                    "protected-file",
                    $"The protected credential file at '{path}' decrypted to something that is not a single-line connect " +
                    "string of at most " + MaxCredentialCharacters + " characters, so it was refused.")
                : GatewayCredentialResolution.Resolved(value, "protected-file");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    /// <summary>
    /// The decoded connect string, or null when the plaintext is not one. Control characters are rejected
    /// rather than stripped: a value carrying a newline could inject a second setting into a child's
    /// environment on some hosts, and a credential that decrypts to something unexpected is a fault.
    /// </summary>
    private static string? Decode(byte[] plaintext)
    {
        if (plaintext.Length is 0 or > MaxCredentialCharacters * 4)
        {
            return null;
        }

        string decoded;
        try
        {
            decoded = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(plaintext);
        }
        catch (ArgumentException)
        {
            return null;
        }

        // A file written by a text editor or by PowerShell redirection commonly carries a BOM or a
        // trailing newline. Those are tolerated; anything else control-shaped is not.
        string trimmed = decoded.TrimStart('\uFEFF').Trim();
        return trimmed.Length is > 0 and <= MaxCredentialCharacters && !trimmed.Any(char.IsControl)
            ? trimmed
            : null;
    }

    internal static byte[] Entropy(string variableName) => Encoding.UTF8.GetBytes(EntropyPrefix + variableName);

    internal static bool IsExistingUnlinkedDirectory(string path)
    {
        try
        {
            FileAttributes attributes = File.GetAttributes(path);
            return attributes.HasFlag(FileAttributes.Directory) &&
                   !attributes.HasFlag(FileAttributes.ReparsePoint);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            return false;
        }
    }

    /// <summary>
    /// Protects <paramref name="value"/> for the current user so an operator can provision the file with
    /// the same code path that reads it. The caller obtains the value from a console prompt or a pipe; it
    /// is never a command-line argument, and this method neither logs nor returns it.
    /// </summary>
    public static byte[] Protect(string variableName, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(variableName);
        ArgumentException.ThrowIfNullOrWhiteSpace(value);

        if (!GatewayOracleCredential.IsReadableVariable(variableName))
        {
            throw new ArgumentException(
                $"A protected credential variable name must begin '{GatewayOracleCredential.RequiredPrefix}'.",
                nameof(variableName));
        }

        string normalized = value.Trim();
        if (normalized.Length is 0 or > MaxCredentialCharacters || normalized.Any(char.IsControl))
        {
            throw new ArgumentException(
                $"A protected credential must be one non-empty line of at most {MaxCredentialCharacters} characters.",
                nameof(value));
        }

        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Protected credential files are a Windows DPAPI facility.");
        }

        byte[] plaintext = Encoding.UTF8.GetBytes(normalized);
        try
        {
            return ProtectedData.Protect(plaintext, Entropy(variableName), DataProtectionScope.CurrentUser);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    private static byte[] UnprotectForCurrentUser(byte[] blob, byte[] entropy) =>
        OperatingSystem.IsWindows()
            ? ProtectedData.Unprotect(blob, entropy, DataProtectionScope.CurrentUser)
            : throw new CryptographicException("DPAPI is not available on this host.");

    private static byte[]? ReadFileOrNull(string path)
    {
        try
        {
            FileAttributes attributes = File.GetAttributes(path);
            if (attributes.HasFlag(FileAttributes.Directory) || attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                return [];
            }

            using FileStream stream = new(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 4096,
                FileOptions.SequentialScan);

            if (stream.Length is <= 0 or > MaxProtectedBlobBytes)
            {
                return [];
            }

            byte[] blob = new byte[checked((int)stream.Length)];
            stream.ReadExactly(blob);
            if (stream.ReadByte() >= 0)
            {
                CryptographicOperations.ZeroMemory(blob);
                return [];
            }

            return blob;
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            // Only a definite not-found result is absence. Every other filesystem failure keeps the
            // caller on the refusal path rather than falling through to the environment.
            return [];
        }
    }
}
