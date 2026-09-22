// Copyright (c) Microsoft. All rights reserved.

using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OracleFormsMigrationFleet.Hosting;

public sealed record SourceWorkerOptions(string ExecutablePath, string ExpectedSha256)
{
    public const string PathVariable = "FORMS_SOURCE_WORKER_PATH";
    public const string ShaVariable = "FORMS_SOURCE_WORKER_SHA256";

    public static bool TryRead(Func<string, string?> configuration, out SourceWorkerOptions? options)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        options = null;
        string path = configuration(PathVariable)?.Trim() ?? string.Empty;
        string hash = configuration(ShaVariable)?.Trim().ToLowerInvariant() ?? string.Empty;
        if (path.Length is 0 or > 1024 || !Path.IsPathFullyQualified(path) ||
            hash.Length != 64 || hash.Any(value => !Uri.IsHexDigit(value)))
        {
            return false;
        }
        string normalized;
        try
        {
            normalized = Path.GetFullPath(path);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
        if (!File.Exists(normalized))
        {
            return false;
        }
        options = new(normalized, hash);
        return true;
    }
}

/// <summary>Strict, bounded parsing of the native source worker protocol; no worker text reaches the caller.</summary>
internal static class SourceWorkerProtocol
{
    internal const string ExpectedFormsRelease = "6.0.8.22.1";
    internal const string ExpectedArchitecture = "X86";
    internal const string ExpectedStatus = "BlockedPrerequisite";
    internal const int ExpectedExitCode = 2;
    internal const int MaxCapabilities = 32;
    internal const int MaxOutputBytes = 256 * 1024;
    internal const int MaxRequestBytes = 8 * 1024;
    private const int MaxIdLength = 64;
    private const int MaxStableIdentifierLength = 100;
    private const int MaxDescriptorLength = 64;

    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    internal static bool TryParse(
        string payload,
        string sourceEnvironmentId,
        int exitCode,
        out IReadOnlyList<SourceCapabilityResult> capabilities,
        out DateTimeOffset probedUtc)
    {
        capabilities = [];
        probedUtc = default;
        if (exitCode != ExpectedExitCode || string.IsNullOrEmpty(payload))
        {
            return false;
        }

        WorkerProbeResult? worker;
        try
        {
            worker = JsonSerializer.Deserialize<WorkerProbeResult>(payload, Json);
        }
        catch (JsonException)
        {
            return false;
        }
        if (worker is null || worker.SchemaVersion != 1 ||
            !string.Equals(worker.Status, ExpectedStatus, StringComparison.Ordinal) ||
            !string.Equals(worker.SourceEnvironmentId, sourceEnvironmentId, StringComparison.Ordinal) ||
            !string.Equals(worker.ProcessArchitecture, ExpectedArchitecture, StringComparison.Ordinal) ||
            !string.Equals(worker.ExpectedFormsRelease, ExpectedFormsRelease, StringComparison.Ordinal) ||
            worker.Capabilities is null || worker.Capabilities.Count == 0 ||
            worker.Capabilities.Count > MaxCapabilities)
        {
            return false;
        }

        List<SourceCapabilityResult> parsed = new(worker.Capabilities.Count);
        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (WorkerCapability? capability in worker.Capabilities)
        {
            if (capability is null || !IsStableIdentifier(capability.Id) || !seen.Add(capability.Id) ||
                !IsStableIdentifier(capability.Remediation) ||
                !IsOptional(capability.RequiredRelease) ||
                !IsOptional(capability.RequiredArchitecture) ||
                !IsOptional(capability.RequiredHost) ||
                !TryParseExact(capability.State, out SourceEnvironmentProbeStatus state) ||
                state is not SourceEnvironmentProbeStatus.BlockedPrerequisite and not SourceEnvironmentProbeStatus.Rejected ||
                !TryParseExact(capability.Prerequisite, out SourcePrerequisite prerequisite))
            {
                return false;
            }
            parsed.Add(new(
                capability.Id,
                state,
                prerequisite,
                capability.RequiredRelease,
                capability.RequiredArchitecture,
                capability.RequiredHost,
                capability.Remediation));
        }

            if (!HasRequiredManifest(parsed))
            {
                return false;
            }

        capabilities = parsed;
        probedUtc = worker.ProbedUtc;
        return true;
    }

    /// <summary>Accepts only the declared enum name; numeric, combined, and case variants are refused.</summary>
    internal static bool TryParseExact<TEnum>(string? value, out TEnum parsed) where TEnum : struct, Enum
    {
        parsed = default;
        return !string.IsNullOrEmpty(value) &&
            Enum.TryParse(value, ignoreCase: false, out parsed) &&
            Enum.IsDefined(parsed) &&
            string.Equals(Enum.GetName(parsed), value, StringComparison.Ordinal);
    }

