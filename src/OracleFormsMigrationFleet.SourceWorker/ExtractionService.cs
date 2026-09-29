// Copyright (c) Microsoft. All rights reserved.

using System.Runtime.InteropServices;

namespace OracleFormsMigrationFleet.SourceWorker;

/// <summary>
/// Runs one bounded extraction: check the host is the one the API needs, resolve the alias under the
/// trusted root and confirm it hashes to what the caller pinned, open the approved native provider, read
/// the module through it, and write a neutral representation whose digest is reported back.
///
/// Every refusal below is typed and carries the prerequisite that would remove it. Nothing here falls back
/// to reading the file as bytes: a module this worker could not open through the Forms API is reported as
/// unread, because binary metadata is not decoded content.
/// </summary>
public sealed class ExtractionService(
    WorkerConfiguration configuration,
    INativeFormsProvider provider,
    WorkerHost? host = null)
{
    private readonly WorkerConfiguration _configuration = configuration;
    private readonly INativeFormsProvider _provider = provider;
    private readonly WorkerHost _host = host ?? WorkerHost.Current;

    public async Task<WorkerExtractionResult> ExtractAsync(WorkerExtractionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!_host.IsWindows)
        {
            return Blocked(request, "forms.worker.os", "WorkerHostArchitecture", null,
                "operator.provision.windows.x86.worker",
                "The Forms API is a Windows library and this worker is not running on Windows.");
        }

        if (_host.ProcessArchitecture != Architecture.X86)
        {
            return Blocked(request, "forms.worker.architecture", "WorkerHostArchitecture", "x86",
                "operator.provision.windows.x86.worker",
                $"The Forms API is 32-bit and this worker is running as {_host.ProcessArchitecture}.");
        }

        if (!_configuration.ExtractionConfigured)
        {
            return Blocked(request, "forms.module.extract", "OracleFormsOpenApiLibraries", "x86",
                "operator.configure.forms.home.and.approved.binary",
                $"Extraction is not configured on this worker ({_configuration.FirstMissingSetting} is unset), so nothing was opened.");
        }

        TrustedInputResolution resolution = await TrustedInput.ResolveAsync(
            _configuration.TrustedInputRoot!, request.ModuleAlias, request.ExpectedContentSha256, cancellationToken);

        if (resolution.Path is null)
        {
            return Rejected(request, "forms.module.input", resolution.Rejection!, resolution.ObservedSha256);
        }

        NativeProviderOpen open = await _provider.OpenAsync(_configuration, cancellationToken);
        if (open.Reader is null)
        {
            return Blocked(request, "forms.openapi.load", open.Prerequisite!, "x86", open.Remediation!, open.Detail!,
                resolution.ObservedSha256);
        }

        using INativeFormsModuleReader reader = open.Reader;
        NativeModuleRead read;
        try
        {
            read = reader.Read(resolution.Path, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is SEHException or ExternalException or AccessViolationException)
        {
            // A native fault is the API telling us it cannot read this module. It is a failed extraction,
            // never a partial one, and the process does not continue as if it had content.
            return Failed(request, reader.Evidence, resolution.ObservedSha256,
                $"The Forms API faulted while reading the module ({exception.GetType().Name}); no content was extracted.");
        }

        if (read.Module is null)
        {
            return Failed(request, reader.Evidence, resolution.ObservedSha256,
                [.. read.Findings.DefaultIfEmpty("The Forms API returned no module.")]);
        }

        // The traversal refuses this already. It is repeated here because the reader is an interface and
        // this is the boundary that decides whether forms.module.extract is Verified: a module naming
        // program units or lists of values it never opened must not earn that capability or an artifact.
        if (read.Module.ProgramUnits.Count > 0 || read.Module.Lovs.Count > 0)
        {
            return Failed(request, reader.Evidence, resolution.ObservedSha256,
                $"The reader returned module '{read.Module.Name}' carrying {read.Module.ProgramUnits.Count} named program unit(s) " +
                $"and {read.Module.Lovs.Count} named list(s) of values whose definitions were not read. No representation was " +
                "written and forms.module.extract was not verified.");
        }

        FormsIrDocument document = new(
            WorkerProtocol.IrGenerator,
            WorkerProtocol.IrSchemaVersion,
            request.SourceEnvironmentId,
            request.ProfileVersion,
            request.ProfileHash,
            request.ModuleAlias,
            resolution.ObservedSha256!,
            request.ExpectedFormsRelease,
            $"{WorkerConfiguration.LibraryAlias}@{reader.Evidence.FileVersion ?? "unreported"}",
            [read.Module]);

        FormsIrWrite written = await FormsIrWriter.WriteAsync(_configuration.ArtifactOutputRoot!, document, cancellationToken);
        if (written.Path is null)
        {
            return Failed(request, reader.Evidence, resolution.ObservedSha256, written.Error!);
        }

        return new WorkerExtractionResult(
            WorkerProtocol.SchemaVersion,
            request.SourceEnvironmentId,
            "Extracted",
            DateTimeOffset.UtcNow,
            _host.OperatingSystemDescription,
            _host.ProcessArchitecture.ToString(),
            request.ExpectedFormsRelease,
            read.Module.Name,
            resolution.ObservedSha256,
            written.Path,
            written.Sha256,
            reader.Evidence,
            [
                new WorkerCapability("forms.openapi.load", CapabilityState.Verified, "OracleFormsOpenApiLibraries",
                    request.ExpectedFormsRelease, "x86", "WindowsWorker", "none",
                    $"exports={string.Join(',', reader.Evidence.ResolvedExports)}"),
                new WorkerCapability("forms.module.extract", CapabilityState.Verified, "OracleFormsOpenApiLibraries",
                    request.ExpectedFormsRelease, "x86", "WindowsWorker", "none",
                    $"blocks={read.Module.Blocks.Count};triggers={read.Module.Triggers.Count};units={read.Module.ProgramUnits.Count}"),
            ],
            read.Findings);
    }

    private WorkerExtractionResult Blocked(
        WorkerExtractionRequest request,
        string id,
        string prerequisite,
        string? architecture,
        string remediation,
        string detail,
        string? observedContentHash = null) =>
        new(WorkerProtocol.SchemaVersion, request.SourceEnvironmentId, CapabilityState.BlockedPrerequisite,
            DateTimeOffset.UtcNow, _host.OperatingSystemDescription, _host.ProcessArchitecture.ToString(),
            request.ExpectedFormsRelease, null, observedContentHash, null, null, null,
            [new WorkerCapability(id, CapabilityState.BlockedPrerequisite, prerequisite, request.ExpectedFormsRelease,
                architecture, "WindowsWorker", remediation)],
            [detail]);

    private WorkerExtractionResult Rejected(
        WorkerExtractionRequest request,
        string id,
        string detail,
        string? observedContentHash) =>
        new(WorkerProtocol.SchemaVersion, request.SourceEnvironmentId, CapabilityState.Rejected,
            DateTimeOffset.UtcNow, _host.OperatingSystemDescription, _host.ProcessArchitecture.ToString(),
            request.ExpectedFormsRelease, null, observedContentHash, null, null, null,
            [new WorkerCapability(id, CapabilityState.Rejected, "TrustedSourceInput", request.ExpectedFormsRelease,
                "x86", "WindowsWorker", "operator.place.module.under.trusted.input.root")],
            [detail]);

    private WorkerExtractionResult Failed(
        WorkerExtractionRequest request,
        WorkerNativeEvidence evidence,
        string? observedContentHash,
        params string[] findings) =>
        new(WorkerProtocol.SchemaVersion, request.SourceEnvironmentId, "ExtractionFailed",
            DateTimeOffset.UtcNow, _host.OperatingSystemDescription, _host.ProcessArchitecture.ToString(),
            request.ExpectedFormsRelease, null, observedContentHash, null, null, evidence,
            [new WorkerCapability("forms.module.extract", CapabilityState.Rejected, "OracleFormsOpenApiLibraries",
                request.ExpectedFormsRelease, "x86", "WindowsWorker", "operator.supply.readable.module")],
            findings);
}
