// Copyright (c) Microsoft. All rights reserved.

using System.Data.Common;
using System.Data.Odbc;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using OracleFormsMigrationFleet.SourceWorker.Gateway;

namespace OracleFormsMigrationFleet.SourceWorker;

internal static class Program
{
    private const int MaxRequestBytes = 8 * 1024;

    public static async Task<int> Main(string[] args)
    {
        // The gateway listens until it is stopped, so it is routed before the per-invocation time budget
        // below, which exists to bound one --probe or --extract and would otherwise shut the server down.
        if (args is ["--serve"])
        {
            return await ServeAsync();
        }

        WorkerConfiguration configuration = WorkerConfiguration.FromEnvironment(Environment.GetEnvironmentVariable);

        using CancellationTokenSource cancellation = new(configuration.Timeout);
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };

        try
        {
            return args switch
            {
                ["--probe"] => await ProbeAsync(configuration, cancellation.Token),
                ["--extract"] => await ExtractAsync(configuration, cancellation.Token),
                ["--extract-schema"] => await ExtractSchemaAsync(cancellation.Token),
                _ => await UsageAsync(),
            };
        }
        catch (OperationCanceledException)
        {
            await Console.Error.WriteLineAsync("The worker was cancelled or exceeded its configured time budget.");
            return WorkerExit.Cancelled;
        }
    }

    /// <summary>
    /// Runs the source gateway. Configuration comes from this host's own environment; a host that cannot
    /// state its tenant, audience, allowed callers and source registry does not listen at all, because a
    /// gateway that guesses any of those cannot say what an operator approved.
    /// </summary>
    private static async Task<int> ServeAsync()
    {
        if (!GatewayOptions.TryRead(Environment.GetEnvironmentVariable, ReadRegistry, out GatewayOptions? options, out IReadOnlyList<string> errors))
        {
            await Console.Error.WriteLineAsync("The source gateway is not configured and did not start:");
            foreach (string error in errors)
            {
                await Console.Error.WriteLineAsync($"  {error}");
            }

            return WorkerExit.UsageRejected;
        }

        await using WebApplication app = WorkerGatewayHost.Build(options!);
        await app.RunAsync();
        return WorkerExit.Success;
    }

    private static string? ReadRegistry(string path)
    {
        try
        {
            return File.ReadAllText(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    private static async Task<int> UsageAsync()
    {
        await Console.Error.WriteLineAsync("This worker accepts --probe, --extract or --extract-schema, each reading one JSON request from standard input, or --serve to run the source gateway.");
        return WorkerExit.UsageRejected;
    }

    /// <summary>
    /// Reads the Oracle catalog for the schemas the request names, bounded by this process's own settings.
    ///
    /// The connect string arrives only in this process's environment, is handed straight to
    /// <see cref="OracleSourceConfiguration"/>, and is never a property, a log line or a protocol field.
    /// The request may narrow the configured allowlist and can never widen it, so the worst a caller can
    /// reach is a subset of what the operator who provisioned this host already approved.
    /// </summary>
    private static async Task<int> ExtractSchemaAsync(CancellationToken cancellationToken)
    {
        (OracleSchemaExtractionRequest? request, int failure) = await ReadAsync<OracleSchemaExtractionRequest>(cancellationToken);
        if (request is null)
        {
            return failure;
        }

        OracleSchemaExtractionService service = new(OracleConnections());

        OracleSchemaExtractionResult result;
        try
        {
            result = await service.ExtractAsync(request, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // A client the host cannot load at all (no ODBC driver manager, a bitness mismatch) faults
            // outside the provider's own exception hierarchy. Exiting on the exception would leave the
            // gateway reading an empty pipe, so the fault becomes the typed refusal it actually is. The
            // exception type is named; its message is not echoed, because a client can quote its DSN.
            result = new OracleSchemaExtractionResult(
                OracleSchemaProtocol.SchemaVersion,
                request.SourceEnvironmentId,
                request.ProfileVersion,
                request.ProfileHash,
                request.SchemaAllowlist,
                CapabilityState.BlockedPrerequisite,
                DateTimeOffset.UtcNow,
                null,
                null,
                [new WorkerCapability("oracle.source.connect", CapabilityState.BlockedPrerequisite, "OracleSourceClient",
                    null, null, "SourceGatewayHost", "operator.install.oracle.odbc.client.on.worker")],
                [$"The Oracle client could not be used on this worker ({exception.GetType().Name}); no catalog was read."]);
        }

        await WriteAsync(result, cancellationToken);

        return result.Status switch
        {
            "Extracted" => WorkerExit.Success,
            CapabilityState.BlockedPrerequisite => WorkerExit.BlockedPrerequisite,
            CapabilityState.Rejected => WorkerExit.RequestRejected,
            _ => WorkerExit.ExtractionFailed,
        };
    }

    /// <summary>
    /// The composed production factory. ODBC is the client because that is what an 8.0.6-era Oracle host
    /// exposes, and it binds positionally, which the catalog reader is told rather than left to guess.
    /// </summary>
    private static ConfiguredOracleConnectionFactory OracleConnections() =>
        new(OracleSourceConfiguration.FromEnvironment(Environment.GetEnvironmentVariable),
            connectionString => (DbConnection)new OdbcConnection(connectionString),
            OracleParameterStyle.Positional);

    private static async Task<int> ProbeAsync(WorkerConfiguration configuration, CancellationToken cancellationToken)
    {
        (WorkerProbeRequest? request, int failure) = await ReadAsync<WorkerProbeRequest>(cancellationToken);
        if (request is null)
        {
            return failure;
        }

        if (request.SchemaVersion != WorkerProtocol.SchemaVersion ||
            !IsIdentifier(request.ExpectedFormsRelease, 40) ||
            !IsIdentifier(request.SourceEnvironmentId, 63))
        {
            await Console.Error.WriteLineAsync("The worker probe request was rejected.");
            return WorkerExit.RequestRejected;
        }

        List<WorkerCapability> capabilities = [];
        bool hostUsable = OperatingSystem.IsWindows() && RuntimeInformation.ProcessArchitecture == Architecture.X86;

        if (!OperatingSystem.IsWindows())
        {
            capabilities.Add(Blocked(request, "forms.worker.os", "WorkerHostArchitecture", null,
                "operator.provision.windows.x86.worker"));
        }

        if (RuntimeInformation.ProcessArchitecture != Architecture.X86)
        {
            capabilities.Add(Blocked(request, "forms.worker.architecture", "WorkerHostArchitecture", "x86",
                "operator.provision.windows.x86.worker"));
        }

        string status = CapabilityState.BlockedPrerequisite;

        if (!configuration.NativeProviderConfigured || !hostUsable)
        {
            // The worker does not inspect arbitrary paths or PATH. The native provider receives a configured
            // home alias and an approved binary digest from its own service wrapper. With neither supplied,
            // no library is loaded and the worker cannot claim the native libraries are present.
            capabilities.Add(Blocked(request, "forms.installation", "OracleFormsInstallation", "x86",
                "operator.install.forms.6i.worker"));
            capabilities.Add(Blocked(request, "forms.openapi.load", "OracleFormsOpenApiLibraries", "x86",
                "operator.supply.authorized.forms.libraries"));
            capabilities.Add(Blocked(request, "forms.module.extract", "OperatorSuppliedExport", "x86",
                "operator.supply.authorized.source.export"));
        }
        else
        {
            // A provider is configured, so the load path is exercised for real and reported by result.
            // Extraction is deliberately absent from this manifest: the probe opens no module, and a
            // capability nothing tested does not belong in a report an operator reads as tested.
            NativeProviderOpen open = await new D2FNativeFormsProvider().OpenAsync(configuration, cancellationToken);
            if (open.Reader is null)
            {
                capabilities.Add(Blocked(request, "forms.openapi.load", open.Prerequisite!, "x86", open.Remediation!));
                await Console.Error.WriteLineAsync(open.Detail);
            }
            else
            {
                using (open.Reader)
                {
                    capabilities.Add(new WorkerCapability("forms.installation", CapabilityState.Verified,
                        "OracleFormsInstallation", request.ExpectedFormsRelease, "x86", "WindowsWorker", "none",
                        $"fileVersion={open.Evidence!.FileVersion ?? "unreported"};productVersion={open.Evidence.ProductVersion ?? "unreported"}"));
                    capabilities.Add(new WorkerCapability("forms.openapi.load", CapabilityState.Verified,
                        "OracleFormsOpenApiLibraries", request.ExpectedFormsRelease, "x86", "WindowsWorker", "none",
                        $"library={open.Evidence.LibrarySha256[..16]};exports={string.Join(',', open.Evidence.ResolvedExports)}"));
                }

                status = "NativeProviderVerified";
            }
        }

        WorkerProbeResult result = new(
            WorkerProtocol.SchemaVersion,
            request.SourceEnvironmentId,
            status,
            DateTimeOffset.UtcNow,
            RuntimeInformation.OSDescription,
            RuntimeInformation.ProcessArchitecture.ToString(),
            request.ExpectedFormsRelease,
            capabilities);

        await WriteAsync(result, cancellationToken);
        return status == CapabilityState.BlockedPrerequisite ? WorkerExit.BlockedPrerequisite : WorkerExit.Success;
    }

    private static async Task<int> ExtractAsync(WorkerConfiguration configuration, CancellationToken cancellationToken)
    {
        (WorkerExtractionRequest? request, int failure) = await ReadAsync<WorkerExtractionRequest>(cancellationToken);
        if (request is null)
        {
            return failure;
        }

        if (request.SchemaVersion != WorkerProtocol.SchemaVersion ||
            !IsIdentifier(request.ExpectedFormsRelease, 40) ||
            !IsIdentifier(request.SourceEnvironmentId, 63) ||
            request.ProfileVersion <= 0 ||
            !ContentHash.IsSha256(request.ProfileHash) ||
            !TrustedInput.IsWellFormedAlias(request.ModuleAlias) ||
            !ContentHash.IsSha256(request.ExpectedContentSha256))
        {
            await Console.Error.WriteLineAsync("The worker extraction request was rejected.");
            return WorkerExit.RequestRejected;
        }

        ExtractionService service = new(configuration, new D2FNativeFormsProvider());
        WorkerExtractionResult result = await service.ExtractAsync(request, cancellationToken);
        await WriteAsync(result, cancellationToken);

        return result.Status switch
        {
            "Extracted" => WorkerExit.Success,
            CapabilityState.BlockedPrerequisite => WorkerExit.BlockedPrerequisite,
            CapabilityState.Rejected => WorkerExit.RequestRejected,
            _ => WorkerExit.ExtractionFailed,
        };
    }

    private static async Task<(TRequest? Request, int Failure)> ReadAsync<TRequest>(CancellationToken cancellationToken)
        where TRequest : class
    {
        try
        {
            await using Stream input = Console.OpenStandardInput();
            using MemoryStream retained = new(MaxRequestBytes);
            byte[] buffer = new byte[1024];
            int total = 0;
            int read;
            while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
            {
                total += read;
                if (total > MaxRequestBytes)
                {
                    await Console.Error.WriteLineAsync("The worker request exceeded its byte limit.");
                    return (null, WorkerExit.RequestRejected);
                }

                retained.Write(buffer, 0, read);
            }

            retained.Position = 0;
            TRequest? request = await JsonSerializer.DeserializeAsync<TRequest>(retained, WorkerProtocol.Json, cancellationToken);
            if (request is null)
            {
                await Console.Error.WriteLineAsync("The worker request was malformed.");
                return (null, WorkerExit.RequestRejected);
            }

            return (request, 0);
        }
        catch (Exception exception) when (exception is JsonException or IOException)
        {
            await Console.Error.WriteLineAsync("The worker request was malformed.");
            return (null, WorkerExit.RequestRejected);
        }
    }

    private static async Task WriteAsync<TResult>(TResult result, CancellationToken cancellationToken) =>
        await JsonSerializer.SerializeAsync(Console.OpenStandardOutput(), result, WorkerProtocol.Json, cancellationToken);

    /// <summary>
    /// Identifiers reach log lines and derived file names, so they are restricted to characters that cannot
    /// carry a separator or a terminal escape rather than merely being length-checked.
    /// </summary>
    private static bool IsIdentifier(string? value, int maximumLength) =>
        value is { Length: > 0 } && value.Length <= maximumLength &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-');

    private static WorkerCapability Blocked(
        WorkerProbeRequest request,
        string id,
        string prerequisite,
        string? requiredArchitecture,
        string remediation) =>
        new(id, CapabilityState.BlockedPrerequisite, prerequisite, request.ExpectedFormsRelease,
            requiredArchitecture, "WindowsWorker", remediation);
}