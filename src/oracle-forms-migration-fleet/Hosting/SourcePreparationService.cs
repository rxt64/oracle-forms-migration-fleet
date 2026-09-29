// Copyright (c) Microsoft. All rights reserved.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OracleFormsMigrationFleet.Fleet;
using OracleFormsMigrationFleet.Fleet.Execution;

namespace OracleFormsMigrationFleet.Hosting;

/// <summary>
/// What the operator asked to prepare. Every field is an identifier or an alias; none of them is a path,
/// a connection string, or a credential, and none of them reaches a filesystem or a network call without
/// being resolved against something the server already stored.
/// </summary>
/// <param name="ProfileVersion">
/// The immutable source-profile version the browser was looking at. Preparing against a version that is
/// no longer current is refused with 409 rather than silently retargeted, because the release, path
/// alias and schema allowlist the operator saw are exactly what the gateway is about to act on.
/// </param>
/// <param name="ModuleAliases">
/// File names of modules that already exist in the caller's own session copy. They are matched against
/// that copy by name, and the bytes found there are what gets hashed and pinned.
/// </param>
public sealed record SourcePreparationRequest(
    string SourceEnvironmentId,
    int ProfileVersion,
    string WorkspaceId,
    string SourceRoot,
    IReadOnlyList<string> ModuleAliases);

/// <summary>One module's outcome, in the gateway's own terms plus what this host actually kept.</summary>
public sealed record PreparedModuleReport(
    string ModuleAlias,
    string Status,
    string? ModuleIdentity,
    string ContentSha256,
    string? ArtifactPath,
    string? ArtifactSha256,
    int ArtifactByteCount,
    string Detail);

/// <summary>
/// The whole preparation, as the console may see it.
///
/// There is no snapshot digest here. A digest identifies the source and is server-only metadata that
/// authorizations are bound to; <see cref="SnapshotRefreshed"/> reports that the identity changed without
/// disclosing what it changed to.
/// </summary>
public sealed record SourcePreparationReport(
    string ProjectId,
    string SourceEnvironmentId,
    int ProfileVersion,
    string WorkspaceId,
    string SourceRoot,
    string Gateway,
    DateTimeOffset PreparedUtc,
    int RequestedCount,
    int ExtractedCount,
    bool SnapshotRefreshed,
    int FileCount,
    IReadOnlyList<PreparedModuleReport> Modules);

/// <summary>
/// What the operator asked to prepare from the source database.
///
/// There is deliberately no schema list and no connection detail. The schemas read are the ones stored on
/// the immutable source profile the caller named, so a browser cannot ask for a schema an operator never
/// approved, and the gateway narrows that list again against its own registry.
/// </summary>
public sealed record SourceSchemaPreparationRequest(
    string SourceEnvironmentId,
    int ProfileVersion,
    string WorkspaceId,
    string SourceRoot);

/// <summary>
/// What a schema preparation produced, in the gateway's terms plus what this host actually kept.
///
/// The counts are the worker's own catalog coverage, copied through. They are not a claim that the schema
/// is complete, convertible, or equivalent to anything: they are what the catalog returned.
/// </summary>
public sealed record SourceSchemaPreparationReport(
    string ProjectId,
    string SourceEnvironmentId,
    int ProfileVersion,
    string WorkspaceId,
    string SourceRoot,
    string Gateway,
    DateTimeOffset PreparedUtc,
    string Status,
    IReadOnlyList<string> Schemas,
    string? SchemaDdlPath,
    string? ProgramUnitPath,
    string? ArtifactSha256,
    int ArtifactByteCount,
    int Tables,
    int Columns,
    int Constraints,
    int Sequences,
    int ProgramUnits,
    bool SnapshotRefreshed,
    int FileCount,
    string Detail);

