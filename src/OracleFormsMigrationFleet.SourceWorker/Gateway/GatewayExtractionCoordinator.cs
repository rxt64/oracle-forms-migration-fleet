namespace OracleFormsMigrationFleet.SourceWorker.Gateway;

/// <param name="StatusCode">
/// The HTTP status the endpoint returns. A policy refusal is a 200 carrying
/// <see cref="GatewayStatus.Rejected"/>, because the caller must record a typed refusal about the source
/// rather than a transport error. Only overload and malformed transport produce a non-200.
/// </param>
public sealed record GatewayFormsModuleOutcome(int StatusCode, GatewayFormsModuleResponse? Response);

public sealed record GatewayOracleSchemaOutcome(int StatusCode, GatewayOracleSchemaResponse? Response);

/// <summary>
/// Everything that happens between an authorized request and a response document.
///
/// It is deliberately separate from the HTTP host: the admission rules, the registry lookup, the bounded
/// concurrency, the worker invocation and the artifact inlining are all exercised here, and the host adds
/// only authentication, routing and serialization on top.
///
/// The coordinator adjudicates nothing. It reports what the worker observed, and every refusal it writes
/// names the prerequisite that would remove it. It issues no attestation and no approval.
/// </summary>
public sealed class GatewayExtractionCoordinator(
    GatewayOptions options,
    IGatewayExtractionRunner runner,
    Func<DateTimeOffset>? clock = null,
    IGatewaySchemaExtractionRunner? schemaRunner = null) : IDisposable
{
    private readonly SemaphoreSlim _slots = new(options.MaxConcurrentExtractions, options.MaxConcurrentExtractions);
    private readonly Func<DateTimeOffset> _clock = clock ?? (() => DateTimeOffset.UtcNow);

    public async Task<GatewayFormsModuleOutcome> ExtractFormsModuleAsync(
        GatewayCallerIdentity caller,
        GatewayFormsModuleRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(request);

        if (Admit(caller, request) is { } refusal)
        {
            return new GatewayFormsModuleOutcome(200, refusal);
        }

        GatewaySourceEntry entry = options.Registry.Find(request.SourceEnvironmentId)!;

        // One extraction at a time per configured slot. A native library that can hang is not something to
        // run an unbounded number of copies of, so exhaustion is reported as overload rather than queued.
        if (!await _slots.WaitAsync(TimeSpan.Zero, cancellationToken).ConfigureAwait(false))
        {
            return new GatewayFormsModuleOutcome(503, null);
        }

        try
        {
            WorkerExtractionRequest workerRequest = new(
                WorkerProtocol.SchemaVersion,
                request.SourceEnvironmentId,
                request.ExpectedFormsRelease,
                request.ProfileVersion,
                request.ProfileHash,
                request.ModuleAlias,
                request.ExpectedContentSha256);

            GatewayRunOutcome run = await runner.RunAsync(entry, workerRequest, cancellationToken).ConfigureAwait(false);
            return new GatewayFormsModuleOutcome(200, await InterpretAsync(request, entry, run, cancellationToken).ConfigureAwait(false));
        }
        finally
        {
            _slots.Release();
        }
    }

    /// <summary>
    /// The Oracle schema half of the contract.
    ///
    /// The worker returns its artifact INLINE rather than by path, because a catalog read writes nothing
    /// to disk, so there is no output root to validate and nothing to clean up. What this method does own
    /// is admission: the caller may only narrow the registry's allowlist, an entry with no registered
    /// Oracle connection is a typed <see cref="GatewayStatus.BlockedPrerequisite"/> rather than an
    /// attempt, and a result whose correlation fields, digest or media type do not match the request is a
    /// defect on this machine and is never reported as a fact about the source.
    /// </summary>
    public async Task<GatewayOracleSchemaOutcome> ExtractOracleSchemaAsync(
        GatewayCallerIdentity caller,
        GatewayOracleSchemaRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        GatewaySourceEntry? entry = options.Registry.Find(request.SourceEnvironmentId);

        if (request.SchemaVersion != GatewayProtocol.SchemaVersion ||
            !GatewayText.IsIdentifier(request.SourceEnvironmentId, 63) ||
            request.ProfileVersion <= 0 ||
            !ContentHash.IsSha256(request.ProfileHash) ||
            entry is null)
        {
            return Schema(request, GatewayStatus.Rejected, null, null,
                [Capability("oracle.schema.extract", GatewayStatus.Rejected, "RegisteredSourceEnvironment", null,
                    "operator.register.source.environment.on.gateway", null)],
                ["The Oracle schema request named no registered source environment, or was malformed, so nothing was contacted."]);
        }

        // Whose source this is, before any of what it contains. The identifier is not a secret and the
        // token proves only the calling application and its tenant, so the registry's own binding is the
        // only thing that keeps one project's operator out of another project's registered database.
        if (entry.Refuses(request.Scope, caller.TenantId) is { } unauthorized)
        {
            return Schema(request, GatewayStatus.Rejected, null, null,
                [Capability("oracle.schema.extract", GatewayStatus.Rejected, "AuthorizedSourceEnvironmentScope", null,
                    "operator.register.authorized.tenant.and.project.on.gateway", null)],
                [unauthorized]);
        }

        if (entry.RefusesProfile(request.ProfileVersion, request.ProfileHash) is { } unapproved)
        {
            return Schema(request, GatewayStatus.Rejected, null, null,
                [Capability("oracle.schema.extract", GatewayStatus.Rejected, "ApprovedSourceProfileVersion", null,
                    "operator.approve.the.current.source.profile.on.gateway", null)],
                [unapproved]);
        }

        // The caller may only narrow the operator's allowlist, never widen it.
        string[] requested = [.. (request.SchemaAllowlist ?? [])
            .Where(name => GatewayText.IsIdentifier(name, 30))
            .Select(name => name.ToUpperInvariant())
            .Distinct(StringComparer.Ordinal)
            .Where(entry.SchemaAllowlist.Contains)
            .Order(StringComparer.Ordinal)
            .Take(GatewayProtocol.MaxSchemaAllowlistEntries)];

        if (entry.SchemaAllowlist.Count == 0 || entry.OracleCredential is null)
        {
            return Schema(request, GatewayStatus.BlockedPrerequisite, null, null,
                [Capability("oracle.schema.extract", GatewayStatus.BlockedPrerequisite, "OracleSourceConnection", null,
                    "operator.configure.oracle.source.connection.on.gateway",
                    $"registeredSchemas={entry.SchemaAllowlist.Count};oracleConnectionRegistered={entry.OracleCredential is not null}")],
                [
                    "This source environment registers no Oracle connection or no readable schema, so no connection was " +
                    "opened and no catalog was read.",
                ]);
        }

        if (requested.Length == 0)
        {
            return Schema(request, GatewayStatus.Rejected, null, null,
                [Capability("oracle.schema.extract", GatewayStatus.Rejected, "OracleSchemaAllowlist", null,
                    "operator.request.schemas.this.gateway.registers",
                    $"registeredSchemas={entry.SchemaAllowlist.Count};requestedInScope=0")],
                ["No schema the request named is on this gateway's allowlist for that source environment, so nothing was read."]);
        }

        if (schemaRunner is null)
        {
            return Schema(request, GatewayStatus.BlockedPrerequisite, null, null,
                [Capability("oracle.schema.extract", GatewayStatus.BlockedPrerequisite, "OracleSourceConnection", null,
                    "operator.configure.oracle.source.connection.on.gateway", null)],
                ["This gateway was built without a schema extraction runner, so no connection was opened."]);
        }

        if (!await _slots.WaitAsync(TimeSpan.Zero, cancellationToken).ConfigureAwait(false))
        {
            return new GatewayOracleSchemaOutcome(503, null);
        }

        try
        {
            OracleSchemaExtractionRequest workerRequest = new(
                OracleSchemaProtocol.SchemaVersion,
                request.SourceEnvironmentId,
                request.ProfileVersion,
                request.ProfileHash,
                requested);

            GatewaySchemaRunOutcome run = await schemaRunner
                .RunAsync(entry, workerRequest, cancellationToken)
                .ConfigureAwait(false);

            return Interpret(request, requested, run);
        }
        finally
        {
            _slots.Release();
        }
    }

    private GatewayOracleSchemaOutcome Interpret(
        GatewayOracleSchemaRequest request,
        IReadOnlyList<string> requested,
        GatewaySchemaRunOutcome run)
    {
        if (run.Result is null)
        {
            return Schema(request, GatewayStatus.ExtractionFailed, null, null,
                [Capability("oracle.schema.extract", GatewayStatus.Rejected, "OracleCatalogReadAccess", null,
                    "operator.review.gateway.oracle.connection", null)],
                [GatewayText.Safe(run.Failure ?? "The schema extraction worker produced no result.", GatewayProtocol.MaxFindingCharacters)]);
        }

        OracleSchemaExtractionResult result = run.Result;
        IReadOnlyList<GatewayCapability> capabilities = Map(result.Capabilities);
        List<string> findings = [.. result.Findings
            .Take(GatewayProtocol.MaxFindings)
            .Select(finding => GatewayText.Safe(finding, GatewayProtocol.MaxFindingCharacters))
            .Where(finding => finding.Length > 0)];

        // The worker echoes its own correlation fields, including the exact list it read. A result that
        // does not name the request it was given is a defect here, not a fact about the source.
        if (result.SchemaVersion != OracleSchemaProtocol.SchemaVersion ||
            !string.Equals(result.SourceEnvironmentId, request.SourceEnvironmentId, StringComparison.Ordinal) ||
            result.ProfileVersion != request.ProfileVersion ||
            !string.Equals(result.ProfileHash, request.ProfileHash, StringComparison.Ordinal) ||
            !result.SchemaAllowlist.SequenceEqual(requested, StringComparer.Ordinal))
        {
            return Schema(request, GatewayStatus.ExtractionFailed, null, null, capabilities,
                ["The schema extraction worker returned a result that is not correlated with the request it was given."]);
        }

        if (!GatewayStatus.IsKnown(result.Status))
        {
            return Schema(request, GatewayStatus.ExtractionFailed, null, null, capabilities,
                ["The schema extraction worker reported a status this gateway does not recognize."]);
        }

        if (!string.Equals(result.Status, GatewayStatus.Extracted, StringComparison.Ordinal))
        {
            return result.SchemaArtifact is not null
                ? Schema(request, GatewayStatus.ExtractionFailed, null, null, capabilities,
                    ["The schema extraction worker reported a non-extracted status while still returning an artifact."])
                : Schema(request, result.Status, null, null, capabilities, findings);
        }

        if (result.SchemaArtifact is not { Content.Length: > 0 } artifact)
        {
            return Schema(request, GatewayStatus.ExtractionFailed, null, null, capabilities,
                ["The schema extraction worker reported an extraction but returned no artifact."]);
        }

        if (!string.Equals(artifact.MediaType, GatewayProtocol.OracleSchemaMediaType, StringComparison.Ordinal))
        {
            return Schema(request, GatewayStatus.ExtractionFailed, null, null, capabilities,
                ["The schema artifact does not declare the Oracle schema media type."]);
        }

        if (artifact.Content.Length > GatewayProtocol.MaxArtifactBytes)
        {
            return Schema(request, GatewayStatus.ExtractionFailed, null, null, capabilities,
                ["The schema artifact is larger than this gateway returns."]);
        }

        // Hashed here rather than trusted: the worker's declaration and this gateway's own digest have to
        // agree before any byte is handed onward.
        string observed = ContentHash.OfBytes(artifact.Content);
        if (!ContentHash.IsSha256(artifact.Sha256) ||
            !ContentHash.Matches(artifact.Sha256, observed) ||
            !ContentHash.Matches(result.SnapshotHash ?? string.Empty, observed))
        {
            return Schema(request, GatewayStatus.ExtractionFailed, null, null, capabilities,
                ["The schema artifact does not hash to the digest the worker declared, so nothing was returned."]);
        }

        return Schema(request, GatewayStatus.Extracted, observed,
            new GatewayInlineArtifact(GatewayProtocol.OracleSchemaMediaType, observed, Convert.ToBase64String(artifact.Content)),
            capabilities, findings);
    }

    private GatewayOracleSchemaOutcome Schema(
        GatewayOracleSchemaRequest request,
        string status,
        string? snapshotHash,
        GatewayInlineArtifact? artifact,
        IReadOnlyList<GatewayCapability> capabilities,
        IReadOnlyList<string> findings) =>
        new(200, new GatewayOracleSchemaResponse(
            GatewayProtocol.SchemaVersion,
            status,
            request.SourceEnvironmentId,
            request.ProfileVersion,
            request.ProfileHash,
            _clock(),
            snapshotHash,
            artifact,
            capabilities,
            findings,
            request.Scope));

    /// <summary>Returns a refusal document, or null when the request may proceed to the worker.</summary>
    private GatewayFormsModuleResponse? Admit(GatewayCallerIdentity caller, GatewayFormsModuleRequest request)
    {
        if (request.SchemaVersion != GatewayProtocol.SchemaVersion)
        {
            return Refuse(request, "ProtocolVersion", "operator.align.gateway.and.workbench.builds",
                $"The request declares schema version {request.SchemaVersion}; this gateway speaks version {GatewayProtocol.SchemaVersion} only.");
        }

        if (!GatewayText.IsIdentifier(request.SourceEnvironmentId, 63) ||
            !GatewayText.IsIdentifier(request.ExpectedFormsRelease, 40) ||
            request.ProfileVersion <= 0 ||
            !ContentHash.IsSha256(request.ProfileHash) ||
            !ContentHash.IsSha256(request.ExpectedContentSha256))
        {
            return Refuse(request, "RequestShape", "workbench.send.a.well.formed.correlated.request",
                "The request does not carry a well-formed source environment, release, profile version, profile hash and content digest.");
        }

        if (!TrustedInput.IsWellFormedAlias(request.ModuleAlias))
        {
            return Refuse(request, "TrustedSourceInput", "operator.place.module.under.the.registered.input.root",
                "The module alias is not a bare file name of permitted characters, so no path was built from it.");
        }

        GatewaySourceEntry? entry = options.Registry.Find(request.SourceEnvironmentId);
        if (entry is null)
        {
            return Refuse(request, "RegisteredSourceEnvironment", "operator.register.source.environment.on.gateway",
                "No source environment with that identifier is registered on this gateway, so nothing was opened.");
        }

        // Whose source this is, checked before its release or its contents. A registered identifier is
        // not an authorization: the operator also said which tenant and which projects may read it, and
        // the tenant half is verified against the caller's own validated token rather than its claim.
        if (entry.Refuses(request.Scope, caller.TenantId) is { } unauthorized)
        {
            return Refuse(request, "AuthorizedSourceEnvironmentScope",
                "operator.register.authorized.tenant.and.project.on.gateway", unauthorized);
        }

        if (entry.RefusesProfile(request.ProfileVersion, request.ProfileHash) is { } unapproved)
        {
            return Refuse(request, "ApprovedSourceProfileVersion",
                "operator.approve.the.current.source.profile.on.gateway", unapproved);
        }

        if (!string.Equals(entry.SupportedFormsRelease, request.ExpectedFormsRelease, StringComparison.OrdinalIgnoreCase))
        {
            return Refuse(request, "OracleFormsInstallation", "operator.register.the.actual.installed.forms.release",
                $"This gateway serves that source environment at Forms release {entry.SupportedFormsRelease}; the request expects a different one.");
        }

        return null;
    }

    private async Task<GatewayFormsModuleResponse> InterpretAsync(
        GatewayFormsModuleRequest request,
        GatewaySourceEntry entry,
        GatewayRunOutcome run,
        CancellationToken cancellationToken)
    {
        if (run.Result is null)
        {
            return Build(request, GatewayStatus.ExtractionFailed, null, null, null, null,
                [Capability("forms.module.extract", GatewayStatus.Rejected, "OracleFormsOpenApiLibraries",
                    request.ExpectedFormsRelease, "operator.review.gateway.worker.installation", null)],
                [run.Failure ?? "The extraction worker produced no result."]);
        }

        WorkerExtractionResult result = run.Result;
        IReadOnlyList<GatewayCapability> capabilities = Map(result.Capabilities);
        List<string> findings = [.. result.Findings
            .Take(GatewayProtocol.MaxFindings)
            .Select(finding => GatewayText.Safe(finding, GatewayProtocol.MaxFindingCharacters))
            .Where(finding => finding.Length > 0)];

        // The worker echoes its own correlation fields. A result that does not name the request it was
        // given is a defect on this machine, not a fact about the source, so it never becomes a status.
        if (result.SchemaVersion != WorkerProtocol.SchemaVersion ||
            !string.Equals(result.SourceEnvironmentId, request.SourceEnvironmentId, StringComparison.Ordinal) ||
            !string.Equals(result.ExpectedFormsRelease, request.ExpectedFormsRelease, StringComparison.Ordinal))
        {
            return Build(request, GatewayStatus.ExtractionFailed, null, null, null, null, capabilities,
                ["The extraction worker returned a result that is not correlated with the request it was given."]);
        }

        if (!GatewayStatus.IsKnown(result.Status))
        {
            return Build(request, GatewayStatus.ExtractionFailed, null, null, null, null, capabilities,
                ["The extraction worker reported a status this gateway does not recognize."]);
        }

        GatewayNativeEvidence? native = Map(result.Native);

        if (!string.Equals(result.Status, GatewayStatus.Extracted, StringComparison.Ordinal))
        {
            return Build(request, result.Status, null, result.ObservedContentSha256, null, native, capabilities, findings);
        }

        if (!ContentHash.IsSha256(result.ObservedContentSha256) ||
            !ContentHash.Matches(request.ExpectedContentSha256, result.ObservedContentSha256!))
        {
            return Build(request, GatewayStatus.ExtractionFailed, null, result.ObservedContentSha256, null, native, capabilities,
                ["The extraction worker opened a module whose content digest is not the digest the request pinned."]);
        }

        if (result.ModuleIdentity is not { Length: > 0 and <= 128 })
        {
            return Build(request, GatewayStatus.ExtractionFailed, null, result.ObservedContentSha256, null, native, capabilities,
                ["The extraction worker reported an extraction with no usable module identity."]);
        }

        GatewayArtifactResult artifact = await GatewayArtifactInliner.InlineAsync(
            entry.OutputRoot,
            result.IntermediateRepresentationPath,
            result.IntermediateRepresentationSha256,
            GatewayProtocol.FormsIrMediaType,
            cancellationToken).ConfigureAwait(false);

        if (artifact.Artifact is null)
        {
            return Build(request, GatewayStatus.ExtractionFailed, null, result.ObservedContentSha256, null, native, capabilities,
                [.. findings.Append(artifact.Failure!)]);
        }

        return Build(request, GatewayStatus.Extracted, GatewayText.Safe(result.ModuleIdentity, 128),
            result.ObservedContentSha256, artifact.Artifact, native, capabilities, findings);
    }

    private GatewayFormsModuleResponse Build(
        GatewayFormsModuleRequest request,
        string status,
        string? moduleIdentity,
        string? observedContentSha256,
        GatewayInlineArtifact? artifact,
        GatewayNativeEvidence? native,
        IReadOnlyList<GatewayCapability> capabilities,
        IReadOnlyList<string> findings) =>
        new(GatewayProtocol.SchemaVersion,
            status,
            request.SourceEnvironmentId,
            request.ExpectedFormsRelease,
            request.ProfileVersion,
            request.ProfileHash,
            request.ModuleAlias,
            _clock(),
            moduleIdentity,
            observedContentSha256,
            artifact,
            native,
            capabilities,
            findings,
            request.Scope);

    private GatewayFormsModuleResponse Refuse(
        GatewayFormsModuleRequest request,
        string prerequisite,
        string remediation,
        string detail) =>
        Build(request, GatewayStatus.Rejected, null, null, null, null,
            [Capability("forms.module.extract", GatewayStatus.Rejected, prerequisite, request.ExpectedFormsRelease, remediation, null)],
            [GatewayText.Safe(detail, GatewayProtocol.MaxFindingCharacters)]);

    private static GatewayCapability Capability(
        string id,
        string state,
        string prerequisite,
        string? requiredRelease,
        string remediation,
        string? observed) =>
        new(id, state, prerequisite, requiredRelease, "x86", "WindowsWorker", remediation, observed);

    /// <summary>
    /// Copies the worker's capability manifest through unchanged. A state is only ever <c>Verified</c>
    /// because the worker exercised it; this gateway never promotes one.
    /// </summary>
    private static IReadOnlyList<GatewayCapability> Map(IReadOnlyList<WorkerCapability> capabilities) =>
    [
        .. capabilities.Take(GatewayProtocol.MaxCapabilities).Select(capability => new GatewayCapability(
            GatewayText.Safe(capability.Id, 64),
            GatewayText.Safe(capability.State, 32),
            GatewayText.Safe(capability.Prerequisite, 64),
            capability.RequiredRelease is null ? null : GatewayText.Safe(capability.RequiredRelease, 40),
            capability.RequiredArchitecture is null ? null : GatewayText.Safe(capability.RequiredArchitecture, 16),
            capability.RequiredHost is null ? null : GatewayText.Safe(capability.RequiredHost, 32),
            GatewayText.Safe(capability.Remediation, 128),
            capability.Observed is null ? null : GatewayText.Safe(capability.Observed, GatewayProtocol.MaxFindingCharacters))),
    ];

    private static GatewayNativeEvidence? Map(WorkerNativeEvidence? evidence) =>
        evidence is null
            ? null
            : new GatewayNativeEvidence(
                GatewayText.Safe(evidence.LibraryAlias, 128),
                evidence.FileVersion is null ? null : GatewayText.Safe(evidence.FileVersion, 40),
                evidence.ProductVersion is null ? null : GatewayText.Safe(evidence.ProductVersion, 40),
                GatewayText.Safe(evidence.LibrarySha256, 64),
                GatewayText.Safe(evidence.DefinitionHeaderSha256, 64),
                [.. evidence.ResolvedExports.Take(64).Select(export => GatewayText.Safe(export, 64))],
                [.. evidence.ResolvedConstants.Take(64).Select(constant => GatewayText.Safe(constant, 64))]);

    public void Dispose() => _slots.Dispose();
}
