// Copyright (c) Microsoft. All rights reserved.

using System.Data.Common;
using System.Data.Odbc;
using System.Runtime.InteropServices;
using System.Text;
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

        if (args is ["--help"] or ["-h"] or ["/?"])
        {
            await UsageAsync(Console.Out);
            return WorkerExit.Success;
        }

        // Provisioning a protected credential is an operator action on the gateway host, not a request.
        // The value is read from standard input so it never reaches a command line, a log or this
        // process's own environment, and it is written back only as a DPAPI blob.
        if (args is ["--protect-credential", var variableName])
        {
            return await ProtectCredentialAsync(variableName);
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
                _ => await UsageRejectedAsync(),
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
    ///
    /// Under the Service Control Manager this same path is the service entry point: the host registers a
    /// Windows service lifetime for <see cref="GatewayOptions.ServiceName"/> and pins its content root to
    /// the installed directory, so nothing about running unattended depends on the working directory the
    /// SCM happens to supply.
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

        try
        {
            GatewayHostBuild build = WorkerGatewayHost.TryBuild(options!);
            if (build.Application is null)
            {
                await Console.Error.WriteLineAsync($"The source gateway did not start: {build.Failure}");
                return WorkerExit.UsageRejected;
            }

            await using WebApplication app = build.Application;
            await app.RunAsync();
            return WorkerExit.Success;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await Console.Error.WriteLineAsync(
                $"The source gateway could not start or remain listening ({exception.GetType().Name}).");
            return WorkerExit.ExtractionFailed;
        }
    }

    /// <summary>
    /// Encrypts one Oracle connect string to a file only this account can decrypt.
    ///
    /// The value arrives on standard input and is never echoed, never logged and never placed in an
    /// argument. The destination is derived from the variable name beneath the configured protected
    /// credential root, so this utility cannot be pointed at an arbitrary path either.
    /// </summary>
    private static async Task<int> ProtectCredentialAsync(string variableName)
    {
        string? root = Environment.GetEnvironmentVariable(GatewayOptions.CredentialRootVariable)?.Trim();
        if (string.IsNullOrEmpty(root) || !Path.IsPathFullyQualified(root))
        {
            await Console.Error.WriteLineAsync(
                $"Set {GatewayOptions.CredentialRootVariable} to the fully qualified protected credential directory first.");
            return WorkerExit.UsageRejected;
        }

        string? protectedRoot = GatewayText.Directory(root);
        if (protectedRoot is null || !GatewayCredentialProvider.IsExistingUnlinkedDirectory(protectedRoot))
        {
            await Console.Error.WriteLineAsync(
                $"The directory named by {GatewayOptions.CredentialRootVariable} must already exist, must not be a " +
                "filesystem link, and must be ACLed for the gateway service account; nothing was written.");
            return WorkerExit.UsageRejected;
        }

        GatewayCredentialProvider provider = new(protectedRoot);
        if (provider.ProtectedPathFor(variableName) is not { } destination)
        {
            await Console.Error.WriteLineAsync(
                $"The variable name must begin '{GatewayOracleCredential.RequiredPrefix}' and contain only A-Z, 0-9 and underscore.");
            return WorkerExit.UsageRejected;
        }

        string? value = Console.IsInputRedirected
            ? await Console.In.ReadLineAsync()
            : ReadCredentialWithoutEcho();
        if (string.IsNullOrWhiteSpace(value))
        {
            await Console.Error.WriteLineAsync("No connect string was supplied on standard input; nothing was written.");
            return WorkerExit.UsageRejected;
        }

        try
        {
            byte[] blob = GatewayCredentialProvider.Protect(variableName, value);
            await File.WriteAllBytesAsync(destination, blob);
        }
        catch (Exception exception) when (exception is PlatformNotSupportedException or ArgumentException or
            IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
        {
            // The exception type is named; its message is not echoed, because a failing DPAPI or file
            // layer can quote the value it was handed.
            await Console.Error.WriteLineAsync(
                $"The protected credential file could not be written ({exception.GetType().Name}).");
            return WorkerExit.ExtractionFailed;
        }

        await Console.Out.WriteLineAsync(
            $"Wrote a DPAPI-protected credential for {variableName}. Only the account that ran this command can decrypt it, " +
            $"so run it as the {GatewayOptions.ServiceName} service account.");
        return WorkerExit.Success;
    }

    private static string? ReadCredentialWithoutEcho()
    {
        StringBuilder value = new();
        while (value.Length <= GatewayCredentialProvider.MaxCredentialCharacters)
        {
            ConsoleKeyInfo key = Console.ReadKey(intercept: true);
            if (key.Key is ConsoleKey.Enter)
            {
                return value.ToString();
            }

            if (key.Key is ConsoleKey.Backspace)
            {
                if (value.Length > 0)
                {
                    value.Length--;
                }

                continue;
            }

            if (key.Key is ConsoleKey.C && key.Modifiers.HasFlag(ConsoleModifiers.Control))
            {
                return null;
            }

            if (!char.IsControl(key.KeyChar))
            {
                value.Append(key.KeyChar);
            }
        }

        return null;
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

    private static async Task<int> UsageRejectedAsync()
    {
        await UsageAsync(Console.Error);
        return WorkerExit.UsageRejected;
    }

    private static async Task UsageAsync(TextWriter writer)
    {
        await writer.WriteLineAsync($"""
            OracleFormsMigrationFleet.SourceWorker

              --probe                          Report this host's Forms capability. Reads one JSON request from stdin.
              --extract                        Extract one Forms module to neutral IR. Reads one JSON request from stdin.
              --extract-schema                 Read the approved Oracle schemas. Reads one JSON request from stdin.
              --serve                          Run the source gateway. Also the Windows service entry point.
              --protect-credential <VARIABLE>  DPAPI-protect one connect string read from stdin, for the service account.
              --help                           Print this text.

            Gateway settings, read from this host's environment. There is no configuration file, and no
            setting below is a credential:

            {Settings()}
            Running unattended: install this executable as Windows service '{GatewayOptions.ServiceName}' with --serve,
            grant the service account read access to the pinned certificate's private key, and provision each Oracle
            connect string with --protect-credential run as that same service account. The gateway refuses to start
            rather than presenting an unapproved certificate, and refuses a request rather than falling back to an
            unprotected credential when a protected file exists but cannot be decrypted.
            """);
    }

    private static string Settings()
    {
        (string Name, string Description)[] settings =
        [
            (GatewayOptions.UrlVariable, "Listener origin. Must be https unless a loopback development listener was asked for."),
            (GatewayOptions.LoopbackHttpVariable, "Set to true to permit a cleartext loopback listener."),
            (GatewayOptions.TenantVariable, "Entra tenant GUID whose tokens are accepted."),
            (GatewayOptions.AudienceVariable, "Single audience this gateway's app registration exposes."),
            (GatewayOptions.CallerAppIdsVariable, "Comma-separated allowlist of caller application GUIDs."),
            (GatewayOptions.RegistryVariable, "Fully qualified path of the source registry document."),
            (GatewayTlsOptions.ThumbprintVariable, @"Thumbprint pinning the server certificate in LocalMachine\My. Preferred."),
            (GatewayTlsOptions.SubjectVariable, "DNS name the approved certificate was issued for. Defaults to the listener host."),
            (GatewayOptions.CredentialRootVariable, "Directory of DPAPI-protected credential files, one per registered variable."),
            (GatewayOptions.ConcurrencyVariable, "Concurrent extractions, 1 to 8."),
            (GatewayOptions.TimeoutVariable, "Per-extraction time budget in seconds."),
            (GatewayOptions.OutputBytesVariable, "Forms worker output ceiling in bytes."),
            (GatewayOptions.SchemaOutputBytesVariable, "Schema worker output ceiling in bytes."),
        ];

        int width = settings.Max(setting => setting.Name.Length);
        return string.Join(
            Environment.NewLine,
            settings.Select(setting => $"  {setting.Name.PadRight(width)}  {setting.Description}"));
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