/// <summary>
/// The GUI-reachable operation that turns an uploaded Forms estate into extracted artifacts.
///
/// It is the only caller of <see cref="ISourceGatewayClient"/>, and it does five things in a fixed order
/// before any I/O leaves this process:
///
/// 1. <b>Identity and membership.</b> The signed-in principal must hold
///    <see cref="WorkbenchRoles.MigrationOperator"/> on the named project. This is the same operator
///    permission that already governs source acquisition; it is deliberately NOT the sandbox or
///    production approval, because reading a customer's own source into the operator's own session is
///    not a mutation of anything the customer runs.
/// 2. <b>Source profile version.</b> The stored profile is looked up and its version must be the one the
///    caller was shown. A mismatch is 409: profiles are immutable versions, and preparing against a
///    superseded one would act on a release, alias, or allowlist nobody approved.
/// 3. <b>Workspace ownership.</b> The source copy is read through the ownership-checked reader, so a
///    caller cannot name someone else's workspace, and the bytes that get hashed are the bytes that went
///    into the digest rather than a second read of the same path.
/// 4. <b>Hash pinning.</b> This host computes each module's SHA-256 itself and sends it as the expected
///    content digest. The gateway must observe the same digest over the module it opened or nothing is
///    kept, so the two sides provably worked on the same module.
/// 5. <b>Admission.</b> The response is validated by <see cref="SourceGatewayValidation"/>, which alone
///    decides whether bytes become a local fact.
///
/// Nothing here fabricates a result. With no gateway configured the operation answers 503 and writes
/// nothing, which is the honest state of this deployment rather than a checkbox.
/// </summary>
public sealed class SourcePreparationService(
    PlatformAccessService platform,
    SourceWorkspaceService workspaces,
    ISourceGatewayClient? gateway = null,
    Func<DateTimeOffset>? clock = null)
{
    /// <summary>Module kinds a Forms worker can open. Anything else is refused before the call.</summary>
    public static readonly string[] SupportedModuleExtensions = [".fmb", ".mmb", ".pll", ".olb"];

    /// <summary>Modules per request. A larger estate is prepared in several bounded operations.</summary>
    public const int MaxModulesPerRequest = 16;

    /// <summary>Total module bytes read out of the session copy for one preparation.</summary>
    public const long MaxRetainedSourceBytes = 64L * 1024 * 1024;

    private readonly Func<DateTimeOffset> _clock = clock ?? (() => DateTimeOffset.UtcNow);

    public bool Configured => gateway is not null;

    public string Description => gateway?.Description ?? SourceGatewayUnavailable.Reason;

    public async Task<PlatformResult<SourcePreparationReport>> PrepareAsync(
        WorkbenchActor actor,
        string projectId,
        SourcePreparationRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(request);

        // Membership is checked before the host's own configuration is described, so a caller who is not
        // a member of this project learns nothing about it, including whether a gateway exists.
        PlatformResult<PlatformMembership> access = await platform
            .RequireMembershipAsync(actor, projectId, WorkbenchRoles.MigrationOperator, cancellationToken)
            .ConfigureAwait(false);
        if (!access.Succeeded)
        {
            return PlatformResult<SourcePreparationReport>.Fail(access.Status, access.Error);
        }

        SourceEnvironmentProfile? profile = await platform.Store
            .GetSourceEnvironmentProfileAsync(actor.TenantId, projectId, request.SourceEnvironmentId, version: null, cancellationToken)
            .ConfigureAwait(false);
        if (profile is null)
        {
            return PlatformResult<SourcePreparationReport>.Fail(404, "The source environment profile was not found in this project.");
        }

        if (profile.Version != request.ProfileVersion)
        {
            return PlatformResult<SourcePreparationReport>.Fail(
                409,
                $"This source environment is now at version {profile.Version}. Re-read the project and prepare against " +
                "the current immutable version; the one you were shown has been superseded.");
        }

        if (!CanExtractFormsModules(profile.Connector))
        {
            return PlatformResult<SourcePreparationReport>.Fail(
                409,
                "Only a Forms environment source is extracted through the source gateway. An operator-supplied export " +
                "is already the source, and a database reader has no Forms module to open.");
        }

        if (ValidateAliases(request.ModuleAliases) is { } aliasError)
        {
            return PlatformResult<SourcePreparationReport>.Fail(400, aliasError);
        }

        if (gateway is null)
        {
            return PlatformResult<SourcePreparationReport>.Fail(503, SourceGatewayUnavailable.Reason);
        }

        HashSet<string> wanted = new(request.ModuleAliases, StringComparer.OrdinalIgnoreCase);
        string owner = PlatformIdentity.WorkspaceOwner(actor, projectId);

        SourceGatewayAuthorizationScope scope = new(actor.TenantId, projectId);
        if (!SourceGatewayAuthorizationScope.IsWellFormed(scope))
        {
            return PlatformResult<SourcePreparationReport>.Fail(500, UnscopedActor);
        }

        // Ownership is established here and not by the read below. ReadTrustedSource deliberately falls
        // back to a workspace path resolved from the identifier alone, because a durable run replaying on
        // another replica has no interactive owner. This request has one, so the owner-checked describe is
        // the gate: without it a second member of the same project could read the first member's copy.
        if (workspaces.Describe(owner, request.WorkspaceId, request.SourceRoot) is null ||
            workspaces.SuppliedSourceBinding(owner, request.WorkspaceId, request.SourceRoot) is not { } before)
        {
            return PlatformResult<SourcePreparationReport>.Fail(
                404, "That source copy was not found, or it does not belong to you in this project.");
        }

        TrustedSourceRead? read = workspaces.ReadTrustedSource(
            owner,
            request.WorkspaceId,
            request.SourceRoot,
            relative => wanted.Contains(LastSegment(relative)),
            MaxRetainedSourceBytes);

        if (read is null)
        {
            return PlatformResult<SourcePreparationReport>.Fail(
                404, "That source copy was not found, or it does not belong to you in this project.");
        }

        // A pass that hit the intake limits carries no file. Reporting that as every module being absent
        // would blame the estate for a limit this host imposed.
        if (string.Equals(read.SnapshotHash, SourceWorkspaceService.UnhashableSource, StringComparison.Ordinal))
        {
            return PlatformResult<SourcePreparationReport>.Fail(
                409,
                "The selected source folder could not be read within this server's intake limits, so no module was " +
                "pinned and nothing was sent. Select a narrower folder.");
        }

        DateTimeOffset preparedUtc = _clock();
        List<PreparedModuleReport> reports = [];
        bool wroteAnything = false;

        foreach (string alias in request.ModuleAliases)
        {
            cancellationToken.ThrowIfCancellationRequested();

            TrustedSourceFile[] matches =
                [.. read.Files.Where(file => string.Equals(LastSegment(file.RelativePath), alias, StringComparison.OrdinalIgnoreCase))];

            if (matches.Length == 0)
            {
                reports.Add(Refused(alias, string.Empty, "That module is not in the selected source folder of your copy."));
                continue;
            }

            if (matches.Length > 1)
            {
                reports.Add(Refused(
                    alias,
                    string.Empty,
                    $"{matches.Length} files in the selected folder are named '{alias}'. Select a folder where the name " +
                    "identifies one module; nothing was sent."));
                continue;
            }

            string contentSha256 = Convert.ToHexStringLower(SHA256.HashData(matches[0].Content));
            SourceGatewayFormsModuleRequest call = new(
                SourceGatewayProtocol.SchemaVersion,
                profile.SourceEnvironmentId,
                profile.ExpectedFormsVersion,
                profile.Version,
                profile.CanonicalHash,
                alias,
                contentSha256,
                scope);

            SourceGatewayCall answered = await gateway.ExtractFormsModuleAsync(call, cancellationToken).ConfigureAwait(false);
            if (answered.TransportError is { } transportError)
            {
                reports.Add(Refused(alias, contentSha256, transportError));
                continue;
            }

            SourceGatewayAdmission admission = SourceGatewayValidation.AdmitFormsModule(
                call, answered.Response, _clock());

            if (!admission.Admitted || admission.Artifact is null)
            {
                reports.Add(new PreparedModuleReport(
                    alias,
                    admission.Reported,
                    null,
                    contentSha256,
                    null,
                    null,
                    0,
                    SourceGatewayText.Safe(admission.Reason, 512)));
                continue;
            }

            string artifactSha256 = Convert.ToHexStringLower(SHA256.HashData(admission.Artifact));
            byte[] provenance = Provenance(
                profile, alias, contentSha256, admission, answered.Response!, artifactSha256, preparedUtc, gateway.Description, scope);
            string provenanceSha256 = Convert.ToHexStringLower(SHA256.HashData(provenance));

            // One commit, because the artifact and the server's claim over it are the same fact. A file
            // written without the claim is not a weaker artifact; it is indistinguishable from one the
            // operator's archive carried, and the normalization phase treats it as exactly that.
            //
            // The revalidation runs inside the copy's writer lock, immediately before the claim is
            // recorded, so a role revoked or a profile republished while the gateway was reading denies
            // this write rather than arriving one module too late.
            PreparedCommitResult commit = await workspaces.CommitPreparedModuleAsync(
                owner,
                request.WorkspaceId,
                request.SourceRoot,
                $"{alias}.forms-ir.json",
                admission.Artifact,
                $"{alias}.provenance.json",
                provenance,
                (artifactPath, provenancePath) => new PreparedSourceClaim(
                    WorkspacePath.Normalize(request.SourceRoot),
                    alias,
                    admission.ModuleIdentity!,
                    contentSha256,
                    artifactPath,
                    artifactSha256,
                    admission.Artifact.Length,
                    provenancePath,
                    provenanceSha256,
                    provenance.Length,
                    profile.SourceEnvironmentId,
                    profile.Version,
                    profile.CanonicalHash,
                    profile.ExpectedFormsVersion,
                    gateway.Description,
                    preparedUtc),
                token => RevalidateAsync(
                    platform, workspaces, actor, projectId, request.SourceEnvironmentId, request.WorkspaceId,
                    request.SourceRoot, owner, profile, before, token),
                cancellationToken).ConfigureAwait(false);

            if (!commit.Committed)
            {
                reports.Add(Refused(alias, contentSha256, commit.Error));
                continue;
            }

            string artifactPath = commit.WorkspacePaths[0];

            wroteAnything = true;
            reports.Add(new PreparedModuleReport(
                alias,
                SourceGatewayStatus.Extracted,
                admission.ModuleIdentity,
                contentSha256,
                artifactPath,
                artifactSha256,
                admission.Artifact.Length,
                "The gateway opened this module, the returned artifact hashed to the digest it declared, and the bytes " +
                "were written into your session copy under a server-held claim that pins them to this module's digest and " +
                "this source profile version. The artifact is extraction output and has not been normalized."));
        }

        SourceWorkspaceFacts? refreshed = wroteAnything
            ? workspaces.Reindex(owner, request.WorkspaceId)
            : workspaces.Describe(owner, request.WorkspaceId);

        return PlatformResult<SourcePreparationReport>.Ok(new SourcePreparationReport(
            projectId,
            profile.SourceEnvironmentId,
            profile.Version,
            request.WorkspaceId,
            request.SourceRoot,
            gateway.Description,
            preparedUtc,
            request.ModuleAliases.Count,
            reports.Count(report => report.Status == SourceGatewayStatus.Extracted),
            wroteAnything && refreshed is not null,
            refreshed?.Summary.FileCount ?? 0,
            reports));
    }

    /// <summary>
    /// The GUI-reachable operation that reads the source database's schema through the same gateway.
    ///
    /// It is read-only by construction: the worker behind the gateway issues parameterized SELECTs
    /// against ALL_ catalog views and nothing else, and this host only ever writes the statements it got
    /// back into the caller's own session copy. Nothing is executed against any database, here or on the
    /// target, and the statements are not a converted schema.
    ///
    /// The order of checks matters and mirrors the module path: membership, then gateway, then the
    /// immutable profile version, then the connector's contract, then ownership of the copy. The schema
    /// list is the profile's, never the caller's. After the call returns, the profile and the source copy
    /// are read again and must be unchanged before anything is committed, because the artifact is bound
    /// to the profile version and to the copy it is about to be written into.
    /// </summary>
    public async Task<PlatformResult<SourceSchemaPreparationReport>> PrepareSchemaAsync(
        WorkbenchActor actor,
        string projectId,
        SourceSchemaPreparationRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(request);

        PlatformResult<PlatformMembership> access = await platform
            .RequireMembershipAsync(actor, projectId, WorkbenchRoles.MigrationOperator, cancellationToken)
            .ConfigureAwait(false);
        if (!access.Succeeded)
        {
            return PlatformResult<SourceSchemaPreparationReport>.Fail(access.Status, access.Error);
        }

        if (gateway is null)
        {
            return PlatformResult<SourceSchemaPreparationReport>.Fail(503, SourceGatewayUnavailable.Reason);
        }

        SourceEnvironmentProfile? profile = await platform.Store
            .GetSourceEnvironmentProfileAsync(actor.TenantId, projectId, request.SourceEnvironmentId, version: null, cancellationToken)
            .ConfigureAwait(false);
        if (profile is null)
        {
            return PlatformResult<SourceSchemaPreparationReport>.Fail(404, "The source environment profile was not found in this project.");
        }

        if (profile.Version != request.ProfileVersion)
        {
            return PlatformResult<SourceSchemaPreparationReport>.Fail(
                409,
                $"This source environment is now at version {profile.Version}. Re-read the project and prepare against " +
                "the current immutable version; the one you were shown has been superseded.");
        }

        if (!CanReadSchema(profile.Connector))
        {
            return PlatformResult<SourceSchemaPreparationReport>.Fail(
                409,
                "Only a Forms environment source or an Oracle database reader has a database to read. An operator-supplied " +
                "export is already the source, so there is nothing to connect to.");
        }

        if (profile.SchemaAllowlist.Count == 0)
        {
            return PlatformResult<SourceSchemaPreparationReport>.Fail(
                409,
                "This source environment allows no Oracle schema, so there is nothing a read could cover. Declare a new " +
                "immutable version that names the schemas an operator approved reading.");
        }

        string owner = PlatformIdentity.WorkspaceOwner(actor, projectId);

        SourceGatewayAuthorizationScope scope = new(actor.TenantId, projectId);
        if (!SourceGatewayAuthorizationScope.IsWellFormed(scope))
        {
            return PlatformResult<SourceSchemaPreparationReport>.Fail(500, UnscopedActor);
        }

        if (workspaces.Describe(owner, request.WorkspaceId, request.SourceRoot) is null ||
            workspaces.SuppliedSourceBinding(owner, request.WorkspaceId, request.SourceRoot) is not { } before)
        {
            return PlatformResult<SourceSchemaPreparationReport>.Fail(
                404, "That source copy was not found, or it does not belong to you in this project.");
        }

        DateTimeOffset preparedUtc = _clock();
        SourceGatewayOracleSchemaRequest call = new(
            SourceGatewayProtocol.SchemaVersion,
            profile.SourceEnvironmentId,
            profile.Version,
            profile.CanonicalHash,
            profile.SchemaAllowlist,
            scope);

        SourceGatewaySchemaCall answered = await gateway.ExtractOracleSchemaAsync(call, cancellationToken).ConfigureAwait(false);
        if (answered.TransportError is { } transportError)
        {
            return Refused(projectId, profile, request, gateway.Description, preparedUtc, transportError);
        }

        SourceGatewaySchemaAdmission admission = SourceGatewayValidation.AdmitOracleSchema(call, answered.Response, _clock());
        if (!admission.Admitted || admission.Artifact is null || admission.Projection is null)
        {
            return Refused(projectId, profile, request, gateway.Description, preparedUtc, admission.Reason, admission.Reported);
        }

        // Re-read every binding after the I/O: the actor's role, the profile, and the copy's identity.
        // A profile that moved on, a membership that was revoked, or a copy that changed while the
        // gateway was reading means the statements about to be written belong to something the operator
        // is no longer authorized for or no longer looking at, so they are discarded rather than
        // committed to the wrong identity. The same check runs again inside the writer lock below; this
        // one exists to answer the caller with a status rather than a per-file refusal.
        if (await RevalidateAsync(
                platform, workspaces, actor, projectId, request.SourceEnvironmentId, request.WorkspaceId,
                request.SourceRoot, owner, profile, before, cancellationToken).ConfigureAwait(false) is { } lapsed)
        {
            return PlatformResult<SourceSchemaPreparationReport>.Fail(409, lapsed);
        }

        SourceGatewaySchemaProjection projection = admission.Projection;
        string artifactSha256 = Convert.ToHexStringLower(SHA256.HashData(admission.Artifact));
        byte[] schemaDdl = Encoding.UTF8.GetBytes(projection.SchemaDdl);
        byte[]? programUnits = projection.ProgramUnitSql is { Length: > 0 } sql ? Encoding.UTF8.GetBytes(sql) : null;

        string stem = profile.SourceEnvironmentId;
        List<(string FileName, byte[] Content)> files =
        [
            ($"{stem}{PreparedSchemaTrustStore.SchemaDdlSuffix}", schemaDdl),
        ];
        if (programUnits is not null)
        {
            files.Add(($"{stem}{PreparedSchemaTrustStore.ProgramUnitSuffix}", programUnits));
        }

        byte[] provenance = SchemaProvenance(
            profile, projection, answered.Response!, artifactSha256, preparedUtc, gateway.Description, scope);
        files.Add(($"{stem}{PreparedSchemaTrustStore.ProvenanceSuffix}", provenance));

        PreparedCommitResult commit = await workspaces.CommitPreparedSchemaAsync(
            owner,
            request.WorkspaceId,
            request.SourceRoot,
            files,
            paths => new PreparedSchemaClaim(
                WorkspacePath.Normalize(request.SourceRoot),
                profile.SourceEnvironmentId,
                profile.Version,
                profile.CanonicalHash,
                projection.Schemas,
                artifactSha256,
                admission.Artifact.Length,
                paths[0],
                Convert.ToHexStringLower(SHA256.HashData(schemaDdl)),
                programUnits is null ? null : paths[1],
                programUnits is null ? null : Convert.ToHexStringLower(SHA256.HashData(programUnits)),
                paths[^1],
                Convert.ToHexStringLower(SHA256.HashData(provenance)),
                projection.Tables,
                projection.Columns,
                projection.Constraints,
                projection.Sequences,
                projection.ProgramUnits,
                gateway.Description,
                preparedUtc),
            token => RevalidateAsync(
                platform, workspaces, actor, projectId, request.SourceEnvironmentId, request.WorkspaceId,
                request.SourceRoot, owner, profile, before, token),
            cancellationToken).ConfigureAwait(false);

        if (!commit.Committed)
        {
            return Refused(projectId, profile, request, gateway.Description, preparedUtc, commit.Error);
        }

        IReadOnlyList<string> written = commit.WorkspacePaths;

        SourceWorkspaceFacts? refreshed = workspaces.Reindex(owner, request.WorkspaceId);

        return PlatformResult<SourceSchemaPreparationReport>.Ok(new SourceSchemaPreparationReport(
            projectId,
            profile.SourceEnvironmentId,
            profile.Version,
            request.WorkspaceId,
            request.SourceRoot,
            gateway.Description,
            preparedUtc,
            SourceGatewayStatus.Extracted,
            projection.Schemas,
            written[0],
            programUnits is null ? null : written[1],
            artifactSha256,
            admission.Artifact.Length,
            projection.Tables,
            projection.Columns,
            projection.Constraints,
            projection.Sequences,
            projection.ProgramUnits,
            refreshed is not null,
            refreshed?.Summary.FileCount ?? 0,
            "The gateway read the allowlisted schemas through parameterized catalog queries, the returned artifact hashed " +
            "to the digest it declared, and the statements it carried were written into your session copy under a " +
            "server-held claim. These are the source's own statements: nothing has been converted, nothing has been " +
            "executed against any database, and no behaviour has been compared."));
    }

    /// <summary>
    /// Which connectors have a database behind them at all. A database reader has only a schema to give;
    /// a Forms environment has both halves; an operator-supplied export has neither, because the export
    /// IS the source and there is nothing to connect to.
    /// </summary>
    public static bool CanReadSchema(SourceConnector connector) =>
        connector is SourceConnector.OracleDatabaseReader or SourceConnector.FormsBuilderWorker;

    /// <summary>Which connectors have Forms modules a worker could open.</summary>
    public static bool CanExtractFormsModules(SourceConnector connector) =>
        connector is SourceConnector.FormsBuilderWorker;

    /// <summary>
    /// Told to a caller whose own identity does not resolve to a tenant and a project this host can put
    /// on the wire. It is a refusal rather than a fallback: a gateway call with no scope is a call the
    /// gateway would have to authorize by itself.
    /// </summary>
    internal const string UnscopedActor =
        "This server could not derive an authorization scope for you on this project, so no source gateway call was made " +
        "and nothing was written.";

    /// <summary>
    /// Everything that was true before a gateway call, asked again, or the reason it no longer is.
    ///
    /// A preparation spans a slow external read, and each of these can change under it: an operator can
    /// be removed from the project, a new immutable source-environment version can be published, and the
    /// session copy can be written into or released. Checking them only at the start would let a write
    /// land under an authority that no longer exists, so they are asked again immediately before the
    /// claim is recorded, inside the writer lock that the publish also runs under.
    ///
    /// RESIDUAL WINDOW, stated rather than implied: the membership and profile reads go to the platform
    /// store, which offers no transaction this host could hold across the publish, and the lock is
    /// per-process. A revocation committed by another replica between this check and the rename is
    /// therefore still possible, and is bounded by two local file writes rather than by the gateway call.
    /// The claim it produces stays revocable — it names the profile version and hash it was prepared
    /// under, and a consumer re-derives authority from the run's own binding rather than from the claim.
    /// </summary>
    private static async Task<string?> RevalidateAsync(
        PlatformAccessService platform,
        SourceWorkspaceService workspaces,
        WorkbenchActor actor,
        string projectId,
        string sourceEnvironmentId,
        string workspaceId,
        string sourceRoot,
        string owner,
        SourceEnvironmentProfile profile,
        string expectedSuppliedSourceBinding,
        CancellationToken cancellationToken)
    {
        PlatformResult<PlatformMembership> access = await platform
            .RequireMembershipAsync(actor, projectId, WorkbenchRoles.MigrationOperator, cancellationToken)
            .ConfigureAwait(false);

        if (!access.Succeeded)
        {
            return
                "Your permission on this project changed while the source gateway was working, so nothing was written. " +
                "Nothing in your session copy was made consumable.";
        }

        SourceEnvironmentProfile? current = await platform.Store
            .GetSourceEnvironmentProfileAsync(actor.TenantId, projectId, sourceEnvironmentId, version: null, cancellationToken)
            .ConfigureAwait(false);

        if (current is null ||
            current.Version != profile.Version ||
            current.Connector != profile.Connector ||
            !string.Equals(current.CanonicalHash, profile.CanonicalHash, StringComparison.Ordinal))
        {
            return
                "This source environment changed while the source gateway was working, so nothing was written. Re-read the " +
                "project and prepare against the current immutable version.";
        }

        if (workspaces.Describe(owner, workspaceId, sourceRoot) is null ||
            workspaces.SuppliedSourceBinding(owner, workspaceId, sourceRoot) is not { } now ||
            !string.Equals(now, expectedSuppliedSourceBinding, StringComparison.Ordinal))
        {
            return
                "Your source copy changed or is no longer yours, so nothing was written. Prepare again against the copy " +
                "you intend to use.";
        }

        return null;
    }

    private static PlatformResult<SourceSchemaPreparationReport> Refused(
        string projectId,
        SourceEnvironmentProfile profile,
        SourceSchemaPreparationRequest request,
        string gateway,
        DateTimeOffset preparedUtc,
        string detail,
        string? status = null) =>
        PlatformResult<SourceSchemaPreparationReport>.Ok(new SourceSchemaPreparationReport(
            projectId,
            profile.SourceEnvironmentId,
            profile.Version,
            request.WorkspaceId,
            request.SourceRoot,
            gateway,
            preparedUtc,
            status ?? SourceGatewayStatus.Rejected,
            profile.SchemaAllowlist,
            null,
            null,
            null,
            0,
            0,
            0,
            0,
            0,
            0,
            false,
            0,
            SourceGatewayText.Safe(detail, 512)));

    /// <summary>
    /// What the fleet can say about where these statements came from, written beside them. It copies the
    /// gateway's capability states and findings through and adds nothing; in particular it never claims
    /// the schema was converted, deployed, or reconciled against anything.
    /// </summary>
    private static byte[] SchemaProvenance(
        SourceEnvironmentProfile profile,
        SourceGatewaySchemaProjection projection,
        SourceGatewayOracleSchemaResponse response,
        string artifactSha256,
        DateTimeOffset preparedUtc,
        string gateway,
        SourceGatewayAuthorizationScope scope) =>
        JsonSerializer.SerializeToUtf8Bytes(
            new
            {
                record = "fleet.source-schema-preparation/1",
                normalized = false,
                preparedUtc,
                gateway,
                authorizedTenantId = scope.TenantId,
                authorizedProjectId = scope.ProjectId,
                sourceEnvironmentId = profile.SourceEnvironmentId,
                profileVersion = profile.Version,
                profileHash = profile.CanonicalHash,
                expectedDatabaseRelease = profile.ExpectedDatabaseVersion,
                schemas = projection.Schemas,
                provider = projection.Provider,
                artifactSha256,
                extractedUtc = response.ExtractedUtc,
                coverage = new
                {
                    projection.Tables,
                    projection.Columns,
                    projection.Constraints,
                    projection.Sequences,
                    projection.ProgramUnits,
                },
                capabilities = response.Capabilities,
                findings = response.Findings,
                note =
                    "Read-only catalog extraction. The statements beside this document are the source database's own " +
                    "definitions as its catalog reported them. They have not been converted to any target dialect, they " +
                    "have not been executed anywhere, and no behaviour has been compared. This document is a readable " +
                    "copy of the server's own claim over those files and is not itself the claim: the record that makes " +
                    "them consumable is held outside this source copy.",
            },
            SourceGatewayProtocol.Json);

    /// <summary>
    /// What the fleet can say about where this artifact came from, written beside it.
    ///
    /// It records the gateway's native evidence and capability states verbatim and adds nothing. In
    /// particular it never asserts that the estate is normalized or that behaviour was preserved: the
    /// artifact is extraction output, and the normalization phase remains the only thing that adjudicates
    /// an estate against a session source root.
    /// </summary>
    private static byte[] Provenance(
        SourceEnvironmentProfile profile,
        string alias,
        string contentSha256,
        SourceGatewayAdmission admission,
        SourceGatewayFormsModuleResponse response,
        string artifactSha256,
        DateTimeOffset preparedUtc,
        string gateway,
        SourceGatewayAuthorizationScope scope) =>
        JsonSerializer.SerializeToUtf8Bytes(
            new
            {
                record = "fleet.source-preparation/1",
                normalized = false,
                preparedUtc,
                gateway,
                authorizedTenantId = scope.TenantId,
                authorizedProjectId = scope.ProjectId,
                sourceEnvironmentId = profile.SourceEnvironmentId,
                profileVersion = profile.Version,
                profileHash = profile.CanonicalHash,
                expectedFormsRelease = profile.ExpectedFormsVersion,
                moduleAlias = alias,
                moduleIdentity = admission.ModuleIdentity,
                expectedContentSha256 = contentSha256,
                observedContentSha256 = response.ObservedContentSha256,
                artifactSha256,
                extractedUtc = response.ExtractedUtc,
                native = response.Native,
                capabilities = response.Capabilities,
                findings = response.Findings,
                note =
                    "Extraction output. It has not been adjudicated as normalized, no behaviour has been compared, " +
                    "and no native capability is claimed beyond what the gateway reported above. This document is a " +
                    "readable copy of the server's own claim over the artifact beside it and is not itself the claim: " +
                    "the record that makes those bytes consumable is held outside this source copy.",
            },
            SourceGatewayProtocol.Json);

    private static PreparedModuleReport Refused(string alias, string contentSha256, string detail) =>
        new(alias, SourceGatewayStatus.Rejected, null, contentSha256, null, null, 0, SourceGatewayText.Safe(detail, 512));

    /// <summary>
    /// Aliases are simple file names, nothing else. The gateway resolves them under its own trusted input
    /// root, so a separator, a traversal step, or a rooted value has no meaning there and is refused here
    /// rather than forwarded to see what happens.
    /// </summary>
    internal static string? ValidateAliases(IReadOnlyList<string>? aliases)
    {
        if (aliases is null || aliases.Count == 0)
        {
            return "Select at least one Forms module to prepare.";
        }

        if (aliases.Count > MaxModulesPerRequest)
        {
            return $"Prepare at most {MaxModulesPerRequest} modules at a time.";
        }

        if (aliases.Distinct(StringComparer.OrdinalIgnoreCase).Count() != aliases.Count)
        {
            return "The same module was listed more than once.";
        }

        foreach (string alias in aliases)
        {
            if (alias is not { Length: > 0 and <= 64 } ||
                !char.IsAsciiLetterOrDigit(alias[0]) ||
                alias.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('.' or '_' or '-')) ||
                alias.Contains("..", StringComparison.Ordinal))
            {
                return $"'{SourceGatewayText.Safe(alias ?? string.Empty, 64)}' is not a module file name. Use the name only, with no folder.";
            }

            if (!SupportedModuleExtensions.Any(extension => alias.EndsWith(extension, StringComparison.OrdinalIgnoreCase)))
            {
                return $"'{alias}' is not a Forms module. Supported kinds are {string.Join(", ", SupportedModuleExtensions)}.";
            }
        }

        return null;
    }

    private static string LastSegment(string relativePath)
    {
        int separator = relativePath.LastIndexOf('/');
        return separator < 0 ? relativePath : relativePath[(separator + 1)..];
    }
}
