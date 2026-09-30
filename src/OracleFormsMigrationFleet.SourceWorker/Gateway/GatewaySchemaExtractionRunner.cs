using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace OracleFormsMigrationFleet.SourceWorker.Gateway;

/// <summary>What one schema worker invocation produced, or the reason there is no result to read.</summary>
public sealed record GatewaySchemaRunOutcome(OracleSchemaExtractionResult? Result, int? ExitCode, string? Failure)
{
    public static GatewaySchemaRunOutcome Failed(string failure) => new(null, null, failure);
}

/// <summary>
/// Runs one bounded <c>--extract-schema</c>. Separate from the coordinator so admission, correlation and
/// inlining can be tested without a database, and so the real spawn can be tested on its own.
/// </summary>
public interface IGatewaySchemaExtractionRunner
{
    Task<GatewaySchemaRunOutcome> RunAsync(
        GatewaySourceEntry entry,
        OracleSchemaExtractionRequest request,
        CancellationToken cancellationToken);
}

/// <summary>
/// Runs the schema worker in a CHILD PROCESS with a rebuilt environment.
///
/// Two things make this different from the Forms runner beside it, and both are about the credential:
///
/// 1. <b>The child's environment is built, not inherited.</b> <see cref="ProcessStartInfo.Environment"/>
///    starts as a copy of this gateway's own, which on a real host carries whatever else the operator or
///    the platform put there. It is cleared and repopulated from a fixed list of operating-system and
///    ODBC variables, so a schema extraction cannot carry an unrelated secret into a child that is about
///    to talk to a customer database.
/// 2. <b>The connect string is resolved at spawn time under a name the registry pinned.</b> It comes from
///    a DPAPI-protected file this service account can decrypt, or — when none was provisioned — from this
///    host's environment under a variable whose name must begin with
///    <see cref="GatewayOracleCredential.RequiredPrefix"/>. Either way it is written only into the child's
///    own <see cref="OracleSourceConfiguration.ConnectionStringVariable"/>, and neither the source
///    variable nor the protected root is among the names copied across, so the child cannot read it twice
///    or read a sibling entry's.
///
/// Nothing on the request influences the command line, the variable names, or the schemas: the allowlist
/// handed down is the registry's, and the worker narrows it against the request itself.
/// </summary>
public sealed class ChildProcessSchemaExtractionRunner(
    GatewayOptions options,
    Func<string, string?>? readEnvironment = null,
    Func<ProcessStartInfo>? launcher = null,
    GatewayCredentialProvider? credentials = null) : IGatewaySchemaExtractionRunner
{
    private readonly GatewayCredentialProvider _credentials =
        credentials ?? new GatewayCredentialProvider(options.ProtectedCredentialRoot, readEnvironment);

    private readonly Func<ProcessStartInfo> _launcher = launcher ?? ChildProcessExtractionRunner.SelfLauncher;

    /// <summary>
    /// The only inherited variables. They are what a .NET host and an ODBC driver manager need to start
    /// and locate a driver; none of them is a credential, and everything absent from this list is dropped.
    /// </summary>
    public static readonly string[] InheritedVariables =
    [
        "PATH", "PATHEXT", "SystemRoot", "SystemDrive", "windir", "ComSpec", "TEMP", "TMP",
        "NUMBER_OF_PROCESSORS", "PROCESSOR_ARCHITECTURE", "OS",
        "ProgramData", "ProgramFiles", "ProgramFiles(x86)", "CommonProgramFiles", "CommonProgramFiles(x86)",
        "HOME", "LANG", "LC_ALL", "LD_LIBRARY_PATH",
        "DOTNET_ROOT", "DOTNET_ROOT(x86)", "DOTNET_HOST_PATH", "DOTNET_BUNDLE_EXTRACT_BASE_DIR",
        "ORACLE_HOME", "TNS_ADMIN", "NLS_LANG", "ODBCINI", "ODBCSYSINI",
    ];

    public async Task<GatewaySchemaRunOutcome> RunAsync(
        GatewaySourceEntry entry,
        OracleSchemaExtractionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(request);

        if (entry.OracleCredential is not { } credential)
        {
            return GatewaySchemaRunOutcome.Failed(
                "This source environment registers no Oracle connection, so no worker was started and no connection was opened.");
        }

        GatewayCredentialResolution resolution = _credentials.Resolve(credential.EnvironmentVariable);
        if (resolution.Value is not { Length: > 0 } connectionString)
        {
            // The NAME and the file PATH are safe to repeat; they are what an operator must fix. The value
            // is never read here, and a protected file that failed to decrypt does not fall back.
            return GatewaySchemaRunOutcome.Failed(resolution.Failure!);
        }

        ProcessStartInfo start = _launcher();
        start.ArgumentList.Add("--extract-schema");
        start.RedirectStandardInput = true;
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        start.UseShellExecute = false;
        start.CreateNoWindow = true;

        BuildEnvironment(start, entry, credential, connectionString, options.ExtractionTimeout);

        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(request, WorkerProtocol.Json);

        using Process? process = StartOrNull(start);
        if (process is null)
        {
            return GatewaySchemaRunOutcome.Failed("The gateway could not start a schema extraction worker.");
        }

        using CancellationTokenSource budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(options.ExtractionTimeout);

        Task<byte[]> stdout = GatewayStream.ReadBoundedAsync(
            process.StandardOutput.BaseStream, options.MaxSchemaWorkerOutputBytes, budget.Token);
        Task<byte[]> stderr = GatewayStream.ReadBoundedAsync(
            process.StandardError.BaseStream, DiagnosticByteCap, budget.Token);

        try
        {
            await using (Stream input = process.StandardInput.BaseStream)
            {
                await input.WriteAsync(payload, budget.Token).ConfigureAwait(false);
                await input.FlushAsync(budget.Token).ConfigureAwait(false);
            }

            await process.WaitForExitAsync(budget.Token).ConfigureAwait(false);
            byte[] output = await stdout.ConfigureAwait(false);
            await stderr.ConfigureAwait(false);

            if (output.Length is 0)
            {
                return new GatewaySchemaRunOutcome(null, process.ExitCode,
                    $"The schema extraction worker exited {process.ExitCode} without writing a result document.");
            }

            OracleSchemaExtractionResult? result =
                JsonSerializer.Deserialize<OracleSchemaExtractionResult>(output, WorkerProtocol.Json);

            return result is null
                ? new GatewaySchemaRunOutcome(null, process.ExitCode, "The schema extraction worker wrote an empty result document.")
                : new GatewaySchemaRunOutcome(result, process.ExitCode, null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            Terminate(process);
            return GatewaySchemaRunOutcome.Failed("The schema extraction worker exceeded the gateway's time budget and was terminated.");
        }
        catch (OperationCanceledException)
        {
            Terminate(process);
            throw;
        }
        catch (InvalidDataException)
        {
            Terminate(process);
            return GatewaySchemaRunOutcome.Failed("The schema extraction worker wrote more output than the gateway accepts and was terminated.");
        }
        catch (JsonException)
        {
            return GatewaySchemaRunOutcome.Failed("The schema extraction worker wrote a result document the gateway cannot read.");
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
            Terminate(process);
            return GatewaySchemaRunOutcome.Failed("The schema extraction worker closed its pipes before a result was exchanged.");
        }
    }

    /// <summary>Exposed so a test can assert what a child would receive without spawning one.</summary>
    public static void BuildEnvironment(
        ProcessStartInfo start,
        GatewaySourceEntry entry,
        GatewayOracleCredential credential,
        string connectionString,
        TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(start);
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(credential);

        Dictionary<string, string?> inherited = new(StringComparer.OrdinalIgnoreCase);
        foreach (string name in InheritedVariables)
        {
            if (start.Environment.TryGetValue(name, out string? value) && value is { Length: > 0 })
            {
                inherited[name] = value;
            }
        }

        start.Environment.Clear();
        foreach ((string name, string? value) in inherited)
        {
            start.Environment[name] = value;
        }

        string seconds = ((int)timeout.TotalSeconds).ToString(CultureInfo.InvariantCulture);
        start.Environment[OracleSourceConfiguration.ConnectionStringVariable] = connectionString;
        start.Environment[OracleSourceConfiguration.ProviderVariable] = credential.ProviderAlias;
        start.Environment[OracleSourceConfiguration.AllowlistVariable] = string.Join(',', entry.SchemaAllowlist);
        start.Environment[OracleSourceConfiguration.TimeoutVariable] = seconds;
        start.Environment[WorkerConfiguration.TimeoutVariable] = seconds;
    }

    private const int DiagnosticByteCap = 64 * 1024;

    private static Process? StartOrNull(ProcessStartInfo start)
    {
        try
        {
            return Process.Start(start);
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return null;
        }
    }

    private static void Terminate(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or NotSupportedException or
            System.ComponentModel.Win32Exception or AggregateException)
        {
        }
    }

    /// <summary>Non-secret description of how schema extraction is invoked, safe to log at startup.</summary>
    public const string Description =
        "Schema extraction runs as a child process of this executable with a rebuilt environment, under a kill-tree " +
        "time budget and a bounded result document.";
}
