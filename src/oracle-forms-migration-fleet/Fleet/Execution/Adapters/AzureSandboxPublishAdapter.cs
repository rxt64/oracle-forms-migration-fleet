// Copyright (c) Microsoft. All rights reserved.

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OracleFormsMigrationFleet.Fleet.Execution.Adapters;

/// <summary>
/// Publishes the generated application tier this run produced to its Azure destination, through a trusted
/// builder the host configured.
///
/// The adapter never builds an image, never holds a registry or subscription credential, and never runs
/// anything out of the generated output. It establishes five facts and then hands them over:
///
/// <list type="number">
/// <item>the generated application verification phase ran in this run and passed;</item>
/// <item>the bytes on disk are still exactly the bytes the generation phase recorded;</item>
/// <item>the server still authorizes generating from this source, with the same recorded decisions;</item>
/// <item>the server resolves an approved, unexpired destination for this run's own target profile;</item>
/// <item>the builder came back with an immutable image digest on that exact resource.</item>
/// </list>
///
/// Any of them failing is a refusal. None of them is something the adapter can decide for itself: the
/// verification outcome comes from the executor, the digests are recomputed from the workspace, the
/// authorization and the destination come from the server at the moment of publishing, and the digest
/// comes from the builder.
/// </summary>
public sealed class AzureSandboxPublishAdapter(
    ITargetApplicationDeploymentGateway? gateway = null,
    TimeProvider? time = null) : IPhaseAdapter
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    /// <summary>Ceiling on a single generated file this adapter will digest.</summary>
    private const long MaxFileBytes = TargetDeploymentPolicy.MaxBundleFileBytes;

    /// <summary>Ceiling on the coverage record and the verification report.</summary>
    private const long MaxReportBytes = 8L * 1024 * 1024;

    private static readonly JsonSerializerOptions s_json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>
    /// The coverage record is written with default naming by the generation phase, so it is read back with
    /// default naming. Reading it camel-cased yields a record with every field empty, which would let an
    /// unbound deployment look like a bound one.
    /// </summary>
    private static readonly JsonSerializerOptions s_recordJson = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public MigrationPhase Phase => MigrationPhase.TargetApplicationDeployment;

    public async Task<PhaseExecutionResult> ExecuteAsync(
        PhaseExecutionContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        string outputRoot = WorkspacePath.Normalize(context.OutputRoot);
        string reportPath = $"{outputRoot}/reports/target-deployment.json";

        if (Prerequisite(context) is string prerequisite)
        {
            return Refuse(context, reportPath, TargetDeploymentState.NotAttempted, prerequisite, []);
        }

        if (ReadCoverage(context, outputRoot, out GenerationCoverageRecord? recorded, out string? coverageFailure)
            is false)
        {
            return Refuse(context, reportPath, TargetDeploymentState.RefusedByPolicy, coverageFailure!, []);
        }

        GenerationCoverageRecord record = recorded!;

        if (BuildBundle(context, outputRoot, record, out TargetDeploymentBundle? built, out string? bundleFailure)
            is false)
        {
            return Refuse(context, reportPath, TargetDeploymentState.RefusedByPolicy, bundleFailure!, []);
        }

        TargetDeploymentBundle bundle = built!;

        if (!string.Equals(bundle.OutputSetSha256, record.OutputSetSha256, StringComparison.Ordinal))
        {
            return Refuse(
                context,
                reportPath,
                TargetDeploymentState.RefusedByPolicy,
                "The generated application tier on disk is not the one this run recorded generating. Nothing was published, " +
                "because an image built from edited bytes would carry the provenance of a generation that did not produce it.",
                [
                    $"Recorded output-set digest: {record.OutputSetSha256}",
                    $"Digest recomputed from the workspace now: {bundle.OutputSetSha256}",
                ]);
        }

        // Checked before the destination is resolved so an unconfigured host reports the thing that is
        // actually blocking it. A host with no builder would otherwise report whatever the destination
        // lookup happened to say, which reads as an approval problem the operator cannot fix.
        if (gateway is null)
        {
            return Refuse(
                context,
                reportPath,
                TargetDeploymentState.GatewayUnavailable,
                "This host has no trusted deployment builder configured, so the product cannot publish the generated " +
                "application tier. Nothing was deployed. This is an unmet external prerequisite of the host, not a " +
                "property of the generated code, which built and passed its own tests in this run.",
                []);
        }

        TargetDeploymentAuthorityDecision destination = await ResolveDestinationAsync(context, cancellationToken)
            .ConfigureAwait(false);

        if (destination.Authority is not { } authority)
        {
            return Refuse(
                context,
                reportPath,
                TargetDeploymentState.NotAuthorized,
                "This run has no approved deployment destination at the moment it asked to publish, so nothing was deployed. " +
                $"Reasons given: {string.Join(" ", destination.Denials)}",
                destination.Denials);
        }

        TargetDeploymentBinding binding = new(
            record.RunId,
            record.TenantId,
            record.ProjectId,
            record.LedgerId,
            context.Request.EngagementId,
            context.Request.ApplicationName,
            record.SourceSnapshotHash,
            record.IntermediateContentSha256,
            record.MappingManifestSha256,
            record.ScopeDigest)
        {
            Authority = authority,
        };

        List<string> rejections =
        [
            .. TargetDeploymentPolicy.RejectBinding(binding, _time.GetUtcNow()),
            .. TargetDeploymentPolicy.RejectBundle(bundle),
        ];

        if (rejections.Count > 0)
        {
            return Refuse(
                context,
                reportPath,
                TargetDeploymentState.RefusedByPolicy,
                "Nothing was published: the deployment did not satisfy the product's own binding rules.",
                rejections,
                authority);
        }

        if (await ReauthorizeAsync(context, record, cancellationToken).ConfigureAwait(false) is string denial)
        {
            return Refuse(context, reportPath, TargetDeploymentState.NotAuthorized, denial, [], authority);
        }

        // Asked again here, after the authorization round-trip above, and compared rather than merely
        // re-read: everything between the first resolution and this point is time in which an approval can
        // be revoked, expire, or be replaced by one naming a different destination.
        TargetDeploymentAuthorityDecision confirmed = await ResolveDestinationAsync(context, cancellationToken)
            .ConfigureAwait(false);

        if (confirmed.Authority is not { } stillApproved)
        {
            return Refuse(
                context,
                reportPath,
                TargetDeploymentState.NotAuthorized,
                "The approved deployment destination stopped being readable between this phase binding it and dispatching, " +
                $"so nothing was published. Reasons given: {string.Join(" ", confirmed.Denials)}",
                confirmed.Denials,
                authority);
        }

        if (!string.Equals(
                TargetDeploymentPolicy.TargetDigest(stillApproved),
                TargetDeploymentPolicy.TargetDigest(authority),
                StringComparison.Ordinal))
        {
            return Refuse(
                context,
                reportPath,
                TargetDeploymentState.NotAuthorized,
                "The approved deployment destination changed between this phase binding it and dispatching, so nothing was " +
                "published. A deployment is dispatched against one destination or not at all.",
                [
                    // Named, never identified: this document is previewed in the browser, and the digest
                    // is enough to show the two destinations differ without publishing where either is.
                    $"Bound destination: {authority.TargetResourceName} under approval {authority.ApprovalId}.",
                    $"Destination as it stands now: {stillApproved.TargetResourceName} under approval {stillApproved.ApprovalId}.",
                ],
                authority);
        }

        if (TargetDeploymentPolicy.RejectAuthority(stillApproved, _time.GetUtcNow()) is { Count: > 0 } lapsed)
        {
            return Refuse(
                context,
                reportPath,
                TargetDeploymentState.NotAuthorized,
                "The approval for this destination no longer holds at the moment of dispatch, so nothing was published.",
                lapsed,
                authority);
        }

        string operationId = TargetDeploymentPolicy.OperationId(binding, bundle.OutputSetSha256);
        context.Info(
            $"Publishing {bundle.Files.Count.ToString(CultureInfo.InvariantCulture)} generated file(s) to " +
            $"{authority.TargetResourceName} through {gateway.Description} as operation {operationId}. The same bytes, " +
            "decisions, destination and approval always carry this identifier, so a retry re-joins the build already in " +
            "flight instead of starting a second one.");

        TargetDeploymentResult result;
        try
        {
            result = await gateway
                .PublishAsync(
                    new TargetDeploymentRequest(
                        binding,
                        bundle,
                        context.Request.Target.BackEnd,
                        context.Request.Target.Database,
                        operationId),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or HttpRequestException or TimeoutException)
        {
            result = TargetDeploymentResult.Refused(
                TargetDeploymentState.NotAttempted,
                $"The deployment builder could not be reached, so nothing was published: {exception.Message}");
        }

        if (TargetDeploymentPolicy.RejectResult(result, authority) is { Count: > 0 } unverifiable)
        {
            return Write(
                context,
                reportPath,
                binding,
                bundle,
                operationId,
                result with
                {
                    State = TargetDeploymentState.VerificationFailed,
                    FailureReason =
                        "The builder reported success, but the report does not show that anything of this run's is serving. " +
                        "It is recorded as unverified rather than as a deployment.",
                    Findings = [.. result.Findings, .. unverifiable],
                });
        }

        return Write(context, reportPath, binding, bundle, operationId, result);
    }

    /// <summary>
    /// Whether this run reached a state a deployment could be about.
    ///
    /// The executor's dependency table is not consulted for this phase, so the prerequisites are enforced
    /// here instead, and there are two of them.
    ///
    /// The first is the aggregate generated-application verification: a tier nobody exercised in this run
    /// is a tier nothing has said builds. It reports counts, so it says how many generated cases ran and
    /// not which recorded decision any of them was about.
    ///
    /// The second is the per-entry target contract verification, which is the only phase whose results
    /// attach to recorded decisions. Requiring only the aggregate meant a run could publish while every
    /// per-entry case was still a gap, or while the ledger was about to refuse the record outright —
    /// refusals that only land after the run reaches a terminal state, by which point the image is
    /// already serving. Both are required unconditionally: this adapter publishes only output it can bind
    /// to a ledger, so a schema-only or Java run never reaches here at all, and a ledger-bound run that
    /// generated under no ledger coverage is refused by <c>ReadCoverage</c> below with that reason
    /// stated. Nothing here waives a gap; a deployment policy that tolerated one would have to name the
    /// approved scope of that gap explicitly rather than accepting them as a class.
    /// </summary>
    private static string? Prerequisite(PhaseExecutionContext context)
    {
        if (Completed(context, MigrationPhase.GeneratedApplicationVerification, "nothing has executed the tier this phase would publish")
            is string aggregate)
        {
            return aggregate;
        }

        return Completed(
            context,
            MigrationPhase.TargetContractVerification,
            "nothing has read the migrated target to confirm that the decisions this tier was generated under actually hold there");
    }

    /// <summary>The reason <paramref name="phase"/> does not clear the way for a deployment, or null.</summary>
    private static string? Completed(PhaseExecutionContext context, MigrationPhase phase, string consequence)
    {
        PhaseOutcome? outcome = context.CompletedPhases.FirstOrDefault(candidate => candidate.Phase == phase);

        if (outcome is null)
        {
            return $"{phase} did not run in this run, so {consequence}. Nothing was deployed.";
        }

        return outcome.State == PhaseExecutionState.Executed
            ? null
            : $"{phase} ended in state {outcome.State}, so {consequence}. Nothing was deployed.";
    }

    private static bool ReadCoverage(
        PhaseExecutionContext context,
        string outputRoot,
        out GenerationCoverageRecord? record,
        out string? failure)
    {
        record = null;
        string path = $"{outputRoot}/{GenerationCoverage.RecordPath}";

        if (!context.Workspace.FileExists(path))
        {
            failure =
                $"No generation coverage record was written at '{path}', so this deployment could not be attributed to " +
                "the recorded decisions the tier was generated under. This product publishes only output it can bind to " +
                "a ledger, so nothing was deployed.";
            return false;
        }

        try
        {
            record = JsonSerializer.Deserialize<GenerationCoverageRecord>(
                context.Workspace.ReadText(path, MaxReportBytes),
                s_recordJson);
        }
        catch (Exception exception) when (exception is JsonException or WorkspaceLimitExceededException)
        {
            failure = $"The generation coverage record at '{path}' could not be read, so nothing was deployed: {exception.Message}";
            return false;
        }

        if (record is null || record.OutputFiles.Count == 0)
        {
            failure =
                $"The generation coverage record at '{path}' names no emitted files, so there is nothing this deployment " +
                "could be a deployment of.";
            return false;
        }

        if (record.OutputFiles.Count > TargetDeploymentPolicy.MaxBundleFiles)
        {
            failure =
                $"The generation coverage record names {record.OutputFiles.Count.ToString(CultureInfo.InvariantCulture)} " +
                $"files, above the {TargetDeploymentPolicy.MaxBundleFiles.ToString(CultureInfo.InvariantCulture)} this " +
                "product will package. Nothing was deployed rather than publishing part of the tier.";
            return false;
        }

        failure = null;
        return true;
    }

    private static bool BuildBundle(
        PhaseExecutionContext context,
        string outputRoot,
        GenerationCoverageRecord record,
        out TargetDeploymentBundle? bundle,
        out string? failure)
    {
        bundle = null;
        List<TargetDeploymentFile> files = [];

        foreach (string relative in record.OutputFiles)
        {
            string path = $"{outputRoot}/{relative}";

            if (!context.Workspace.FileExists(path))
            {
                failure =
                    $"'{relative}' was recorded as generated but is not in the workspace now. Nothing was deployed, because " +
                    "an image built from what remains would be missing part of the tier this run generated.";
                return false;
            }

            try
            {
                if (!context.Workspace.TryResolve(path, out string absolute, out string error))
                {
                    failure = $"'{relative}' could not be resolved inside the workspace and was refused: {error}";
                    return false;
                }

                files.Add(new TargetDeploymentFile(
                    relative,
                    context.Workspace.Sha256(path, MaxFileBytes),
                    new FileInfo(absolute).Length));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or WorkspaceLimitExceededException)
            {
                failure = $"'{relative}' could not be read in full, so nothing was deployed: {exception.Message}";
                return false;
            }
        }

        bundle = new TargetDeploymentBundle(
            context.WorkspaceRoot,
            outputRoot,
            files,
            GenerationCoverage.OutputSetDigest(files.Select(file => (file.Path, file.ContentSha256))));
        failure = null;
        return true;
    }

    /// <summary>
    /// Asks the server where, if anywhere, this run may publish.
    ///
    /// The answer is never read out of the workspace. The coverage record on disk names the run, the
    /// ledger, and the bytes, and it is trusted for exactly that; it says nothing about which subscription
    /// or which application an operator approved, and a destination taken from a document the generation
    /// phase wrote would be a destination the generated output could influence.
    /// </summary>
    private async Task<TargetDeploymentAuthorityDecision> ResolveDestinationAsync(
        PhaseExecutionContext context,
        CancellationToken cancellationToken)
    {
        if (context.DeploymentAuthorityProvider is not { } provider)
        {
            return TargetDeploymentAuthorityDecision.Denied(
                "This phase was given no server authority over where the run may publish, so it cannot tell which Azure " +
                "resource this run's project was approved to deploy to. Nothing was deployed.");
        }

        try
        {
            return await provider.ResolveAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or HttpRequestException or TimeoutException)
        {
            return TargetDeploymentAuthorityDecision.Denied(
                $"The approved deployment destination could not be read, so nothing was deployed: {exception.Message}");
        }
    }

    /// <summary>
    /// Asks the server again, at the moment of publishing, whether this output may still be generated from
    /// this source under these decisions.
    ///
    /// A deployment is the point where generated code starts serving requests, so a decision changed after
    /// the code was written is a decision the running application would otherwise contradict silently. The
    /// scope digest is compared against the one generation recorded, which is what makes this a check on
    /// the decisions rather than merely on the server still answering.
    /// </summary>
    private async Task<string?> ReauthorizeAsync(
        PhaseExecutionContext context,
        GenerationCoverageRecord record,
        CancellationToken cancellationToken)
    {
        if (context.AuthorizationProvider is not { } provider)
        {
            return "This phase was given no server authority over the run's source, so it cannot tell whether the decisions " +
                "the tier was generated under still stand. Nothing was deployed.";
        }

        GenerationAuthorizationDecision decision = await provider
            .AuthorizeAsync(
                new GenerationAuthorizationRequest(
                    Phase,
                    record.IntermediateContentSha256,
                    record.MappingManifestSha256,
                    []),
                cancellationToken)
            .ConfigureAwait(false);

        if (decision.Authorization is not { } issued)
        {
            return "The server did not authorize this run's source at the moment this phase asked to publish, so nothing " +
                $"was deployed. Reasons given: {string.Join(" ", decision.Denials)}";
        }

        if (!string.Equals(issued.ScopeDigest, record.ScopeDigest, StringComparison.Ordinal))
        {
            return "The recorded decisions changed after this tier was generated, so nothing was deployed. Publishing now " +
                "would put code in front of the converted database that carries a decision an operator has since revised.";
        }

        if (!string.Equals(issued.RunId, record.RunId, StringComparison.Ordinal))
        {
            return "The server's authorization is bound to a different run than the one that generated this tier, so nothing was deployed.";
        }

        // The coverage record is a document the generation phase wrote into the workspace, so every
        // identity in it is compared against the server's own answer rather than trusted. A record naming
        // another source snapshot, tenant, project, or ledger describes a generation this authorization is
        // not about, and publishing it would attach this run's provenance to bytes produced elsewhere.
        if (!string.Equals(issued.SourceSnapshotHash, record.SourceSnapshotHash, StringComparison.Ordinal))
        {
            return "The coverage record names a different source snapshot than the one the server authorized for this run, so " +
                "nothing was deployed. An image built from it would carry this run's provenance over a tier generated from " +
                "source bytes nobody approved here.";
        }

        if (!string.Equals(issued.TenantId, record.TenantId, StringComparison.Ordinal) ||
            !string.Equals(issued.ProjectId, record.ProjectId, StringComparison.Ordinal) ||
            !string.Equals(issued.LedgerId, record.LedgerId, StringComparison.Ordinal))
        {
            return "The coverage record names a different tenant, project, or disposition ledger than the one the server " +
                "authorized for this run, so nothing was deployed.";
        }

        return null;
    }

    private static PhaseExecutionResult Refuse(
        PhaseExecutionContext context,
        string reportPath,
        TargetDeploymentState state,
        string reason,
        IReadOnlyList<string> findings,
        TargetDeploymentAuthority? authority = null)
    {
        context.Workspace.WriteText(
            reportPath,
            JsonSerializer.Serialize(
                new TargetDeploymentReport(
                    "1.1",
                    context.Request.ApplicationName,
                    Succeeded: false,
                    state,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    findings,
                    reason)
                {
                    Destination = TargetDeploymentDestinationView.From(authority),
                },
                s_json));

        return new PhaseExecutionResult(
            false,
            [Artifact(reportPath)],
            findings,
            reason);
    }

    private static PhaseExecutionResult Write(
        PhaseExecutionContext context,
        string reportPath,
        TargetDeploymentBinding binding,
        TargetDeploymentBundle bundle,
        string operationId,
        TargetDeploymentResult result)
    {
        context.Workspace.WriteText(
            reportPath,
            JsonSerializer.Serialize(
                new TargetDeploymentReport(
                    "1.1",
                    context.Request.ApplicationName,
                    result.Succeeded,
                    result.State,
                    operationId,
                    bundle.OutputSetSha256,
                    binding.RunId,
                    result.BuildRunId,
                    result.ImageDigest,
                    result.ApplicationUrl,
                    result.Findings,
                    result.FailureReason)
                {
                    Destination = TargetDeploymentDestinationView.From(binding.Authority),
                    RevisionName = result.RevisionName,
                },
                s_json));

        if (result.Succeeded)
        {
            context.Info(
                $"A revision is running image digest {result.ImageDigest} at {result.ApplicationUrl}. That is a statement " +
                "about which bytes are serving, and not a claim that the deployed application behaves like the Oracle Forms " +
                "source, which nothing in this run executed.");

            return PhaseExecutionResult.Success([Artifact(reportPath)], result.Findings);
        }

        return new PhaseExecutionResult(
            false,
            [Artifact(reportPath)],
            result.Findings,
            result.FailureReason ?? $"The deployment ended in state {result.State}.");
    }

    private static ArtifactReference Artifact(string reportPath) => new(
        reportPath,
        ArtifactKind.ValidationReport,
        "What was published, which build produced it, and the immutable image digest a revision is running. " +
        "No behavioral equivalence with the Oracle Forms source is asserted.");
}