    private static bool IsBounded(string? value, int maximum) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= maximum;

    private static bool IsStableIdentifier(string? value) =>
        IsBounded(value, MaxStableIdentifierLength) &&
        char.IsAsciiLetterOrDigit(value![0]) && !char.IsAsciiLetterUpper(value[0]) &&
        value[1..].All(character => char.IsAsciiDigit(character) || char.IsAsciiLetterLower(character) || character is '.' or '-');

    private static bool IsOptional(string? value) => value is null || IsBounded(value, MaxDescriptorLength);

    private static bool HasRequiredManifest(IReadOnlyList<SourceCapabilityResult> capabilities) =>
        capabilities.Count == 3 &&
        HasRequiredCapability(capabilities, "forms.installation", SourcePrerequisite.OracleFormsInstallation,
            "operator.install.forms.6i.worker") &&
        HasRequiredCapability(capabilities, "forms.openapi.load", SourcePrerequisite.OracleFormsOpenApiLibraries,
            "operator.supply.authorized.forms.libraries") &&
        HasRequiredCapability(capabilities, "forms.module.extract", SourcePrerequisite.OperatorSuppliedExport,
            "operator.supply.authorized.source.export");

    private static bool HasRequiredCapability(
        IReadOnlyList<SourceCapabilityResult> capabilities,
        string id,
        SourcePrerequisite prerequisite,
        string remediation) => capabilities.Any(capability =>
            capability.Id == id &&
            capability.State == SourceEnvironmentProbeStatus.BlockedPrerequisite &&
            capability.Prerequisite == prerequisite &&
            capability.RequiredRelease == ExpectedFormsRelease &&
            capability.RequiredArchitecture == "x86" &&
            capability.RequiredHost == "WindowsWorker" &&
            capability.Remediation == remediation);

    internal sealed record WorkerProbeRequest(int SchemaVersion, string SourceEnvironmentId, string ExpectedFormsRelease);

    private sealed record WorkerCapability(
        string Id,
        string State,
        string Prerequisite,
        string? RequiredRelease,
        string? RequiredArchitecture,
        string? RequiredHost,
        string Remediation);

    private sealed record WorkerProbeResult(
        int SchemaVersion,
        string SourceEnvironmentId,
        string Status,
        DateTimeOffset ProbedUtc,
        string OperatingSystem,
        string ProcessArchitecture,
        string ExpectedFormsRelease,
        IReadOnlyList<WorkerCapability?> Capabilities);
}

/// <summary>Verifies the staged worker is a bounded 32-bit Windows PE image before it is allowed to run.</summary>
internal static class SourceWorkerImage
{
    internal const long MaxImageBytes = 96L * 1024 * 1024;
    private const ushort DosSignature = 0x5A4D;
    private const uint PeSignature = 0x0000_4550;
    private const ushort MachineI386 = 0x014C;

    internal static bool IsAcceptableLength(long length) => length is > 0 and <= MaxImageBytes;

    internal static bool IsWindowsX86(ReadOnlySpan<byte> image)
    {
        if (image.Length < 0x40 || BinaryPrimitives.ReadUInt16LittleEndian(image) != DosSignature)
        {
            return false;
        }
        int offset = BinaryPrimitives.ReadInt32LittleEndian(image[0x3C..]);
        if (offset < 0x40 || offset > image.Length - 6)
        {
            return false;
        }
        return BinaryPrimitives.ReadUInt32LittleEndian(image[offset..]) == PeSignature &&
            BinaryPrimitives.ReadUInt16LittleEndian(image[(offset + 4)..]) == MachineI386;
    }
}

public sealed class ProcessSourceEnvironmentProbe(SourceWorkerOptions options) : ISourceEnvironmentProbe
{
    private static readonly TimeSpan s_timeout = TimeSpan.FromMinutes(2);

    public string Description => "Hash-pinned external Oracle Forms source worker";

