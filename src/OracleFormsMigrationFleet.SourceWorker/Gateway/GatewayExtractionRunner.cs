using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace OracleFormsMigrationFleet.SourceWorker.Gateway;

/// <summary>What one worker invocation produced, or the reason there is no result to read.</summary>
public sealed record GatewayRunOutcome(WorkerExtractionResult? Result, int? ExitCode, string? Failure)
{
    public static GatewayRunOutcome Failed(string failure) => new(null, null, failure);
}

/// <summary>
/// Runs one bounded <c>--extract</c>. Separate from the coordinator so the orchestration can be tested
/// without spawning anything, and so the real spawn can be tested on its own.
/// </summary>
public interface IGatewayExtractionRunner
{
    Task<GatewayRunOutcome> RunAsync(
        GatewaySourceEntry entry,
        WorkerExtractionRequest request,
        CancellationToken cancellationToken);
}

/// <summary>
/// Runs the worker in a CHILD PROCESS, never on a request thread.
///
/// This is not an implementation preference. The Forms API is a 32-bit native library that can hang, fault
/// or leak inside the process that loaded it, and a hang on a request thread would take the listener down
/// with it. Isolating it means a stuck extraction costs one killed child and one failed request.
///
/// The child is always this same executable. No command line, library name or path comes from a caller or
/// from configuration, so there is nothing to inject into: the only thing the caller influences is the
/// content of the JSON request written to the child's standard input, and the worker validates that
/// against its own configuration before it opens anything.
///
/// The child's environment is BUILT, not inherited, for the same reason it is on the schema runner beside
/// this one. <see cref="ProcessStartInfo.Environment"/> starts as a copy of the gateway's own, which on a
/// real host carries every registered source's <c>OFM_GATEWAY_ORACLE_*</c> connect string and whatever
/// else the platform put there. Removing the six worker variables left the rest in place, so a Forms
/// extraction — which never opens a database and needs no credential at all — ran with the credentials of
/// every source in the registry in reach of a native library that can execute module code. The environment
/// is cleared and repopulated from a fixed operating-system and runtime list plus the worker settings this
/// one registry entry authorizes.
/// </summary>
public sealed class ChildProcessExtractionRunner(
    GatewayOptions options,
    Func<ProcessStartInfo>? launcher = null) : IGatewayExtractionRunner
{
    private readonly Func<ProcessStartInfo> _launcher = launcher ?? SelfLauncher;

    public async Task<GatewayRunOutcome> RunAsync(
        GatewaySourceEntry entry,
        WorkerExtractionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(request);

        ProcessStartInfo start = _launcher();
        start.ArgumentList.Add("--extract");
        start.RedirectStandardInput = true;
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        start.UseShellExecute = false;
        start.CreateNoWindow = true;

        BuildEnvironment(start, entry, options.ExtractionTimeout);

        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(request, WorkerProtocol.Json);

        using Process? process = StartOrNull(start, out string? startFailure);
        if (process is null)
        {
            return GatewayRunOutcome.Failed(startFailure!);
        }

        using CancellationTokenSource budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(options.ExtractionTimeout);

        Task<byte[]> stdout = GatewayStream.ReadBoundedAsync(process.StandardOutput.BaseStream, options.MaxWorkerOutputBytes, budget.Token);
        Task<byte[]> stderr = GatewayStream.ReadBoundedAsync(process.StandardError.BaseStream, DiagnosticByteCap, budget.Token);

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
                return new GatewayRunOutcome(null, process.ExitCode,
                    $"The extraction worker exited {process.ExitCode} without writing a result document.");
            }

            WorkerExtractionResult? result =
                JsonSerializer.Deserialize<WorkerExtractionResult>(output, WorkerProtocol.Json);

            return result is null
                ? new GatewayRunOutcome(null, process.ExitCode, "The extraction worker wrote an empty result document.")
                : new GatewayRunOutcome(result, process.ExitCode, null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            Terminate(process);
            return GatewayRunOutcome.Failed("The extraction worker exceeded the gateway's time budget and was terminated.");
        }
        catch (OperationCanceledException)
        {
            Terminate(process);
            throw;
        }
        catch (InvalidDataException)
        {
            Terminate(process);
            return GatewayRunOutcome.Failed("The extraction worker wrote more output than the gateway accepts and was terminated.");
        }
        catch (Exception exception) when (exception is JsonException)
        {
            return GatewayRunOutcome.Failed("The extraction worker wrote a result document the gateway cannot read.");
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
            Terminate(process);
            return GatewayRunOutcome.Failed("The extraction worker closed its pipes before a result was exchanged.");
        }
    }

    /// <summary>Standard error is drained so the child cannot block on a full pipe; the bytes are not reported.</summary>
    private const int DiagnosticByteCap = 64 * 1024;

    /// <summary>
    /// The only inherited variables. They are what a .NET host needs to start and what the Windows loader
    /// and the native Forms runtime read once it has: <c>ifd2f60.dll</c> is loaded with
    /// <c>LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_SYSTEM32</c>, so the system root is what
    /// resolves its system dependencies, and the Forms home comes from the registry entry rather than from
    /// here. No connection variable is on this list, and neither is anything that carries a credential:
    /// module extraction reads a file and passes the load-from-database flag as zero, so it has no use for
    /// one. Everything absent is dropped.
    /// </summary>
    public static readonly string[] InheritedVariables =
    [
        "PATH", "PATHEXT", "SystemRoot", "SystemDrive", "windir", "ComSpec", "TEMP", "TMP",
        "NUMBER_OF_PROCESSORS", "PROCESSOR_ARCHITECTURE", "OS",
        "ProgramData", "ProgramFiles", "ProgramFiles(x86)", "CommonProgramFiles", "CommonProgramFiles(x86)",
        "HOME", "LANG", "LC_ALL", "LD_LIBRARY_PATH",
        "DOTNET_ROOT", "DOTNET_ROOT(x86)", "DOTNET_HOST_PATH", "DOTNET_BUNDLE_EXTRACT_BASE_DIR",
        "NLS_LANG",
    ];

    /// <summary>
    /// Replaces the child's inherited environment with the operating-system and runtime essentials plus the
    /// worker settings <paramref name="entry"/> authorizes. Exposed so a test can assert what a child would
    /// receive without spawning one.
    /// </summary>
    public static void BuildEnvironment(ProcessStartInfo start, GatewaySourceEntry entry, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(start);
        ArgumentNullException.ThrowIfNull(entry);

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

        start.Environment[WorkerConfiguration.InputRootVariable] = entry.InputRoot;
        start.Environment[WorkerConfiguration.OutputRootVariable] = entry.OutputRoot;
        start.Environment[WorkerConfiguration.FormsHomeVariable] = entry.FormsHome;
        start.Environment[WorkerConfiguration.LibraryHashVariable] = entry.ApprovedLibrarySha256;
        start.Environment[WorkerConfiguration.LibraryVersionVariable] = entry.ApprovedLibraryFileVersion;
        start.Environment[WorkerConfiguration.TimeoutVariable] =
            ((int)timeout.TotalSeconds).ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// The child is this executable. A framework-dependent build is launched through the same host that is
    /// already running, so the bitness the operator installed for the 32-bit Forms API is preserved.
    /// </summary>
    public static ProcessStartInfo SelfLauncher()
    {
        string? host = Environment.ProcessPath;
        string assembly = Environment.GetCommandLineArgs()[0];

        if (host is null)
        {
            throw new InvalidOperationException("The gateway could not determine its own executable path.");
        }

        ProcessStartInfo start = new(host);
        string hostName = Path.GetFileNameWithoutExtension(host);
        if (string.Equals(hostName, "dotnet", StringComparison.OrdinalIgnoreCase) &&
            assembly.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
        {
            start.ArgumentList.Add(assembly);
        }

        start.WorkingDirectory = AppContext.BaseDirectory;
        return start;
    }

    private static Process? StartOrNull(ProcessStartInfo start, out string? failure)
    {
        try
        {
            Process? process = Process.Start(start);
            if (process is null)
            {
                failure = "The gateway could not start an extraction worker.";
                return null;
            }

            failure = null;
            return process;
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            failure = "The gateway could not start an extraction worker.";
            return null;
        }
    }

    /// <summary>
    /// Kills the whole tree. The native API may have started helpers of its own, and leaving them behind
    /// would let a timed-out extraction keep holding the module and the library after the request failed.
    /// </summary>
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

    /// <summary>Non-secret description of how extraction is invoked, safe to log at startup.</summary>
    public const string Description =
        "Extraction runs as a child process of this executable with a rebuilt environment, under a kill-tree " +
        "time budget with a bounded result document.";
}

/// <summary>
/// Reads at most <c>limit</c> bytes and throws when there are more, so neither a child process nor a
/// caller can stream this server out of memory by declaring no length or lying about it.
/// </summary>
internal static class GatewayStream
{
    public static async Task<byte[]> ReadBoundedAsync(Stream stream, int limit, CancellationToken cancellationToken)
    {
        using MemoryStream buffer = new();
        byte[] chunk = new byte[16 * 1024];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > limit)
            {
                throw new InvalidDataException("The stream carried more than the configured limit.");
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }
}
