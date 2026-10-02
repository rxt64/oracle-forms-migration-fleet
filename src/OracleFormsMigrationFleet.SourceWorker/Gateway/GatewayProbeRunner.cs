using System.Diagnostics;
using System.Text.Json;

namespace OracleFormsMigrationFleet.SourceWorker.Gateway;

/// <summary>What one probe invocation produced, or the reason there is no result to read.</summary>
public sealed record GatewayProbeRunOutcome(WorkerProbeResult? Result, int? ExitCode, string? Failure)
{
    public static GatewayProbeRunOutcome Failed(string failure) => new(null, null, failure);
}

/// <summary>
/// Runs one bounded <c>--probe</c>. Separate from the coordinator for the same reason the extraction
/// runner is: the admission rules have to be testable without spawning anything, and the spawn has to be
/// testable on its own.
/// </summary>
public interface IGatewayProbeRunner
{
    Task<GatewayProbeRunOutcome> RunAsync(
        GatewaySourceEntry entry,
        WorkerProbeRequest request,
        CancellationToken cancellationToken);
}

/// <summary>
/// Runs the worker's existing <c>--probe</c> mode in a CHILD PROCESS.
///
/// The probe loads the same 32-bit native library an extraction would, so it gets the same isolation: a
/// library that hangs or faults costs one killed child and one failed request rather than the listener.
/// The child is this same executable, launched through <see cref="ChildProcessExtractionRunner"/>'s own
/// launcher and environment builder so there is exactly one definition of what a child worker inherits.
/// No connect string is on that list, and a probe opens no database, so none is needed.
/// </summary>
public sealed class ChildProcessProbeRunner(
    GatewayOptions options,
    Func<ProcessStartInfo>? launcher = null) : IGatewayProbeRunner
{
    private const int DiagnosticByteCap = 64 * 1024;

    private readonly Func<ProcessStartInfo> _launcher = launcher ?? ChildProcessExtractionRunner.SelfLauncher;

    public async Task<GatewayProbeRunOutcome> RunAsync(
        GatewaySourceEntry entry,
        WorkerProbeRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(request);

        ProcessStartInfo start = _launcher();
        start.ArgumentList.Add("--probe");
        start.RedirectStandardInput = true;
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        start.UseShellExecute = false;
        start.CreateNoWindow = true;

        ChildProcessExtractionRunner.BuildEnvironment(start, entry, options.ExtractionTimeout);

        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(request, WorkerProtocol.Json);

        Process? started;
        try
        {
            started = Process.Start(start);
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            started = null;
        }

        if (started is null)
        {
            return GatewayProbeRunOutcome.Failed("The gateway could not start a probe worker.");
        }

        using Process process = started;
        using CancellationTokenSource budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(options.ExtractionTimeout);

        Task<byte[]> stdout = GatewayStream.ReadBoundedAsync(
            process.StandardOutput.BaseStream, options.MaxWorkerOutputBytes, budget.Token);
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
                return new GatewayProbeRunOutcome(null, process.ExitCode,
                    $"The probe worker exited {process.ExitCode} without writing a result document.");
            }

            WorkerProbeResult? result = JsonSerializer.Deserialize<WorkerProbeResult>(output, WorkerProtocol.Json);
            return result is null
                ? new GatewayProbeRunOutcome(null, process.ExitCode, "The probe worker wrote an empty result document.")
                : new GatewayProbeRunOutcome(result, process.ExitCode, null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            Terminate(process);
            return GatewayProbeRunOutcome.Failed("The probe worker exceeded the gateway's time budget and was terminated.");
        }
        catch (OperationCanceledException)
        {
            Terminate(process);
            throw;
        }
        catch (InvalidDataException)
        {
            Terminate(process);
            return GatewayProbeRunOutcome.Failed("The probe worker wrote more output than the gateway accepts and was terminated.");
        }
        catch (JsonException)
        {
            return GatewayProbeRunOutcome.Failed("The probe worker wrote a result document the gateway cannot read.");
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
            Terminate(process);
            return GatewayProbeRunOutcome.Failed("The probe worker closed its pipes before a result was exchanged.");
        }
    }

    /// <summary>Kills the whole tree: the native API may have started helpers that would outlive the request.</summary>
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
}