    public async Task<SourceEnvironmentProbeResult> ProbeAsync(
        SourceEnvironmentProfile profile,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        cancellationToken.ThrowIfCancellationRequested();

        string root = Path.Combine(Path.GetTempPath(), $"ofm-source-worker-{Guid.NewGuid():N}");
        try
        {
            Staged staged = await StageAsync(root, cancellationToken).ConfigureAwait(false);
            return staged.Path is null
                ? Rejected(profile, staged.CapabilityId!, staged.Remediation!)
                : await RunAsync(profile, staged.Path, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Cleanup(root);
        }
    }

    /// <summary>Copies the verified image into a private directory so nothing can be swapped between hash and launch.</summary>
    private async Task<Staged> StageAsync(string root, CancellationToken cancellationToken)
    {
        byte[] header = new byte[4096];
        int headerLength;
        try
        {
            byte[] expected = Convert.FromHexString(options.ExpectedSha256);
            Directory.CreateDirectory(root);
            string path = Path.Combine(root, "source-worker.exe");
            byte[] streamed;
            await using (FileStream source = new(
                options.ExecutablePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true))
            {
                if (!SourceWorkerImage.IsAcceptableLength(source.Length))
                {
                    return Refused("forms.worker.integrity", "operator.install.reviewed.worker");
                }
                await using FileStream destination = new(
                    path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true);
                using IncrementalHash running = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                byte[] buffer = new byte[81920];
                long total = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    total += read;
                    if (!SourceWorkerImage.IsAcceptableLength(total))
                    {
                        return Refused("forms.worker.integrity", "operator.install.reviewed.worker");
                    }
                    running.AppendData(buffer, 0, read);
                    await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                }
                streamed = running.GetHashAndReset();
            }
            if (!CryptographicOperations.FixedTimeEquals(streamed, expected))
            {
                return Refused("forms.worker.integrity", "operator.install.reviewed.worker");
            }

            byte[] copied;
            await using (FileStream verify = new(
                path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true))
            {
                headerLength = await verify.ReadAtLeastAsync(
                    header, header.Length, throwOnEndOfStream: false, cancellationToken).ConfigureAwait(false);
                verify.Position = 0;
                copied = await SHA256.HashDataAsync(verify, cancellationToken).ConfigureAwait(false);
            }
            if (!CryptographicOperations.FixedTimeEquals(copied, expected))
            {
                return Refused("forms.worker.integrity", "operator.install.reviewed.worker");
            }
            if (!SourceWorkerImage.IsWindowsX86(header.AsSpan(0, headerLength)))
            {
                return Refused("forms.worker.image", "operator.provision.windows.x86.worker");
            }
            File.SetAttributes(path, FileAttributes.ReadOnly);
            return new(path, null, null);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException or FormatException)
        {
            return Refused("forms.worker.integrity", "operator.install.reviewed.worker");
        }
    }

    private static async Task<SourceEnvironmentProbeResult> RunAsync(
        SourceEnvironmentProfile profile,
        string staged,
        CancellationToken cancellationToken)
    {
        byte[] request = JsonSerializer.SerializeToUtf8Bytes(
            new SourceWorkerProtocol.WorkerProbeRequest(
                1, profile.SourceEnvironmentId, SourceWorkerProtocol.ExpectedFormsRelease),
            SourceWorkerProtocol.Json);
        if (request.Length > SourceWorkerProtocol.MaxRequestBytes)
        {
            return Rejected(profile, "forms.worker.request", "operator.inspect.worker.output");
        }

        ProcessStartInfo start = new(staged, ["--probe"])
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(staged)!,
        };
        ProcessApplicationBuildGateway.ApplyRestrictedEnvironment(start);
        start.Environment["DOTNET_BUNDLE_EXTRACT_BASE_DIR"] = Path.Combine(Path.GetDirectoryName(staged)!, "bundle");

        using CancellationTokenSource timeout = new(s_timeout);
        using CancellationTokenSource linked =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        using Process process = new() { StartInfo = start };
        try
        {
            if (!process.Start())
            {
                return Rejected(profile, "forms.worker.start", "operator.install.reviewed.worker");
            }
        }
        catch (Exception exception) when (
            exception is Win32Exception or InvalidOperationException or NotSupportedException or PlatformNotSupportedException)
        {
            return Rejected(profile, "forms.worker.start", "operator.install.reviewed.worker");
        }

