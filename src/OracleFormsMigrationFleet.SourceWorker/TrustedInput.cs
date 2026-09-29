// Copyright (c) Microsoft. All rights reserved.

using System.Security.Cryptography;

namespace OracleFormsMigrationFleet.SourceWorker;

public static class ContentHash
{
    public static bool IsSha256(string? value) =>
        value is { Length: 64 } && value.All(char.IsAsciiHexDigit);

    public static async Task<string> OfFileAsync(string path, CancellationToken cancellationToken)
    {
        await using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, useAsync: true);
        byte[] digest = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexStringLower(digest);
    }

    public static string OfBytes(ReadOnlySpan<byte> content) => Convert.ToHexStringLower(SHA256.HashData(content));

    /// <summary>Compares two hex digests without leaking where they first differ.</summary>
    public static bool Matches(string expected, string observed) =>
        expected.Length == observed.Length &&
        CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.ASCII.GetBytes(expected.ToLowerInvariant()),
            System.Text.Encoding.ASCII.GetBytes(observed.ToLowerInvariant()));
}

public sealed record TrustedInputResolution(string? Path, string? ObservedSha256, string? Rejection);

/// <summary>
/// Resolves a caller-supplied module alias to a file under the configured trusted input root, and nowhere
/// else.
///
/// The alias is a single file name. It is not a relative path, so there is no traversal to normalize away
/// and no device name, stream name or UNC prefix to strip — those are rejected as malformed aliases before
/// any path is built. After the path is built it is resolved and re-checked against the root on whole
/// segments, so a link or a root whose name merely prefixes a sibling cannot widen the surface either.
/// </summary>
public static class TrustedInput
{
    public const long MaxModuleBytes = 128L * 1024 * 1024;

    private static readonly string[] s_supportedExtensions = [".fmb", ".mmb", ".pll", ".olb"];

    public static bool IsWellFormedAlias(string? alias)
    {
        if (alias is not { Length: > 0 and <= 128 })
        {
            return false;
        }

        foreach (char character in alias)
        {
            if (!char.IsAsciiLetterOrDigit(character) && character is not ('.' or '_' or '-'))
            {
                return false;
            }
        }

        // A leading dot would let "..", ".", and hidden names through the character filter above.
        return alias[0] != '.' && !alias.EndsWith('.') && !alias.Contains("..", StringComparison.Ordinal);
    }

    public static async Task<TrustedInputResolution> ResolveAsync(
        string trustedRoot,
        string alias,
        string expectedSha256,
        CancellationToken cancellationToken)
    {
        if (!IsWellFormedAlias(alias))
        {
            return new TrustedInputResolution(null, null,
                "The module alias is not a bare file name of permitted characters, so no path was built from it.");
        }

        if (!ContentHash.IsSha256(expectedSha256))
        {
            return new TrustedInputResolution(null, null,
                "The request did not pin the module content to a SHA-256 digest, so nothing was opened.");
        }

        if (!s_supportedExtensions.Contains(Path.GetExtension(alias), StringComparer.OrdinalIgnoreCase))
        {
            return new TrustedInputResolution(null, null,
                $"The module alias does not name a supported Forms module ({string.Join(", ", s_supportedExtensions)}).");
        }

        string candidate;
        try
        {
            candidate = Path.GetFullPath(Path.Combine(trustedRoot, alias));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return new TrustedInputResolution(null, null, "The module alias did not resolve to a usable path.");
        }

        if (!IsWithin(trustedRoot, candidate))
        {
            return new TrustedInputResolution(null, null,
                "The module alias resolved outside the configured trusted input root, so it was refused.");
        }

        FileInfo file = new(candidate);
        if (!file.Exists)
        {
            return new TrustedInputResolution(null, null,
                "No module with that alias is present under the configured trusted input root.");
        }

        // A reparse point under the root can still point outside it, and the fully-qualified check above
        // cannot see that. Refusing is cheaper than resolving a link chain correctly on every filesystem.
        if (file.LinkTarget is not null)
        {
            return new TrustedInputResolution(null, null,
                "The module alias resolved to a link rather than a regular file, so it was refused.");
        }

        if (file.Length == 0 || file.Length > MaxModuleBytes)
        {
            return new TrustedInputResolution(null, null,
                $"The module is empty or exceeds the {MaxModuleBytes} byte worker limit, so it was not read.");
        }

        string observed = await ContentHash.OfFileAsync(candidate, cancellationToken);
        return ContentHash.Matches(expectedSha256, observed)
            ? new TrustedInputResolution(candidate, observed, null)
            : new TrustedInputResolution(null, observed,
                "The module content does not match the SHA-256 the request pinned, so it was not opened.");
    }

    public static bool IsWithin(string root, string candidate)
    {
        string normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        string normalizedCandidate = Path.GetFullPath(candidate);
        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        return normalizedCandidate.Length > normalizedRoot.Length &&
            normalizedCandidate.StartsWith(normalizedRoot, comparison) &&
            (normalizedCandidate[normalizedRoot.Length] == Path.DirectorySeparatorChar ||
             normalizedCandidate[normalizedRoot.Length] == Path.AltDirectorySeparatorChar);
    }
}