/// <summary>
/// The approved destination as an operator may read it.
///
/// The application's own name, the profile version it was approved under, the approval, and a digest over
/// the full coordinates. Deliberately not the subscription, resource group, or ARM identifier: this
/// document is returned through the artifact preview in the browser, and the digest is enough to prove two
/// deployments went to the same place without publishing where that place is.
/// </summary>
public sealed record TargetDeploymentDestinationView(
    string ApplicationResourceName,
    string TargetProfileId,
    int TargetProfileVersion,
    string TargetProfileHash,
    string EnvironmentName,
    string ApprovalId,
    DateTimeOffset ApprovalExpiresUtc,
    string TargetDigest)
{
    public static TargetDeploymentDestinationView? From(TargetDeploymentAuthority? authority) =>
        authority is null
            ? null
            : new TargetDeploymentDestinationView(
                authority.TargetResourceName,
                authority.TargetProfileId,
                authority.TargetProfileVersion,
                authority.TargetProfileHash,
                authority.EnvironmentName,
                authority.ApprovalId,
                authority.ApprovalExpiresUtc,
                TargetDeploymentPolicy.TargetDigest(authority));
}

/// <summary>
/// The deployment record written beside the run's other reports.
///
/// It names the bytes, the operation, the build, the digest, and the approved destination by name and
/// digest. There is no endpoint, registry, subscription, principal, or token in it, so it stays safe to
/// return through the artifact preview an operator reads in the browser.
/// </summary>
public sealed record TargetDeploymentReport(
    string SchemaVersion,
    string Application,
    bool Succeeded,
    TargetDeploymentState State,
    string? OperationId,
    string? OutputSetSha256,
    string? RunId,
    string? BuildRunId,
    string? ImageDigest,
    string? ApplicationUrl,
    IReadOnlyList<string> Findings,
    string? FailureReason)
{
    /// <summary>Where this run was approved to publish, or null when it resolved nowhere.</summary>
    public TargetDeploymentDestinationView? Destination { get; init; }

    public string? RevisionName { get; init; }

    public const string NoEquivalenceClaim =
        "None. An image built from this run's generated bytes is running against the converted database. No Oracle Forms " +
        "runtime was contacted and no behavioral equivalence with the source is asserted.";

    public string EquivalenceClaim => NoEquivalenceClaim;
}