        Task<Drain> output = DrainAsync(process.StandardOutput.BaseStream, linked.Token);
        Task<Drain> error = DrainAsync(process.StandardError.BaseStream, linked.Token);
        try
        {
            bool broken = false;
            try
            {
                await process.StandardInput.BaseStream.WriteAsync(request, linked.Token).ConfigureAwait(false);
                await process.StandardInput.BaseStream.FlushAsync(linked.Token).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or ObjectDisposedException)
            {
                broken = true;
            }
            finally
            {
                try
                {
                    process.StandardInput.Close();
                }
                catch (Exception exception) when (exception is IOException or ObjectDisposedException)
                {
                }
            }
            if (broken)
            {
                TryKill(process);
            }

            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
            Drain stdout = await output.ConfigureAwait(false);
            Drain stderr = await error.ConfigureAwait(false);
            string diagnostic = Diagnostic(stderr.Bytes);
            if (broken || stdout.Incomplete || stderr.Incomplete ||
                !SourceWorkerProtocol.TryParse(
                    Encoding.UTF8.GetString(stdout.Bytes),
                    profile.SourceEnvironmentId,
                    process.ExitCode,
                    out IReadOnlyList<SourceCapabilityResult> capabilities,
                    out DateTimeOffset probedUtc))
            {
                return Rejected(profile, "forms.worker.output", "operator.inspect.worker.output", diagnostic);
            }
            return Blocked(profile, capabilities, probedUtc);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Rejected(profile, "forms.worker.timeout", "operator.inspect.worker.timeout");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException)
        {
            return Rejected(profile, "forms.worker.output", "operator.inspect.worker.output");
        }
        finally
        {
            await linked.CancelAsync().ConfigureAwait(false);
            await StopAndObserveAsync(process, output, error).ConfigureAwait(false);
        }
    }

    private static SourceEnvironmentProbeResult Blocked(
        SourceEnvironmentProfile profile,
        IReadOnlyList<SourceCapabilityResult> capabilities,
        DateTimeOffset probedUtc)
    {
        SourcePrerequisite[] blocked = [.. capabilities
            .Where(capability => capability.State == SourceEnvironmentProbeStatus.BlockedPrerequisite)
            .Select(capability => capability.Prerequisite)
            .Distinct()];
        return new(
            1,
            profile.SourceEnvironmentId,
            profile.Version,
            profile.CanonicalHash,
            blocked.Length > 0 ? SourceEnvironmentProbeStatus.BlockedPrerequisite : SourceEnvironmentProbeStatus.Rejected,
            probedUtc,
            profile.Connector,
            new(profile.ExpectedFormsVersion, profile.ExpectedDatabaseVersion),
            new(null, null),
            capabilities,
            blocked,
            blocked.Length > 0 ? [] : ["The native source worker returned no verified capability and no prerequisite."]);
    }

    private static SourceEnvironmentProbeResult Rejected(
        SourceEnvironmentProfile profile,
        string capabilityId,
        string remediation,
        string? diagnostic = null) => new(
            1,
            profile.SourceEnvironmentId,
            profile.Version,
            profile.CanonicalHash,
            SourceEnvironmentProbeStatus.Rejected,
            DateTimeOffset.UtcNow,
            profile.Connector,
            new(profile.ExpectedFormsVersion, profile.ExpectedDatabaseVersion),
            new(null, null),
            [new(capabilityId, SourceEnvironmentProbeStatus.Rejected, SourcePrerequisite.SourceWorkerExecutable,
                "6.0.8.22.1", "x86", "WindowsWorker", remediation)],
            [],
            diagnostic is null
                ? ["The configured native source worker was refused."]
                : ["The configured native source worker was refused.", $"Worker diagnostic {diagnostic}."]);

    /// <summary>Drains to end of stream even after the budget is spent so the worker never blocks on a full pipe.</summary>
    private static async Task<Drain> DrainAsync(Stream stream, CancellationToken cancellationToken)
    {
        using MemoryStream retained = new();
        byte[] buffer = new byte[8192];
        bool incomplete = false;
        try
        {
            int read;
            while ((read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                int room = SourceWorkerProtocol.MaxOutputBytes - (int)retained.Length;
                if (read > room)
                {
                    incomplete = true;
                    read = room;
                }
                if (read > 0)
                {
                    retained.Write(buffer, 0, read);
                }
            }
        }
        catch (Exception exception) when (
            exception is IOException or ObjectDisposedException or OperationCanceledException)
        {
            incomplete = true;
        }
        return new(retained.ToArray(), incomplete);
    }

    /// <summary>Worker error text is never surfaced; only a stable identifier for correlating an operator report.</summary>
    private static string Diagnostic(byte[] error) =>
        error.Length == 0 ? "none" : Convert.ToHexStringLower(SHA256.HashData(error))[..16];

    private static Staged Refused(string capabilityId, string remediation) => new(null, capabilityId, remediation);

    private static void Cleanup(string root)
    {
        try
        {
            if (!Directory.Exists(root))
            {
                return;
            }
            foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }
            Directory.Delete(root, recursive: true);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or Win32Exception or NotSupportedException)
        {
        }
    }

    private static async Task StopAndObserveAsync(Process process, Task<Drain> output, Task<Drain> error)
    {
        TryKill(process);
        try
        {
            using CancellationTokenSource grace = new(TimeSpan.FromSeconds(5));
            await process.WaitForExitAsync(grace.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or NotSupportedException or OperationCanceledException)
        {
        }
        Task drains = Task.WhenAll(output, error);
        try
        {
            await drains.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            _ = drains.ContinueWith(
                static completed => _ = completed.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }

    private sealed record Staged(string? Path, string? CapabilityId, string? Remediation);

    private sealed record Drain(byte[] Bytes, bool Incomplete);
}