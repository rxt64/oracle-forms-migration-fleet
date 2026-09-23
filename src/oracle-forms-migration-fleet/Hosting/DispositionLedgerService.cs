using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using OracleFormsMigrationFleet.Fleet;
using OracleFormsMigrationFleet.Fleet.Execution;

namespace OracleFormsMigrationFleet.Hosting;

/// <summary>What the console is told about one ledger. Every number is computed from stored entries.</summary>
public sealed record DispositionLedgerSummary(
    string LedgerId,
    string ProjectId,
    string RunId,
    string SourceSnapshotHash,
    string SourceRoot,
    DateTimeOffset CreatedUtc,
    string CreatedByObjectId,
    int ModuleCount,
    DispositionLedgerCounts Counts,
    DispositionCompletionState Completion,
    bool IsStale,
    string? StaleReason);

/// <summary>Counts for one behaviour grouping within one screen.</summary>
public sealed record DispositionBehaviorGroupView(string BehaviorGroup, DispositionLedgerCounts Counts);

/// <summary>One screen's totals, so an operator reads the estate by screen rather than by property.</summary>
public sealed record DispositionScreenView(
    string ModuleName,
    string FilePath,
    DispositionLedgerCounts Counts,
    IReadOnlyList<DispositionBehaviorGroupView> Groups);

public sealed record DispositionLedgerView(
    DispositionLedgerSummary Summary,
    IReadOnlyList<DispositionScreenView> Screens,
    IReadOnlyList<DispositionMappingRule> Rules);

/// <summary>A bounded page of entries, for the expandable technical detail under a screen.</summary>
public sealed record DispositionEntryPage(
    IReadOnlyList<DispositionLedgerEntry> Entries,
    int Total,
    int Skip,
    int Take);

/// <summary>The only thing an actor may send about an entry: a decision, its reason, and its rule.</summary>
public sealed record DispositionDecisionInput(
    DispositionDecision Decision,
    string? Rationale,
    string? MappingRuleId,
    int MappingRuleVersion,
    int ExpectedVersion);

/// <summary>
/// Execution evidence for one entry, produced inside this process by something that ran.
///
/// It reaches the ledger only through <see cref="DispositionLedgerService.RecordVerificationAsync"/>,
/// which no endpoint maps, because verification a caller can post is verification that never happened.
/// </summary>
public sealed record DispositionVerificationEvidence(
    string RunId,
    string EntryId,
    IReadOnlyList<DispositionGeneratedReference> Generated,
    IReadOnlyList<DispositionTestReference> Tests);

/// <summary>
/// What recording one run's generation changed. <see cref="EntriesRecorded"/> is a count of decisions an
/// artifact now exists for, and is deliberately not reported as anything having been verified.
/// </summary>
public sealed record DispositionLedgerGeneration(
    string LedgerId,
    string RunId,
    string OutputSetSha256,
    int FilesHashed,
    int EntriesRecorded);

/// <summary>
/// What recording one run's executed verification changed.
///
/// The counts are kept apart on purpose. <see cref="CasesRecorded"/> is how many results were entered,
/// <see cref="EntriesRecorded"/> how many properties now carry one, and <see cref="GapsDeclared"/> how
/// many things the verification stated it does not demonstrate. A reader who wants "how much of this
/// migration is proved" gets it from the ledger's own counts, never from these.
/// </summary>
public sealed record DispositionLedgerVerification(
    string LedgerId,
    string RunId,
    string TestedOutputSetSha256,
    int EntriesRecorded,
    int CasesRecorded,
    int CasesPassed,
    int CasesFailed,
    int GapsDeclared);

/// <summary>
/// The claim a still-running worker holds over the run whose generation it is recording.
///
/// It is supplied only by the worker that made the claim. Recording under it re-establishes ownership
/// against the run store rather than trusting that the claim made earlier still stands, and stamps the
/// token onto what is written so a displaced owner's write can be recognised later.
/// </summary>
public sealed record MigrationRunOwnership(long FenceToken, TimeSpan LeaseExtension);

/// <summary>
/// The server's authority over one run's generation, bound at construction to the actor, the ledger and
/// the run it answers for.
///
/// A phase holds this, not an authorization. It asks at the moment it is about to generate and is
/// answered from the ledger as it stands then, so decisions changed after an operator reviewed them are
/// the decisions the run acts under — or the reason it stops. The phase names no tenant, project, ledger
/// or run: those are fixed here by the host, so an adapter cannot widen its own authority and a request
/// body cannot supply one.
///
/// Authority is per phase. A phase this provider was not constructed for is denied rather than granted
/// the generation phase's authorization under another name.
/// </summary>
public sealed class LedgerGenerationAuthorizationProvider(
    DispositionLedgerService ledgers,
    WorkbenchActor actor,
    string ledgerId,
    string runId,
    IReadOnlyList<MigrationPhase>? phases = null) : IGenerationAuthorizationProvider
{
    private static readonly MigrationPhase[] s_defaultPhases = [MigrationPhase.ApplicationCodeConversion];

    private readonly IReadOnlyList<MigrationPhase> _phases = phases is { Count: > 0 } ? phases : s_defaultPhases;

    public async Task<GenerationAuthorizationDecision> AuthorizeAsync(
        GenerationAuthorizationRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!_phases.Contains(request.Phase))
        {
            return GenerationAuthorizationDecision.Denied(
                $"This run carries no generation authority for {request.Phase}. An authorization issued for one phase is not a " +
                "licence for another, so it was not offered here.");
        }

        PlatformResult<GenerationAuthorization> issued = await ledgers
            .AuthorizeGenerationAsync(actor, ledgerId, runId, cancellationToken).ConfigureAwait(false);

        if (!issued.Succeeded)
        {
            return GenerationAuthorizationDecision.Denied(
                issued.Error ?? "The server would not authorize generating from this run's source.");
        }

        GenerationAuthorization authorization = issued.Value!;

        // The phase re-adjudicates these too. Denying here as well means a mismatch is reported as the
        // server refusing rather than as the phase disbelieving something the server handed it.
        if (!string.Equals(
                authorization.IntermediateContentSha256, request.ObservedIntermediateSha256, StringComparison.OrdinalIgnoreCase))
        {
            return GenerationAuthorizationDecision.Denied(
                "The normalized source this phase read is not the normalized source the recorded decisions were made against.");
        }

        return string.Equals(
            authorization.MappingManifestSha256, request.ObservedMappingManifestSha256, StringComparison.OrdinalIgnoreCase)
            ? GenerationAuthorizationDecision.Granted(authorization)
            : GenerationAuthorizationDecision.Denied(
                "The target mapping this phase read is not the mapping the server issued an authorization over.");
    }
}

/// <summary>
/// The server's authority over verifying one run's generation, bound at construction to the ledger, the
/// run, and the ownership claim the worker holds.
///
/// The verification phase holds this and nothing else. It offers the digest it observed on disk and is
/// answered from a generation the ledger already recorded durably under this run's claim — so a phase
/// whose generation was never recorded, whose output has changed, or whose worker has been displaced gets
/// no claim and opens no connection to a customer database.
/// </summary>
public sealed class LedgerEntryVerificationProvider(
    DispositionLedgerService ledgers,
    string tenantId,
    string ledgerId,
    string runId,
    MigrationRunOwnership? ownership) : IEntryVerificationGenerationProvider
{
    public Task<EntryVerificationClaimDecision> ClaimAsync(
        string observedOutputSetSha256,
        CancellationToken cancellationToken) =>
        ledgers.ClaimGenerationForVerificationAsync(
            tenantId, ledgerId, runId, observedOutputSetSha256, ownership, cancellationToken);
}

/// <summary>
/// The disposition ledger: what the source declared, what was decided about it, and what was proved.
///
/// Ingest reads one owned, completed run's own intermediate representation, after verifying the bytes
/// against that run's artifact manifest. Nothing is accepted from a request body except a run identifier,
/// an entry identifier, and a decision; the snapshot hash, the identities, the observed values, the
/// actor, and every timestamp are the server's.
///
/// The ledger authorizes nothing and signs nothing. It refuses a decision with no rule or no reason, it
/// refuses to call a snapshot complete while anything is unresolved or deferred, and it goes stale the
/// moment a newer snapshot exists for the project.
/// </summary>
public sealed class DispositionLedgerService(
    IPlatformStateStore store,
    PlatformAccessService platform,
    IMigrationRunStore runs,
    SourceWorkspaceService workspaces,
    Func<DateTimeOffset>? clock = null)
{
    /// <summary>The artifact source normalization writes its intermediate representation to.</summary>
    public const string IntermediateArtifactSuffix = "/intermediate/forms-ir.json";

    public const int MaxEntryPage = 200;

    /// <summary>Ceiling on the coverage record this server reads back out of a run's output.</summary>
    public const long MaxCoverageBytes = 4L * 1024 * 1024;

    private static readonly JsonSerializerOptions s_coverageJson = new()
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly Func<DateTimeOffset> _clock = clock ?? (() => DateTimeOffset.UtcNow);

    /// <summary>
    /// Records a ledger from the run the caller names, reading only what that run already produced.
    ///
    /// The run must belong to a project the caller is an operator on, must have succeeded, and its
    /// intermediate artifact must still hash to what its own manifest recorded. A run from another
    /// project is not found here, not filtered out later.
    /// </summary>
    public async Task<PlatformResult<DispositionLedgerView>> IngestFromRunAsync(
        WorkbenchActor actor,
        string projectId,
        string? runId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);

        PlatformResult<PlatformMembership> membership = await platform
            .RequireMembershipAsync(actor, projectId, WorkbenchRoles.MigrationOperator, cancellationToken)
            .ConfigureAwait(false);
        if (!membership.Succeeded)
        {
            return PlatformResult<DispositionLedgerView>.Fail(membership.Status, membership.Error);
        }

        if (string.IsNullOrWhiteSpace(runId))
        {
            return Fail(400, "A ledger is read from one completed run. Name the run.");
        }

        MigrationRunRecord? run = await runs.GetAsync(actor.TenantId, runId, cancellationToken).ConfigureAwait(false);
        if (run is null || !string.Equals(run.ProjectId, projectId, StringComparison.Ordinal))
        {
            return Fail(404, "No run with that identifier belongs to this project.");
        }

        if (run.State != MigrationRunState.Succeeded)
        {
            return Fail(409, $"That run is {run.State}. A ledger is only read from a run that finished successfully.");
        }

        IReadOnlyList<MigrationRunArtifact> manifest =
            await runs.ArtifactsAsync(actor.TenantId, run.RunId, cancellationToken).ConfigureAwait(false);

        MigrationRunArtifact[] candidates =
        [
            .. manifest.Where(artifact =>
                artifact.Path.EndsWith(IntermediateArtifactSuffix, StringComparison.Ordinal)),
        ];

        if (candidates.Length != 1)
        {
            return Fail(409, candidates.Length == 0
                ? "That run wrote no normalized Forms representation, so it declared no source property to record."
                : "That run recorded more than one normalized Forms representation, so which one the ledger would describe is ambiguous.");
        }

        MigrationRunArtifact intermediate = candidates[0];
        string? workspaceRoot = workspaces.ResolveRoot(run.WorkspaceOwnerId, run.WorkspaceId)
            ?? workspaces.ResolveDurableRoot(run.WorkspaceId);

        if (workspaceRoot is null || !TryResolveRunArtifact(workspaceRoot, intermediate.Path, out string absolute))
        {
            return Fail(410, "The run's artifact manifest is retained, but the workspace bytes it describes have expired.");
        }

        string json;
        try
        {
            FileInfo file = new(absolute);
            if (file.Length != intermediate.ByteLength || file.Length > FormsIntermediateReader.MaxDocumentBytes)
            {
                return Fail(409, "The retained normalized representation no longer matches the size its run recorded.");
            }

            byte[] bytes = await File.ReadAllBytesAsync(absolute, cancellationToken).ConfigureAwait(false);
            string hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
            if (!CryptographicOperations.FixedTimeEquals(
                    Convert.FromHexString(hash), Convert.FromHexString(intermediate.ContentSha256)))
            {
                return Fail(409, "The retained normalized representation no longer matches the digest its run recorded.");
            }

            json = Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF');
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or FormatException)
        {
            return Fail(500, "The normalized representation this run wrote could not be read.");
        }

        string sourceRoot = WorkspacePath.Normalize(run.Request.SourceRoot);
        FormsIntermediateRead read = FormsIntermediateReader.Read(json, sourceRoot);
        if (read.Modules is not { } modules)
        {
            return Fail(409, $"The run's normalized representation was refused, so no ledger was written: {read.Error}");
        }

        string ledgerId = $"dled-{Guid.NewGuid():N}";
        (IReadOnlyList<DispositionLedgerEntry>? entries, string? rejection) = DispositionLedgerEntries.Project(
            ledgerId, actor.TenantId, projectId, run.SourceSnapshotHash, modules);

        if (entries is null)
        {
            return Fail(409, rejection!);
        }

        DispositionLedger ledger = new()
        {
            LedgerId = ledgerId,
            TenantId = actor.TenantId,
            ProjectId = projectId,
            RunId = run.RunId,
            SourceSnapshotHash = run.SourceSnapshotHash,
            SourceRoot = sourceRoot,
            IntermediateContentSha256 = intermediate.ContentSha256,
            CreatedUtc = _clock(),
            CreatedByObjectId = actor.ObjectId,
            ModuleCount = modules.Count,
            EntryCount = entries.Count,
        };

        DispositionLedger? written =
            await store.CreateDispositionLedgerAsync(ledger, entries, cancellationToken).ConfigureAwait(false);

        if (written is null)
        {
            return Fail(409, "That run already has a ledger. Open it rather than recording its source a second time.");
        }

        return PlatformResult<DispositionLedgerView>.Ok(await ViewAsync(written, entries, cancellationToken).ConfigureAwait(false));
    }

    public async Task<PlatformResult<IReadOnlyList<DispositionLedgerSummary>>> LedgersAsync(
        WorkbenchActor actor,
        string projectId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);

        PlatformResult<PlatformMembership> membership =
            await platform.RequireMembershipAsync(actor, projectId, null, cancellationToken).ConfigureAwait(false);
        if (!membership.Succeeded)
        {
            return PlatformResult<IReadOnlyList<DispositionLedgerSummary>>.Fail(membership.Status, membership.Error);
        }

        List<DispositionLedgerSummary> summaries = [];
        foreach (DispositionLedger ledger in
            await store.DispositionLedgersAsync(actor.TenantId, projectId, cancellationToken).ConfigureAwait(false))
        {
            IReadOnlyList<DispositionLedgerEntry> entries = await AdmissibleAsync(
                actor.TenantId,
                await store.DispositionLedgerEntriesAsync(actor.TenantId, ledger.LedgerId, cancellationToken)
                    .ConfigureAwait(false),
                cancellationToken).ConfigureAwait(false);
            summaries.Add(await SummaryAsync(ledger, entries, cancellationToken).ConfigureAwait(false));
        }

        return PlatformResult<IReadOnlyList<DispositionLedgerSummary>>.Ok(summaries);
    }

    public async Task<PlatformResult<DispositionLedgerView>> ViewAsync(
        WorkbenchActor actor,
        string ledgerId,
        CancellationToken cancellationToken)
    {
        PlatformResult<DispositionLedger> resolved =
            await ResolveAsync(actor, ledgerId, null, cancellationToken).ConfigureAwait(false);
        if (!resolved.Succeeded)
        {
            return PlatformResult<DispositionLedgerView>.Fail(resolved.Status, resolved.Error);
        }

        IReadOnlyList<DispositionLedgerEntry> entries = await AdmissibleAsync(
            actor.TenantId,
            await store.DispositionLedgerEntriesAsync(actor.TenantId, ledgerId, cancellationToken).ConfigureAwait(false),
            cancellationToken).ConfigureAwait(false);

        return PlatformResult<DispositionLedgerView>.Ok(
            await ViewAsync(resolved.Value!, entries, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>A bounded slice of one screen's rows. The whole ledger is never one response.</summary>
    public async Task<PlatformResult<DispositionEntryPage>> EntriesAsync(
        WorkbenchActor actor,
        string ledgerId,
        string? moduleName,
        string? behaviorGroup,
        bool undecidedOnly,
        int skip,
        int take,
        CancellationToken cancellationToken)
    {
        PlatformResult<DispositionLedger> resolved =
            await ResolveAsync(actor, ledgerId, null, cancellationToken).ConfigureAwait(false);
        if (!resolved.Succeeded)
        {
            return PlatformResult<DispositionEntryPage>.Fail(resolved.Status, resolved.Error);
        }

        IEnumerable<DispositionLedgerEntry> matching = await AdmissibleAsync(
            actor.TenantId,
            await store.DispositionLedgerEntriesAsync(actor.TenantId, ledgerId, cancellationToken).ConfigureAwait(false),
            cancellationToken).ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(moduleName))
        {
            matching = matching.Where(entry =>
                string.Equals(entry.Identity.ModuleName, moduleName, StringComparison.Ordinal));
        }

        if (!string.IsNullOrWhiteSpace(behaviorGroup))
        {
            matching = matching.Where(entry =>
                string.Equals(entry.Identity.BehaviorGroup, behaviorGroup, StringComparison.Ordinal));
        }

        if (undecidedOnly)
        {
            matching = matching.Where(entry => !entry.IsDecided);
        }

        DispositionLedgerEntry[] ordered =
        [
            .. matching
                .OrderBy(entry => entry.Identity.ObjectPath, StringComparer.Ordinal)
                .ThenBy(entry => entry.Identity.PropertyName, StringComparer.Ordinal),
        ];

        int page = Math.Clamp(take, 1, MaxEntryPage);
        int offset = Math.Clamp(skip, 0, ordered.Length);

        return PlatformResult<DispositionEntryPage>.Ok(
            new DispositionEntryPage([.. ordered.Skip(offset).Take(page)], ordered.Length, offset, page));
    }

    /// <summary>
    /// Records one operator's disposition of one property.
    ///
    /// The decision is refused without a rationale and a published rule that authorizes it, refused when
    /// the entry moved since the caller read it, and refused outright while the ledger is stale — a
    /// decision about a snapshot that has been superseded is a decision about source nobody is migrating.
    /// </summary>
    public async Task<PlatformResult<DispositionLedgerEntry>> DecideAsync(
        WorkbenchActor actor,
        string ledgerId,
        string entryId,
        DispositionDecisionInput input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);

        PlatformResult<DispositionLedger> resolved = await ResolveAsync(
            actor, ledgerId, WorkbenchRoles.MigrationOperator, cancellationToken).ConfigureAwait(false);
        if (!resolved.Succeeded)
        {
            return PlatformResult<DispositionLedgerEntry>.Fail(resolved.Status, resolved.Error);
        }

        if (await StaleReasonAsync(resolved.Value!, cancellationToken).ConfigureAwait(false) is { } stale)
        {
            return PlatformResult<DispositionLedgerEntry>.Fail(409, stale);
        }

        if (DispositionLedgerRules.RejectDecision(
                input.Decision, input.Rationale, input.MappingRuleId, input.MappingRuleVersion) is { } rejection)
        {
            return PlatformResult<DispositionLedgerEntry>.Fail(400, rejection);
        }

        IReadOnlyList<DispositionLedgerEntry> entries = await store
            .DispositionLedgerEntriesAsync(actor.TenantId, ledgerId, cancellationToken).ConfigureAwait(false);

        if (entries.FirstOrDefault(entry => string.Equals(entry.EntryId, entryId, StringComparison.Ordinal)) is not { } current)
        {
            return PlatformResult<DispositionLedgerEntry>.Fail(404, "This ledger records no property with that identifier.");
        }

        // A decision replaces the reasoning, never the artifact record: what was generated stays exactly
        // as the server recorded it.
        DispositionLedgerEntry updated = current with
        {
            Decision = input.Decision,
            Rationale = input.Rationale!.Trim(),
            MappingRuleId = input.MappingRuleId,
            MappingRuleVersion = input.MappingRuleVersion,
            DecidedByObjectId = actor.ObjectId,
            DecidedUtc = _clock(),
            DecisionSequence = current.DecisionSequence + 1,
        };

        // A test that ran proved something about an artifact generated under the decision that stood
        // then, and an artifact was generated under that decision and no other. Once the decision moves,
        // both stop standing: the row keeps every reference as the audit trail, and the property reads
        // back neither generated nor verified until it is generated again under the decision that now
        // stands and retested against what that produced.
        updated = updated with
        {
            Verification = DispositionLedgerRules
                .AdmitEvidence(updated, updated.GeneratedRefs, updated.TestRefs).Verification,
        };

        DispositionLedgerEntry? written = await store
            .UpdateDispositionLedgerEntryAsync(updated, input.ExpectedVersion, cancellationToken).ConfigureAwait(false);

        return written is null
            ? PlatformResult<DispositionLedgerEntry>.Fail(
                409, "This property changed since you read it. Re-read the ledger and decide again.")
            : PlatformResult<DispositionLedgerEntry>.Ok(written);
    }

    /// <summary>
    /// Records what a generation phase wrote and what an executed test returned, for one entry.
    ///
    /// Server-only by construction: it takes a <see cref="DispositionVerificationEvidence"/> that a caller
    /// has no route to produce, and it re-derives the run, the project, and the snapshot itself. It will
    /// not mark anything verified from a file count, a build result, or an artifact merely existing — a
    /// test must be present, and it must carry the producer's own immutable statement of the decision it
    /// was executed under, the source it was generated from, the content it ran against, and the
    /// ownership claim it ran under. None of that is supplied here on the producer's behalf: the server
    /// checks the claim against what it holds, so a result that arrives after the decision moved, after
    /// the output was regenerated, or after a newer claim displaced the worker has nothing to attach to.
    /// </summary>
    public async Task<PlatformResult<DispositionLedgerEntry>> RecordVerificationAsync(
        string tenantId,
        string ledgerId,
        DispositionVerificationEvidence evidence,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(evidence);

        DispositionLedger? ledger =
            await store.GetDispositionLedgerAsync(tenantId, ledgerId, cancellationToken).ConfigureAwait(false);
        if (ledger is null)
        {
            return PlatformResult<DispositionLedgerEntry>.Fail(404, "No ledger with that identifier exists in this tenant.");
        }

        MigrationRunRecord? run = await runs.GetAsync(tenantId, evidence.RunId, cancellationToken).ConfigureAwait(false);
        if (run is null || !string.Equals(run.ProjectId, ledger.ProjectId, StringComparison.Ordinal))
        {
            return PlatformResult<DispositionLedgerEntry>.Fail(404, "No run with that identifier belongs to this ledger's project.");
        }

        if (!string.Equals(run.SourceSnapshotHash, ledger.SourceSnapshotHash, StringComparison.Ordinal))
        {
            return PlatformResult<DispositionLedgerEntry>.Fail(
                409, "That run read a different source snapshot, so its results are not evidence about this ledger's source.");
        }

        if (evidence.Generated.Any(reference => !string.Equals(reference.RunId, evidence.RunId, StringComparison.Ordinal)) ||
            evidence.Tests.Any(test => !string.Equals(test.RunId, evidence.RunId, StringComparison.Ordinal)))
        {
            return PlatformResult<DispositionLedgerEntry>.Fail(400, "Every reference in one evidence record belongs to the run that produced it.");
        }

        IReadOnlyList<DispositionLedgerEntry> entries = await store
            .DispositionLedgerEntriesAsync(tenantId, ledgerId, cancellationToken).ConfigureAwait(false);

        if (entries.FirstOrDefault(entry => string.Equals(entry.EntryId, evidence.EntryId, StringComparison.Ordinal)) is not { } current)
        {
            return PlatformResult<DispositionLedgerEntry>.Fail(404, "This ledger records no property with that identifier.");
        }

        foreach (DispositionGeneratedReference offered in evidence.Generated)
        {
            if (DispositionLedgerRules.RejectGeneratedBinding(current, offered, run.FenceToken) is { } refused)
            {
                return PlatformResult<DispositionLedgerEntry>.Fail(409, refused);
            }
        }

        // Only the moment of recording is the server's. Everything else a reference carries is what the
        // producer says it actually generated and actually tested, checked above and below against what
        // this server holds — stamping the current revision, the current fence, or the standing digest
        // onto a record that arrived without them would manufacture the very agreement being verified.
        DateTimeOffset recordedUtc = _clock();

        DispositionGeneratedReference[] generated =
            [.. current.GeneratedRefs, .. evidence.Generated
                .Select(reference => reference with { RecordedUtc = recordedUtc })
                .Where(reference => !Carries(current, reference))];

        // A test result is a statement about a generated artifact, so it is admitted only against the
        // generation that stands for this property right now: output this run produced, under the
        // ownership this run still holds. A result executed against an image that has since been
        // regenerated, or produced by a worker a newer claim displaced, has nothing here to attach to.
        DispositionGeneratedReference? standing =
            DispositionLedgerRules.StandingGeneration(current.DecisionRevision, generated);

        if (evidence.Tests.Count > 0 &&
            (standing is null ||
             !string.Equals(standing.RunId, evidence.RunId, StringComparison.Ordinal) ||
             standing.RunFenceToken != run.FenceToken))
        {
            return PlatformResult<DispositionLedgerEntry>.Fail(409,
                "The generation that stands for this property is not the one this run owns, so a result executed against some other " +
                "output was not entered as evidence about it. Generate under this run and record the result against what it wrote.");
        }

        foreach (DispositionTestReference offered in evidence.Tests)
        {
            if (DispositionLedgerRules.RejectTestBinding(current, standing, offered) is { } refused)
            {
                return PlatformResult<DispositionLedgerEntry>.Fail(409, refused);
            }
        }

        // Re-recording the identical result is the same fact arriving twice, so it changes nothing.
        DispositionTestReference[] recorded =
            [.. current.TestRefs, .. evidence.Tests
                .Select(test => test with { RecordedUtc = recordedUtc })
                .Where(test => !Carries(current, test))];

        // Passed is the only state a test that ran and succeeded can produce. Everything else, including
        // "generated and never executed", stays NotExecuted.
        DispositionVerificationStatus verification =
            DispositionLedgerRules.AdmitEvidence(current, generated, recorded).Verification;

        DispositionLedgerEntry? written = await store.UpdateDispositionLedgerEntryAsync(
            current with { GeneratedRefs = generated, TestRefs = recorded, Verification = verification },
            current.Version,
            cancellationToken).ConfigureAwait(false);

        return written is null
            ? PlatformResult<DispositionLedgerEntry>.Fail(409, "This property changed while its evidence was being recorded.")
            : PlatformResult<DispositionLedgerEntry>.Ok(written);
    }

    /// <summary>
    /// Whether the ledger a run names is one this actor may generate under, and one that describes the
    /// source this run would read.
    ///
    /// It is called before a run is stored, so an operator is told at the moment they start a run that the
    /// ledger is another project's, is stale, or was recorded against different source — rather than being
    /// told by a generation phase refusing much later. It issues nothing and grants nothing: the phase
    /// still asks <see cref="AuthorizeGenerationAsync"/> at the moment it generates, and the decisions it
    /// acts under are the rows as they stand then.
    /// </summary>
    public async Task<PlatformResult<DispositionLedger>> BindRunAsync(
        WorkbenchActor actor,
        string ledgerId,
        string projectId,
        string sourceSnapshotHash,
        CancellationToken cancellationToken)
    {
        PlatformResult<DispositionLedger> resolved = await ResolveAsync(
            actor, ledgerId, WorkbenchRoles.MigrationOperator, cancellationToken).ConfigureAwait(false);
        if (!resolved.Succeeded)
        {
            return resolved;
        }

        DispositionLedger ledger = resolved.Value!;

        // Not found rather than forbidden: which ledgers another project holds is not this caller's to learn.
        if (!string.Equals(ledger.TenantId, actor.TenantId, StringComparison.Ordinal) ||
            !string.Equals(ledger.ProjectId, projectId, StringComparison.Ordinal))
        {
            return PlatformResult<DispositionLedger>.Fail(404, "No ledger with that identifier is available to you.");
        }

        if (!string.Equals(ledger.SourceSnapshotHash, sourceSnapshotHash, StringComparison.Ordinal))
        {
            return PlatformResult<DispositionLedger>.Fail(409,
                "That ledger records decisions about a different source snapshot than this run would read, so they are not decisions " +
                "about what it would generate. Record a ledger from a run over this source, or start this run over the source those " +
                "decisions were made against.");
        }

        return await StaleReasonAsync(ledger, cancellationToken).ConfigureAwait(false) is { } stale
            ? PlatformResult<DispositionLedger>.Fail(409,
                $"{stale} A run started under it would generate from source nobody reviewed.")
            : PlatformResult<DispositionLedger>.Ok(ledger);
    }

    /// <summary>
    /// Issues the server's own statement of which recorded dispositions cover generating from one run's
    /// source under the target mapping that run supplies.
    ///
    /// Every input is the server's: the ledger, its entries, the run, and the manifest bytes read out of
    /// the run's own workspace. The caller supplies two identifiers and nothing else. The result carries
    /// no verdict field — it carries the decisions and the digests, and the generation phase re-adjudicates
    /// them — so an authorization that leaked could still not make an undecided property look decided.
    ///
    /// It refuses while the ledger is stale, refuses when the run reads different source, and refuses when
    /// any property the mapping rests on is unresolved or deferred.
    /// </summary>
    public async Task<PlatformResult<GenerationAuthorization>> AuthorizeGenerationAsync(
        WorkbenchActor actor,
        string ledgerId,
        string? runId,
        CancellationToken cancellationToken)
    {
        PlatformResult<DispositionLedger> resolved = await ResolveAsync(
            actor, ledgerId, WorkbenchRoles.MigrationOperator, cancellationToken).ConfigureAwait(false);
        if (!resolved.Succeeded)
        {
            return PlatformResult<GenerationAuthorization>.Fail(resolved.Status, resolved.Error);
        }

        DispositionLedger ledger = resolved.Value!;

        if (await StaleReasonAsync(ledger, cancellationToken).ConfigureAwait(false) is { } stale)
        {
            return PlatformResult<GenerationAuthorization>.Fail(409,
                $"{stale} Generating under decisions about superseded source would ship an application nobody reviewed the source of.");
        }

        if (string.IsNullOrWhiteSpace(runId))
        {
            return PlatformResult<GenerationAuthorization>.Fail(400, "An authorization is bound to one run. Name the run.");
        }

        MigrationRunRecord? run = await runs.GetAsync(actor.TenantId, runId, cancellationToken).ConfigureAwait(false);
        if (run is null || !string.Equals(run.ProjectId, ledger.ProjectId, StringComparison.Ordinal))
        {
            return PlatformResult<GenerationAuthorization>.Fail(404, "No run with that identifier belongs to this ledger's project.");
        }

        if (!string.Equals(run.SourceSnapshotHash, ledger.SourceSnapshotHash, StringComparison.Ordinal))
        {
            return PlatformResult<GenerationAuthorization>.Fail(409,
                "That run reads a different source snapshot than the one these decisions were recorded against, so they are not " +
                "decisions about what it would generate.");
        }

        string sourceRoot = WorkspacePath.Normalize(run.Request.SourceRoot);
        string manifestPath = $"{sourceRoot}/{TargetMappingReader.ConventionalPath}";
        string? workspaceRoot = workspaces.ResolveRoot(run.WorkspaceOwnerId, run.WorkspaceId)
            ?? workspaces.ResolveDurableRoot(run.WorkspaceId);

        if (workspaceRoot is null || !TryResolveWithin(workspaceRoot, sourceRoot, manifestPath, out string manifestAbsolute))
        {
            return PlatformResult<GenerationAuthorization>.Fail(410,
                $"The target mapping at '{manifestPath}' is not readable in this run's workspace, so which source objects a " +
                "generation would rest on cannot be established.");
        }

        string manifestDigest;
        string manifestJson;
        try
        {
            FileInfo manifestFile = new(manifestAbsolute);
            if (manifestFile.Length > TargetMappingReader.MaxManifestBytes)
            {
                return PlatformResult<GenerationAuthorization>.Fail(413,
                    "The target mapping is larger than this server reads, so it was not read at all.");
            }

            byte[] bytes = await File.ReadAllBytesAsync(manifestAbsolute, cancellationToken).ConfigureAwait(false);
            manifestDigest = Convert.ToHexStringLower(SHA256.HashData(bytes));
            manifestJson = Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF');
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return PlatformResult<GenerationAuthorization>.Fail(500, "The target mapping this run supplies could not be read.");
        }

        (IReadOnlyList<MappedSourceReference>? sources, IReadOnlyList<string> rejections) =
            TargetMappingReader.ReadAnchors(manifestJson);

        if (sources is null)
        {
            return PlatformResult<GenerationAuthorization>.Fail(409,
                $"The target mapping was refused before any coverage was computed: {string.Join(" ", rejections)}");
        }

        MappedSourceReference[] formsAnchors =
        [
            .. sources.Where(source =>
                source.Kind == MappedSourceKind.FormsSourceObject && source.Module is { Length: > 0 }),
        ];

        if (formsAnchors.Length == 0)
        {
            return PlatformResult<GenerationAuthorization>.Fail(409,
                "That run's target mapping cites no Oracle Forms source object, so it claims to carry no behaviour from the modules " +
                "this ledger records. An authorization over it would say nothing about what would be generated.");
        }

        IReadOnlyList<DispositionLedgerEntry> entries = await store
            .DispositionLedgerEntriesAsync(actor.TenantId, ledgerId, cancellationToken).ConfigureAwait(false);

        // Every row this ledger holds, not the rows a mapping anchor reaches. A trigger declared beside a
        // mapped block is behaviour the generated application would either carry or drop; scoping it out
        // of the authorization would let it be dropped with nobody having decided to.
        GenerationScopedDecision[] scope =
        [
            .. entries
                .OrderBy(entry => entry.EntryId, StringComparer.Ordinal)
                .Select(entry => new GenerationScopedDecision(
                    entry.EntryId,
                    entry.Identity.FilePath,
                    entry.Identity.ObjectPath,
                    entry.Identity.PropertyName,
                    entry.Decision,
                    entry.MappingRuleId,
                    entry.MappingRuleVersion,
                    entry.DecisionRevision)),
        ];

        if (scope.Length > GenerationCoverage.MaxScopeEntries)
        {
            return PlatformResult<GenerationAuthorization>.Fail(409,
                $"This ledger holds {scope.Length.ToString(CultureInfo.InvariantCulture)} declared properties, beyond the " +
                $"{GenerationCoverage.MaxScopeEntries.ToString(CultureInfo.InvariantCulture)} one authorization carries. Narrow the " +
                "source this ledger was read from rather than authorizing a subset silently.");
        }

        GenerationAuthorization authorization = new()
        {
            LedgerId = ledger.LedgerId,
            TenantId = ledger.TenantId,
            ProjectId = ledger.ProjectId,
            RunId = run.RunId,
            SourceSnapshotHash = ledger.SourceSnapshotHash,
            IntermediateContentSha256 = ledger.IntermediateContentSha256,
            MappingManifestSha256 = manifestDigest,
            IssuedUtc = _clock(),
            Scope = scope,
            LedgerEntryCount = entries.Count,
            ScopeDigest = GenerationCoverage.ScopeDigest(scope),
        };

        // Issued only when the same rules the generation phase applies already pass here, so an operator
        // is told what is undecided before a run starts rather than by a phase failing halfway through.
        if (GenerationCoverage.Reject(
                authorization,
                sources,
                [.. scope.Select(entry => entry.ModulePath).Distinct(StringComparer.Ordinal)],
                ledger.IntermediateContentSha256,
                manifestDigest)
            is { Count: > 0 } blockers)
        {
            return PlatformResult<GenerationAuthorization>.Fail(409, string.Join(" ", blockers));
        }

        return PlatformResult<GenerationAuthorization>.Ok(authorization);
    }

    /// <summary>
    /// Records what a completed generation actually wrote, against the entries it was covered by.
    ///
    /// The run's coverage record says which files were emitted; it is not believed about their content.
    /// Every listed file is re-hashed here, out of the run's own workspace, and the set digest recomputed;
    /// a file edited after the phase ran no longer matches and nothing is recorded. Nothing becomes
    /// verified: generation is not execution, so every entry this records against is returned to
    /// <see cref="DispositionVerificationStatus.NotExecuted"/> and any earlier test result is dropped,
    /// because a result proved against the previous output proves nothing about this one. An identical
    /// re-recording of the same output by the same owner changes no row and therefore drops nothing.
    /// </summary>
    public async Task<PlatformResult<DispositionLedgerGeneration>> RecordGenerationFromRunAsync(
        string tenantId,
        string ledgerId,
        string runId,
        CancellationToken cancellationToken) =>
        await RecordGenerationFromRunAsync(tenantId, ledgerId, runId, manifest: null, ownership: null, cancellationToken)
            .ConfigureAwait(false);

    /// <summary>
    /// The same recording, against an artifact manifest the server has already hashed but not yet stored.
    ///
    /// The durable worker builds that manifest itself, out of the files on disk, at the moment the run
    /// stops. Reading it here rather than from the store lets the worker find out whether the evidence
    /// records before it declares the run a success, so a generation whose evidence could not be recorded
    /// does not become a green run with nothing behind it. Passing null reads the stored manifest, which
    /// is what an operator-initiated recording does.
    /// </summary>
    public async Task<PlatformResult<DispositionLedgerGeneration>> RecordGenerationFromRunAsync(
        string tenantId,
        string ledgerId,
        string runId,
        IReadOnlyList<MigrationRunArtifact>? manifest,
        CancellationToken cancellationToken) =>
        await RecordGenerationFromRunAsync(tenantId, ledgerId, runId, manifest, ownership: null, cancellationToken)
            .ConfigureAwait(false);

    /// <summary>
    /// The same recording, performed by the worker that still holds the run's lease.
    ///
    /// <paramref name="ownership"/> is the claim that worker made. It is not taken as fact: the run store
    /// is asked to renew that exact fence immediately before the ledger is written, so a worker a newer
    /// claim already displaced is refused. The renewal and the ledger write are not one transaction —
    /// runs and ledgers are separate stores — so the reference also carries the fence it was written
    /// under, and <see cref="AdmissibleAsync"/> reads back only references still matching the run's fence.
    /// A displaced owner that wins the race therefore writes something durably inert, never partial.
    /// </summary>
    public async Task<PlatformResult<DispositionLedgerGeneration>> RecordGenerationFromRunAsync(
        string tenantId,
        string ledgerId,
        string runId,
        IReadOnlyList<MigrationRunArtifact>? manifest,
        MigrationRunOwnership? ownership,
        CancellationToken cancellationToken)
    {
        DispositionLedger? ledger =
            await store.GetDispositionLedgerAsync(tenantId, ledgerId, cancellationToken).ConfigureAwait(false);
        if (ledger is null)
        {
            return PlatformResult<DispositionLedgerGeneration>.Fail(404, "No ledger with that identifier exists in this tenant.");
        }

        MigrationRunRecord? run = await runs.GetAsync(tenantId, runId, cancellationToken).ConfigureAwait(false);
        if (run is null || !string.Equals(run.ProjectId, ledger.ProjectId, StringComparison.Ordinal))
        {
            return PlatformResult<DispositionLedgerGeneration>.Fail(404, "No run with that identifier belongs to this ledger's project.");
        }

        if (!string.Equals(run.SourceSnapshotHash, ledger.SourceSnapshotHash, StringComparison.Ordinal))
        {
            return PlatformResult<DispositionLedgerGeneration>.Fail(409,
                "That run read a different source snapshot, so what it generated is not evidence about this ledger's source.");
        }

        if (await StaleReasonAsync(ledger, cancellationToken).ConfigureAwait(false) is { } stale)
        {
            return PlatformResult<DispositionLedgerGeneration>.Fail(409,
                $"{stale} Recording a generation against decisions about superseded source would enter an artifact nobody " +
                "reviewed the source of as evidence about the source that stands.");
        }

        // The only path a coverage record may be read from is the one this run's own request declared as
        // its output root. A record found somewhere else under the workspace describes some other output.
        string expectedOutputRoot = WorkspacePath.Normalize(run.Request.OutputRoot);
        string expectedCoveragePath = $"{expectedOutputRoot}/{GenerationCoverage.RecordPath}";

        IReadOnlyList<MigrationRunArtifact> artifacts = manifest
            ?? await runs.ArtifactsAsync(tenantId, run.RunId, cancellationToken).ConfigureAwait(false);

        MigrationRunArtifact[] candidates =
        [
            .. artifacts.Where(artifact =>
                string.Equals(artifact.RunId, run.RunId, StringComparison.Ordinal) &&
                string.Equals(WorkspacePath.Normalize(artifact.Path), expectedCoveragePath, StringComparison.Ordinal)),
        ];

        if (candidates.Length != 1)
        {
            return PlatformResult<DispositionLedgerGeneration>.Fail(409, candidates.Length == 0
                ? "That run recorded no generation coverage at its own output root, so nothing generated in it is bound to a recorded decision."
                : "That run recorded more than one generation coverage, so which generation it describes is ambiguous.");
        }

        string? workspaceRoot = workspaces.ResolveRoot(run.WorkspaceOwnerId, run.WorkspaceId)
            ?? workspaces.ResolveDurableRoot(run.WorkspaceId);

        if (workspaceRoot is null)
        {
            return PlatformResult<DispositionLedgerGeneration>.Fail(410,
                "The run's artifact manifest is retained, but the workspace bytes it describes have expired.");
        }

        PlatformResult<GenerationCoverageRecord> coverage =
            await ReadCoverageAsync(workspaceRoot, candidates[0], cancellationToken).ConfigureAwait(false);
        if (!coverage.Succeeded)
        {
            return PlatformResult<DispositionLedgerGeneration>.Fail(coverage.Status, coverage.Error);
        }

        GenerationCoverageRecord record = coverage.Value!;

        if (!string.Equals(record.LedgerId, ledger.LedgerId, StringComparison.Ordinal) ||
            !string.Equals(record.TenantId, ledger.TenantId, StringComparison.Ordinal) ||
            !string.Equals(record.ProjectId, ledger.ProjectId, StringComparison.Ordinal) ||
            !string.Equals(record.RunId, run.RunId, StringComparison.Ordinal) ||
            !string.Equals(record.SourceSnapshotHash, ledger.SourceSnapshotHash, StringComparison.Ordinal) ||
            !string.Equals(record.IntermediateContentSha256, ledger.IntermediateContentSha256, StringComparison.OrdinalIgnoreCase))
        {
            return PlatformResult<DispositionLedgerGeneration>.Fail(409,
                "That generation was covered by a different tenant, project, ledger, run, or source than this one, so it is not evidence here.");
        }

        if (!string.Equals(WorkspacePath.Normalize(record.OutputRoot), expectedOutputRoot, StringComparison.Ordinal))
        {
            return PlatformResult<DispositionLedgerGeneration>.Fail(409,
                "That generation names an output root this run did not write to, so the files it describes are not the files this run " +
                "produced. Nothing was hashed and nothing was recorded.");
        }

        if (record.OutputFiles.Count == 0 || record.OutputFiles.Count > GenerationCoverage.MaxOutputFiles)
        {
            return PlatformResult<DispositionLedgerGeneration>.Fail(409,
                "That generation coverage describes no emitted file, or more than this server re-hashes.");
        }

        if (record.OutputFiles.Distinct(StringComparer.Ordinal).Count() != record.OutputFiles.Count)
        {
            return PlatformResult<DispositionLedgerGeneration>.Fail(409,
                "That generation coverage lists the same emitted file more than once, so the digest it claims is over a set this " +
                "server cannot reproduce.");
        }

        IReadOnlyList<DispositionLedgerEntry> entries = await store
            .DispositionLedgerEntriesAsync(tenantId, ledgerId, cancellationToken).ConfigureAwait(false);

        if (RejectDecisionState(record, entries) is { } divergence)
        {
            return PlatformResult<DispositionLedgerGeneration>.Fail(409, divergence);
        }

        string outputRoot = expectedOutputRoot;
        List<(string Path, string ContentSha256)> hashed = [];

        foreach (string relative in record.OutputFiles)
        {
            string full = $"{outputRoot}/{relative}";

            if (string.Equals(WorkspacePath.Normalize(full), expectedCoveragePath, StringComparison.Ordinal))
            {
                return PlatformResult<DispositionLedgerGeneration>.Fail(409,
                    "That generation coverage lists itself among the files it describes, which no document can be a true digest of.");
            }

            if (!TryResolveRunArtifact(workspaceRoot, full, out string absolute))
            {
                return PlatformResult<DispositionLedgerGeneration>.Fail(409,
                    $"The generated file '{full}' is no longer readable in this run's output, so the generation it was part of " +
                    "cannot be recorded from its bytes.");
            }

            try
            {
                byte[] bytes = await File.ReadAllBytesAsync(absolute, cancellationToken).ConfigureAwait(false);
                hashed.Add((relative, Convert.ToHexStringLower(SHA256.HashData(bytes))));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return PlatformResult<DispositionLedgerGeneration>.Fail(500, $"The generated file '{full}' could not be read.");
            }
        }

        string observed = GenerationCoverage.OutputSetDigest(hashed);
        if (!string.Equals(observed, record.OutputSetSha256, StringComparison.OrdinalIgnoreCase))
        {
            return PlatformResult<DispositionLedgerGeneration>.Fail(409,
                "The generated files no longer digest to what the generation recorded, so they are not the output that generation " +
                "produced. Nothing was recorded against them.");
        }

        HashSet<string> covered = new(record.CoveredEntries.Select(entry => entry.EntryId), StringComparer.Ordinal);
        DateTimeOffset recordedUtc = _clock();

        // One artifact set, but a separate reference per property, each carrying the decision revision
        // that property was generated under. The scope digest checked above already proved those are the
        // revisions the generation was covered by, so this is the server's own reading of its rows rather
        // than a claim taken from the record.
        DispositionLedgerEntryUpdate[] updates =
        [
            .. entries
                .Where(entry => covered.Contains(entry.EntryId))
                .Select(entry => (Entry: entry, Reference: new DispositionGeneratedReference(
                    run.RunId, $"{outputRoot}/application", observed, recordedUtc, entry.DecisionRevision, run.FenceToken)))
                .Where(pair => !Carries(pair.Entry, pair.Reference))
                .Select(pair =>
                {
                    DispositionGeneratedReference[] generated = [.. pair.Entry.GeneratedRefs, pair.Reference];

                    // A different artifact set, a different run, or a different owner than whatever was
                    // executed before. Results about the previous output prove nothing about this one, so
                    // they stop standing here rather than letting a new generation inherit a Passed
                    // nobody re-established against it. The references stay as the audit trail.
                    return new DispositionLedgerEntryUpdate(
                        pair.Entry with
                        {
                            GeneratedRefs = generated,
                            Verification = DispositionLedgerRules
                                .AdmitEvidence(pair.Entry, generated, pair.Entry.TestRefs).Verification,
                        },
                        pair.Entry.Version);
                }),
        ];

        if (ownership is not null &&
            (run.FenceToken != ownership.FenceToken ||
             !await runs.RenewAsync(run.RunId, ownership.FenceToken, ownership.LeaseExtension, cancellationToken)
                 .ConfigureAwait(false)))
        {
            return PlatformResult<DispositionLedgerGeneration>.Fail(409,
                "This run is no longer held by the worker recording it, so what it generated was not entered against any " +
                "decision. Nothing was written.");
        }

        // One all-or-nothing write over every covered property, each against the version read above. The
        // properties a generation covered are one fact about one artifact set, so a set that could only be
        // applied in part is applied not at all.
        if (await store.UpdateDispositionLedgerEntriesAsync(updates, cancellationToken).ConfigureAwait(false) is null)
        {
            return PlatformResult<DispositionLedgerGeneration>.Fail(409,
                "A property changed while this generation was being recorded against it, so no property was changed. " +
                "Record it again.");
        }

        return PlatformResult<DispositionLedgerGeneration>.Ok(
            new DispositionLedgerGeneration(ledger.LedgerId, run.RunId, observed, record.OutputFiles.Count, updates.Length));
    }

    /// <summary>
    /// Grants a verification phase the generation it may speak about, or every reason it has none.
    ///
    /// This is what stands between a run and a connection to a customer database. It answers only from a
    /// generation the ledger already holds: the references recorded against this run, under the claim this
    /// worker still owns, over the exact bytes the phase says it is looking at. A run whose generation was
    /// never recorded, whose output has changed since, or whose worker a newer claim displaced gets no
    /// claim, so it reads nothing and produces no evidence it could not have attached to anything.
    ///
    /// The decisions carried back are the rows as they stand now, not as the run's own coverage document
    /// described them. A property re-decided between generating and verifying therefore drops out of the
    /// claim, and the phase plans no case for it rather than proving something about a decision nobody
    /// holds.
    /// </summary>
    public async Task<EntryVerificationClaimDecision> ClaimGenerationForVerificationAsync(
        string tenantId,
        string ledgerId,
        string runId,
        string observedOutputSetSha256,
        MigrationRunOwnership? ownership,
        CancellationToken cancellationToken)
    {
        DispositionLedger? ledger =
            await store.GetDispositionLedgerAsync(tenantId, ledgerId, cancellationToken).ConfigureAwait(false);
        if (ledger is null)
        {
            return EntryVerificationClaimDecision.Denied("No ledger with that identifier exists in this tenant.");
        }

        MigrationRunRecord? run = await runs.GetAsync(tenantId, runId, cancellationToken).ConfigureAwait(false);
        if (run is null || !string.Equals(run.ProjectId, ledger.ProjectId, StringComparison.Ordinal))
        {
            return EntryVerificationClaimDecision.Denied("No run with that identifier belongs to this ledger's project.");
        }

        if (!string.Equals(run.SourceSnapshotHash, ledger.SourceSnapshotHash, StringComparison.Ordinal))
        {
            return EntryVerificationClaimDecision.Denied(
                "That run read a different source snapshot, so nothing it generated is evidence about this ledger's source.");
        }

        if (await StaleReasonAsync(ledger, cancellationToken).ConfigureAwait(false) is { } stale)
        {
            return EntryVerificationClaimDecision.Denied(stale);
        }

        if (string.IsNullOrWhiteSpace(run.TargetProfileId))
        {
            return EntryVerificationClaimDecision.Denied(
                "This run names no target profile, so there is no immutable record of the destination a verification would read.");
        }

        PlatformTargetProfile? profile = await store.GetTargetProfileAsync(
            tenantId, ledger.ProjectId, run.TargetProfileId, run.TargetProfileVersion, cancellationToken)
            .ConfigureAwait(false);

        if (profile is null)
        {
            return EntryVerificationClaimDecision.Denied(
                "The target profile this run was accepted against is no longer readable, so the destination a verification " +
                "would read cannot be confirmed.");
        }

        if (!string.Equals(profile.CanonicalHash, run.TargetProfileHash, StringComparison.Ordinal))
        {
            return EntryVerificationClaimDecision.Denied(
                "The target profile no longer matches the exact profile this run was accepted against, so the destination a " +
                "verification would read cannot be confirmed.");
        }

        IReadOnlyList<DispositionLedgerEntry> entries = await store
            .DispositionLedgerEntriesAsync(tenantId, ledgerId, cancellationToken).ConfigureAwait(false);

        List<GenerationCoveredEntry> covered = [];
        foreach (DispositionLedgerEntry entry in entries)
        {
            DispositionGeneratedReference? standing =
                DispositionLedgerRules.StandingGeneration(entry.DecisionRevision, entry.GeneratedRefs);

            if (standing is null ||
                !string.Equals(standing.RunId, run.RunId, StringComparison.Ordinal) ||
                standing.RunFenceToken != run.FenceToken ||
                !string.Equals(standing.ContentSha256, observedOutputSetSha256, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            covered.Add(new GenerationCoveredEntry(
                entry.EntryId, entry.DecisionRevision, entry.Decision, entry.MappingRuleId, entry.MappingRuleVersion));
        }

        if (covered.Count == 0)
        {
            return EntryVerificationClaimDecision.Denied(
                "This ledger records no generation produced by this run, under the claim it still holds, over the output now on " +
                "disk. Either the generation was never recorded, the decisions have moved since, or the files have changed.");
        }

        // The claim is re-established against the run store here, immediately before the phase is told it
        // may read. A worker a newer claim already displaced never reaches the target.
        if (ownership is not null &&
            (run.FenceToken != ownership.FenceToken ||
             !await runs.RenewAsync(run.RunId, ownership.FenceToken, ownership.LeaseExtension, cancellationToken)
                 .ConfigureAwait(false)))
        {
            return EntryVerificationClaimDecision.Denied(
                "This run is no longer held by the worker asking, so it was given no claim and read nothing.");
        }

        return EntryVerificationClaimDecision.Granted(new EntryVerificationGenerationClaim(
            ledger.LedgerId,
            ledger.TenantId,
            ledger.ProjectId,
            run.RunId,
            ledger.SourceSnapshotHash,
            observedOutputSetSha256,
            new EntryVerificationTargetBinding(
                profile.EndpointHost, profile.DatabaseName, profile.SchemaName, profile.ExecutionIdentity),
            covered));
    }

    /// <summary>
    /// Records what a run's trusted verifier actually read from the approved target, per recorded
    /// decision, from the run's own retained evidence.
    ///
    /// This is the only production route by which an executed result reaches a property, and everything
    /// it adds is a check on where the evidence came from. The cases are read out of a document retained
    /// in the run's manifest and re-hashed here, the output they were read against is re-derived from the
    /// run's own generation coverage rather than believed, the destination is compared with the project's
    /// immutable target profile, and every case is matched to the row it names by identity as well as by
    /// identifier. The binding itself — decision revision, source snapshot, generated content, run, and
    /// fence — is adjudicated by the same rules the single-entry path uses, so there is one definition of
    /// what makes a result evidence and this path cannot relax it. The write is one all-or-nothing update
    /// over every property named, because one verification is one fact about one output set.
    ///
    /// Nothing about an aggregate test report reaches here. A JUnit file says how many generated tests
    /// ran; it does not say which recorded decision any of them was about, so no count from one can move a
    /// property off unexecuted.
    /// </summary>
    public async Task<PlatformResult<DispositionLedgerVerification>> RecordVerificationFromRunAsync(
        string tenantId,
        string ledgerId,
        string runId,
        IReadOnlyList<MigrationRunArtifact>? manifest,
        MigrationRunOwnership? ownership,
        CancellationToken cancellationToken)
    {
        DispositionLedger? ledger =
            await store.GetDispositionLedgerAsync(tenantId, ledgerId, cancellationToken).ConfigureAwait(false);
        if (ledger is null)
        {
            return Refuse(404, "No ledger with that identifier exists in this tenant.");
        }

        MigrationRunRecord? run = await runs.GetAsync(tenantId, runId, cancellationToken).ConfigureAwait(false);
        if (run is null || !string.Equals(run.ProjectId, ledger.ProjectId, StringComparison.Ordinal))
        {
            return Refuse(404, "No run with that identifier belongs to this ledger's project.");
        }

        if (!string.Equals(run.SourceSnapshotHash, ledger.SourceSnapshotHash, StringComparison.Ordinal))
        {
            return Refuse(409,
                "That run read a different source snapshot, so what it executed is not evidence about this ledger's source.");
        }

        if (await StaleReasonAsync(ledger, cancellationToken).ConfigureAwait(false) is { } stale)
        {
            return Refuse(409,
                $"{stale} Recording execution against decisions about superseded source would enter a result nobody reviewed the " +
                "source of as evidence about the source that stands.");
        }

        string expectedOutputRoot = WorkspacePath.Normalize(run.Request.OutputRoot);
        IReadOnlyList<MigrationRunArtifact> artifacts = manifest
            ?? await runs.ArtifactsAsync(tenantId, run.RunId, cancellationToken).ConfigureAwait(false);

        MigrationRunArtifact? verification = Single(artifacts, run.RunId, $"{expectedOutputRoot}/{EntryVerificationCoverage.RecordPath}");
        MigrationRunArtifact? generation = Single(artifacts, run.RunId, $"{expectedOutputRoot}/{GenerationCoverage.RecordPath}");

        if (verification is null)
        {
            return Refuse(409,
                "That run retained no per-entry verification record at its own output root, or retained more than one, so which " +
                "execution it describes is not established. Nothing was recorded.");
        }

        if (generation is null)
        {
            return Refuse(409,
                "That run retained no single generation coverage at its own output root, so the output its results were executed " +
                "against cannot be re-derived and no result was recorded.");
        }

        string? workspaceRoot = workspaces.ResolveRoot(run.WorkspaceOwnerId, run.WorkspaceId)
            ?? workspaces.ResolveDurableRoot(run.WorkspaceId);

        if (workspaceRoot is null)
        {
            return Refuse(410, "The run's manifest is retained, but the workspace bytes it describes have expired.");
        }

        PlatformResult<EntryVerificationCoverageRecord> read =
            await ReadEntryVerificationAsync(workspaceRoot, verification, cancellationToken).ConfigureAwait(false);
        if (!read.Succeeded)
        {
            return Refuse(read.Status, read.Error!);
        }

        EntryVerificationCoverageRecord record = read.Value!;

        if (!string.Equals(record.SchemaVersion, EntryVerificationCoverage.SchemaVersion, StringComparison.Ordinal) ||
            !string.Equals(record.LedgerId, ledger.LedgerId, StringComparison.Ordinal) ||
            !string.Equals(record.TenantId, ledger.TenantId, StringComparison.Ordinal) ||
            !string.Equals(record.ProjectId, ledger.ProjectId, StringComparison.Ordinal) ||
            !string.Equals(record.RunId, run.RunId, StringComparison.Ordinal) ||
            !string.Equals(record.SourceSnapshotHash, ledger.SourceSnapshotHash, StringComparison.Ordinal) ||
            !string.Equals(WorkspacePath.Normalize(record.OutputRoot), expectedOutputRoot, StringComparison.Ordinal))
        {
            return Refuse(409,
                "That verification was executed for a different tenant, project, ledger, run, source, or output root than this " +
                "one, or under a record version this build does not read, so it is not evidence here.");
        }

        if (record.Cases.Count == 0 || record.Cases.Count > EntryVerificationCoverage.MaxCases)
        {
            return Refuse(409,
                "That verification record describes no executed case, or more than this server records, so nothing was entered.");
        }

        if (record.Cases.Select(item => $"{item.EntryId}\u0000{item.TestId}").Distinct(StringComparer.Ordinal).Count()
            != record.Cases.Count)
        {
            return Refuse(409,
                "That verification record names the same case on the same property more than once, so which result it reports is ambiguous.");
        }

        // Where it was read matters as much as what it said. A result taken from a database this project's
        // immutable profile does not name is a result about somewhere nobody approved, whatever it found.
        PlatformTargetProfile? profile = string.IsNullOrWhiteSpace(run.TargetProfileId)
            ? null
            : await store.GetTargetProfileAsync(
                tenantId, ledger.ProjectId, run.TargetProfileId, run.TargetProfileVersion, cancellationToken)
                .ConfigureAwait(false);

        if (profile is null)
        {
            return Refuse(409,
                "The target profile this run was accepted against is no longer readable, so the destination its results were read " +
                "from cannot be confirmed. Nothing was recorded.");
        }

        if (!string.Equals(profile.CanonicalHash, run.TargetProfileHash, StringComparison.Ordinal))
        {
            return Refuse(409,
                "The target profile no longer matches the exact profile this run was accepted against, so where its results were " +
                "read cannot be confirmed. Nothing was recorded.");
        }

        if (!record.Target.SameTargetAs(new EntryVerificationTargetBinding(
                profile.EndpointHost, profile.DatabaseName, profile.SchemaName, profile.ExecutionIdentity)))
        {
            return Refuse(409,
                "That verification was read from a destination this project's approved target profile does not name, so what it " +
                "observed is not evidence about the target this run was approved for. Nothing was recorded.");
        }

        // Re-derived, not believed. The digest the cases were executed against has to be the digest this
        // server computes from the run's own generation, or the results describe output that has since
        // been replaced.
        PlatformResult<string> tested =
            await TestedOutputDigestAsync(workspaceRoot, expectedOutputRoot, generation, cancellationToken).ConfigureAwait(false);
        if (!tested.Succeeded)
        {
            return Refuse(tested.Status, tested.Error!);
        }

        if (!string.Equals(record.TestedOutputSetSha256, tested.Value, StringComparison.OrdinalIgnoreCase))
        {
            return Refuse(409,
                "The cases in that record name different generated content than this run's output now digests to, so they were " +
                "executed against output this run has replaced. Nothing was recorded.");
        }

        IReadOnlyList<DispositionLedgerEntry> entries = await store
            .DispositionLedgerEntriesAsync(tenantId, ledgerId, cancellationToken).ConfigureAwait(false);
        Dictionary<string, DispositionLedgerEntry> byId = entries.ToDictionary(entry => entry.EntryId, StringComparer.Ordinal);

        foreach (EntryVerificationCase item in record.Cases)
        {
            if (!byId.TryGetValue(item.EntryId, out DispositionLedgerEntry? entry))
            {
                return Refuse(409, "That verification names a property this ledger does not record, so it describes a different ledger's source.");
            }

            if (!string.Equals(entry.Identity.FilePath, item.ModulePath, StringComparison.Ordinal) ||
                !string.Equals(entry.Identity.ObjectPath, item.ObjectPath, StringComparison.Ordinal) ||
                !string.Equals(entry.Identity.PropertyName, item.PropertyName, StringComparison.Ordinal))
            {
                return Refuse(409,
                    "A case names a property whose identity does not match the row it claims, so what was executed and what it " +
                    "would be recorded against are two different properties.");
            }

            // A retirement is a decision to drop the property. A case that passed because the target
            // happens to hold the object would read back as the property having been preserved, so it is
            // refused here rather than admitted as evidence about a decision it does not describe.
            if (entry.Decision is not (DispositionDecision.Preserve or DispositionDecision.Transform))
            {
                return Refuse(409,
                    $"A case was executed for a property recorded {entry.Decision}, which is not a decision to carry it into the " +
                    "target. Recording it would present that decision as a preservation, so nothing was entered.");
            }

            if (!string.Equals(item.DecisionRevision, entry.DecisionRevision, StringComparison.Ordinal))
            {
                return Refuse(409,
                    "A case names a different disposition of its property than the one recorded now, so it proves something about " +
                    "a decision nobody holds any more. Generate and verify again under the decision that stands.");
            }

            if (item.Outcome == DispositionVerificationStatus.NotExecuted)
            {
                return Refuse(409,
                    "A case in that record reports nothing having been executed. An unexecuted case is a gap, and a gap is not " +
                    "recorded as a result about a property.");
            }
        }

        // The claim this worker holds is re-established against the run store immediately before anything
        // is written, exactly as recording a generation does. A worker a newer claim already displaced
        // writes nothing.
        if (ownership is not null &&
            (run.FenceToken != ownership.FenceToken ||
             !await runs.RenewAsync(run.RunId, ownership.FenceToken, ownership.LeaseExtension, cancellationToken)
                 .ConfigureAwait(false)))
        {
            return Refuse(409,
                "This run is no longer held by the worker recording it, so what it executed was not entered against any decision. " +
                "Nothing was written.");
        }

        long fence = ownership?.FenceToken ?? run.FenceToken;
        DateTimeOffset recordedUtc = _clock();
        List<DispositionLedgerEntryUpdate> updates = [];

        // One verification is one fact about one output set, so it is applied to every property it names
        // or to none of them. The per-entry path adjudicates the same bindings one row at a time and can
        // leave half a verification standing when a row moves underneath it; this reads the rows once,
        // applies the same audit, and writes them together against the versions it read.
        foreach (IGrouping<string, EntryVerificationCase> group in record.Cases.GroupBy(item => item.EntryId, StringComparer.Ordinal))
        {
            DispositionLedgerEntry entry = byId[group.Key];

            DispositionGeneratedReference? standing =
                DispositionLedgerRules.StandingGeneration(entry.DecisionRevision, entry.GeneratedRefs);

            if (standing is null ||
                !string.Equals(standing.RunId, run.RunId, StringComparison.Ordinal) ||
                standing.RunFenceToken != run.FenceToken)
            {
                return Refuse(409,
                    "The generation that stands for a property in that record is not the one this run owns, so a result read " +
                    "against some other output was not entered as evidence about it. Record this run's generation first.");
            }

            DispositionTestReference[] offered =
            [
                .. group.Select(item => new DispositionTestReference(
                    run.RunId,
                    item.TestId,
                    item.Outcome,
                    item.Detail,
                    recordedUtc,
                    item.DecisionRevision,
                    record.TestedOutputSetSha256,
                    ledger.SourceSnapshotHash,
                    fence)),
            ];

            foreach (DispositionTestReference test in offered)
            {
                if (DispositionLedgerRules.RejectTestBinding(entry, standing, test) is { } refused)
                {
                    return Refuse(409, refused);
                }
            }

            // Re-recording the identical result is the same fact arriving twice, so it changes nothing.
            DispositionTestReference[] tests =
                [.. entry.TestRefs, .. offered.Where(test => !Carries(entry, test))];

            updates.Add(new DispositionLedgerEntryUpdate(
                entry with
                {
                    TestRefs = tests,
                    Verification = DispositionLedgerRules.AdmitEvidence(entry, entry.GeneratedRefs, tests).Verification,
                },
                entry.Version));
        }

        if (await store.UpdateDispositionLedgerEntriesAsync(updates, cancellationToken).ConfigureAwait(false) is null)
        {
            return Refuse(409,
                "A property changed while this verification was being recorded against it, so no property was changed. " +
                "Record it again.");
        }

        int passed = record.Cases.Count(item => item.Outcome == DispositionVerificationStatus.Passed);

        return PlatformResult<DispositionLedgerVerification>.Ok(new DispositionLedgerVerification(
            ledger.LedgerId,
            run.RunId,
            record.TestedOutputSetSha256,
            updates.Count,
            record.Cases.Count,
            passed,
            record.Cases.Count - passed,
            record.Gaps.Count));

        static PlatformResult<DispositionLedgerVerification> Refuse(int status, string error) =>
            PlatformResult<DispositionLedgerVerification>.Fail(status, error);

        static MigrationRunArtifact? Single(IReadOnlyList<MigrationRunArtifact> all, string runId, string path)
        {
            MigrationRunArtifact[] matches =
            [
                .. all.Where(artifact =>
                    string.Equals(artifact.RunId, runId, StringComparison.Ordinal) &&
                    string.Equals(WorkspacePath.Normalize(artifact.Path), path, StringComparison.Ordinal)),
            ];

            return matches.Length == 1 ? matches[0] : null;
        }
    }

    /// <summary>
    /// The digest of the output a run's results may speak about: re-hashed here from the file list its own
    /// generation coverage recorded, never taken from the verification record.
    /// </summary>
    private static async Task<PlatformResult<string>> TestedOutputDigestAsync(
        string workspaceRoot,
        string outputRoot,
        MigrationRunArtifact generation,
        CancellationToken cancellationToken)
    {
        PlatformResult<GenerationCoverageRecord> coverage =
            await ReadCoverageAsync(workspaceRoot, generation, cancellationToken).ConfigureAwait(false);
        if (!coverage.Succeeded)
        {
            return PlatformResult<string>.Fail(coverage.Status, coverage.Error);
        }

        GenerationCoverageRecord record = coverage.Value!;
        if (record.OutputFiles.Count == 0 || record.OutputFiles.Count > GenerationCoverage.MaxOutputFiles)
        {
            return PlatformResult<string>.Fail(409,
                "That run's generation coverage describes no emitted file, or more than this server re-hashes.");
        }

        List<(string Path, string ContentSha256)> hashed = [];
        foreach (string relative in record.OutputFiles)
        {
            if (!TryResolveRunArtifact(workspaceRoot, $"{outputRoot}/{relative}", out string absolute))
            {
                return PlatformResult<string>.Fail(409,
                    $"The generated file '{relative}' is no longer readable in this run's output, so what the results were executed " +
                    "against cannot be established from its bytes.");
            }

            try
            {
                byte[] bytes = await File.ReadAllBytesAsync(absolute, cancellationToken).ConfigureAwait(false);
                hashed.Add((relative, Convert.ToHexStringLower(SHA256.HashData(bytes))));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return PlatformResult<string>.Fail(500, $"The generated file '{relative}' could not be read.");
            }
        }

        return PlatformResult<string>.Ok(GenerationCoverage.OutputSetDigest(hashed));
    }

    private static async Task<PlatformResult<EntryVerificationCoverageRecord>> ReadEntryVerificationAsync(
        string workspaceRoot,
        MigrationRunArtifact artifact,
        CancellationToken cancellationToken)
    {
        if (!TryResolveRunArtifact(workspaceRoot, artifact.Path, out string absolute))
        {
            return PlatformResult<EntryVerificationCoverageRecord>.Fail(410,
                "The run's verification record is retained in its manifest, but the workspace bytes have expired.");
        }

        try
        {
            FileInfo file = new(absolute);
            if (file.Length != artifact.ByteLength || file.Length > MaxCoverageBytes)
            {
                return PlatformResult<EntryVerificationCoverageRecord>.Fail(409,
                    "The retained verification record no longer matches the size its run recorded.");
            }

            byte[] bytes = await File.ReadAllBytesAsync(absolute, cancellationToken).ConfigureAwait(false);
            if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(bytes), Convert.FromHexString(artifact.ContentSha256)))
            {
                return PlatformResult<EntryVerificationCoverageRecord>.Fail(409,
                    "The retained verification record no longer matches the digest its run recorded.");
            }

            EntryVerificationCoverageRecord? record =
                JsonSerializer.Deserialize<EntryVerificationCoverageRecord>(bytes, s_coverageJson);

            return record is null
                ? PlatformResult<EntryVerificationCoverageRecord>.Fail(409, "That run's verification record could not be read.")
                : PlatformResult<EntryVerificationCoverageRecord>.Ok(record);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or FormatException or JsonException)
        {
            return PlatformResult<EntryVerificationCoverageRecord>.Fail(409, "That run's verification record could not be read.");
        }
    }

    private static bool Carries(DispositionLedgerEntry entry, DispositionGeneratedReference reference) =>
        entry.GeneratedRefs.Any(existing =>
            string.Equals(existing.RunId, reference.RunId, StringComparison.Ordinal) &&
            string.Equals(existing.ArtifactPath, reference.ArtifactPath, StringComparison.Ordinal) &&
            string.Equals(existing.DecisionRevision, reference.DecisionRevision, StringComparison.Ordinal) &&
            existing.RunFenceToken == reference.RunFenceToken &&
            string.Equals(existing.ContentSha256, reference.ContentSha256, StringComparison.OrdinalIgnoreCase));

    private static bool Carries(DispositionLedgerEntry entry, DispositionTestReference test) =>
        entry.TestRefs.Any(existing =>
            string.Equals(existing.RunId, test.RunId, StringComparison.Ordinal) &&
            string.Equals(existing.TestId, test.TestId, StringComparison.Ordinal) &&
            existing.Outcome == test.Outcome &&
            existing.ProducerFenceToken == test.ProducerFenceToken &&
            string.Equals(existing.DecisionRevision, test.DecisionRevision, StringComparison.Ordinal) &&
            string.Equals(existing.SourceSnapshotHash, test.SourceSnapshotHash, StringComparison.Ordinal) &&
            string.Equals(existing.GeneratedContentSha256, test.GeneratedContentSha256, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The entries as a reader may be told them: a generated reference counts as evidence only while the
    /// fence it was written under is still the run's fence and the decision it was produced under is
    /// still the decision the row records, and a test result counts only while the exact artifact it
    /// names is the one that still stands.
    ///
    /// A worker displaced by a newer claim can still complete a write before it learns so. That write is
    /// atomic and its bytes were verified, but it was made by an owner that no longer speaks for the run,
    /// so it is suppressed here. Suppressing the artifact has to suppress the result proved about it too:
    /// a row reading "verified" with no admissible generation behind it is exactly the false clean result
    /// the ledger exists to prevent. Nothing is deleted — the row keeps what was written, and this
    /// projection stops presenting and counting it.
    /// </summary>
    private async Task<IReadOnlyList<DispositionLedgerEntry>> AdmissibleAsync(
        string tenantId,
        IReadOnlyList<DispositionLedgerEntry> entries,
        CancellationToken cancellationToken)
    {
        string[] runIds =
        [
            .. entries
                .SelectMany(entry => entry.GeneratedRefs)
                .Select(reference => reference.RunId)
                .Distinct(StringComparer.Ordinal),
        ];

        Dictionary<string, long> fences = new(StringComparer.Ordinal);
        foreach (string runId in runIds)
        {
            if (await runs.GetAsync(tenantId, runId, cancellationToken).ConfigureAwait(false) is { } run)
            {
                fences[runId] = run.FenceToken;
            }
        }

        return
        [
            .. entries.Select(entry =>
            {
                DispositionGeneratedReference[] admitted =
                [
                    .. DispositionLedgerRules.GenerationsUnder(entry.DecisionRevision, entry.GeneratedRefs)
                        .Where(reference =>
                            fences.TryGetValue(reference.RunId, out long fence) && fence == reference.RunFenceToken),
                ];

                (IReadOnlyList<DispositionTestReference> tests, DispositionVerificationStatus verification) =
                    DispositionLedgerRules.AdmitEvidence(entry, admitted, entry.TestRefs);

                return admitted.Length == entry.GeneratedRefs.Count &&
                    tests.Count == entry.TestRefs.Count &&
                    verification == entry.Verification
                        ? entry
                        : entry with { GeneratedRefs = admitted, TestRefs = tests, Verification = verification };
            }),
        ];
    }

    /// <summary>
    /// Why the decisions a generation was covered by are not the decisions this ledger now records, or
    /// null when they are the same decisions at the same revisions.
    ///
    /// Matching identifiers is not enough. A generation covered by Preserve, followed by an operator
    /// moving that property to Defer, would otherwise be recorded as an artifact for a decision nobody
    /// holds any more. The canonical digest includes each row's decision revision, so it changes when the
    /// decision changes — including a restatement under a new reason — and does not change when the
    /// server records evidence against the row.
    /// </summary>
    private static string? RejectDecisionState(
        GenerationCoverageRecord record,
        IReadOnlyList<DispositionLedgerEntry> entries)
    {
        if (record.CoveredEntries.Select(entry => entry.EntryId).Distinct(StringComparer.Ordinal).Count()
            != record.CoveredEntries.Count)
        {
            return "That generation names the same property more than once among the decisions it was covered by.";
        }

        HashSet<string> current = new(entries.Select(entry => entry.EntryId), StringComparer.Ordinal);
        HashSet<string> claimed = new(record.CoveredEntries.Select(entry => entry.EntryId), StringComparer.Ordinal);

        int unknown = claimed.Count(entryId => !current.Contains(entryId));
        if (unknown > 0)
        {
            return $"That generation claims to cover {unknown.ToString(CultureInfo.InvariantCulture)} propert" +
                $"{(unknown == 1 ? "y" : "ies")} this ledger does not record, so it describes a different ledger's source.";
        }

        int missing = current.Count(entryId => !claimed.Contains(entryId));
        if (missing > 0)
        {
            return $"That generation left {missing.ToString(CultureInfo.InvariantCulture)} of this ledger's recorded propert" +
                $"{(missing == 1 ? "y" : "ies")} uncovered. A generation that describes part of the source is not evidence about " +
                "the source, because the part it omits is exactly the part nobody would be told about.";
        }

        string currentDigest = GenerationCoverage.ScopeDigest(entries
            .OrderBy(entry => entry.EntryId, StringComparer.Ordinal)
            .Select(entry => new GenerationScopedDecision(
                entry.EntryId,
                entry.Identity.FilePath,
                entry.Identity.ObjectPath,
                entry.Identity.PropertyName,
                entry.Decision,
                entry.MappingRuleId,
                entry.MappingRuleVersion,
                entry.DecisionRevision)));

        return string.Equals(currentDigest, record.ScopeDigest, StringComparison.OrdinalIgnoreCase)
            ? null
            : "The decisions this ledger records are no longer the decisions that generation was covered by. A property decided one " +
                "way when the application was generated and decided another way now has no artifact for the decision that stands, so " +
                "nothing was recorded. Generate again under the decisions as they are.";
    }

    private static async Task<PlatformResult<GenerationCoverageRecord>> ReadCoverageAsync(
        string workspaceRoot,
        MigrationRunArtifact artifact,
        CancellationToken cancellationToken)
    {
        if (!TryResolveRunArtifact(workspaceRoot, artifact.Path, out string absolute))
        {
            return PlatformResult<GenerationCoverageRecord>.Fail(410,
                "The run's generation coverage is retained in its manifest, but the workspace bytes have expired.");
        }

        try
        {
            FileInfo file = new(absolute);
            if (file.Length != artifact.ByteLength || file.Length > MaxCoverageBytes)
            {
                return PlatformResult<GenerationCoverageRecord>.Fail(409,
                    "The retained generation coverage no longer matches the size its run recorded.");
            }

            byte[] bytes = await File.ReadAllBytesAsync(absolute, cancellationToken).ConfigureAwait(false);
            if (!CryptographicOperations.FixedTimeEquals(
                    SHA256.HashData(bytes), Convert.FromHexString(artifact.ContentSha256)))
            {
                return PlatformResult<GenerationCoverageRecord>.Fail(409,
                    "The retained generation coverage no longer matches the digest its run recorded.");
            }

            GenerationCoverageRecord? record = JsonSerializer.Deserialize<GenerationCoverageRecord>(bytes, s_coverageJson);

            return record is null
                ? PlatformResult<GenerationCoverageRecord>.Fail(409, "That run's generation coverage could not be read as a coverage record.")
                : PlatformResult<GenerationCoverageRecord>.Ok(record);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or FormatException or JsonException)
        {
            return PlatformResult<GenerationCoverageRecord>.Fail(409, "That run's generation coverage could not be read as a coverage record.");
        }
    }

    /// <summary>Resolves a workspace-relative path the server may read, constrained to one declared root.</summary>
    private static bool TryResolveWithin(string workspaceRoot, string containingRoot, string path, out string absolutePath)
    {
        absolutePath = string.Empty;

        string normalized = WorkspacePath.Normalize(path);
        if (WorkspacePath.Validate(path, "Path") is not null || !WorkspacePath.IsWithin(containingRoot, normalized))
        {
            return false;
        }

        WorkspaceWriter workspace = new(workspaceRoot);
        if (!workspace.TryResolve(normalized, out string resolved, out _) || !File.Exists(resolved))
        {
            return false;
        }

        absolutePath = resolved;
        return true;
    }

    private static PlatformResult<DispositionLedgerView> Fail(int status, string error) =>
        PlatformResult<DispositionLedgerView>.Fail(status, error);

    /// <summary>
    /// Resolves a manifest path to bytes this server may read: workspace-relative, inside the run's own
    /// output root, and inside the resolved workspace directory.
    ///
    /// It is deliberately not the console's preview resolver. That one additionally refuses the
    /// normalized representation by name, because the operator's browser is never handed the raw
    /// source-derived representation. The ledger is the server reading a run's own output, so the
    /// containment rules apply and the preview policy does not.
    /// </summary>
    private static bool TryResolveRunArtifact(string workspaceRoot, string path, out string absolutePath)
    {
        absolutePath = string.Empty;

        string normalized = WorkspacePath.Normalize(path);
        if (WorkspacePath.Validate(path, "Artifact path") is not null ||
            !WorkspacePath.IsWithin(WorkbenchExecution.OutputRoot, normalized))
        {
            return false;
        }

        WorkspaceWriter workspace = new(workspaceRoot);
        if (!workspace.TryResolve(normalized, out string resolved, out _) || !File.Exists(resolved))
        {
            return false;
        }

        absolutePath = resolved;
        return true;
    }

    private async Task<PlatformResult<DispositionLedger>> ResolveAsync(
        WorkbenchActor actor,
        string ledgerId,
        string? requiredRole,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);

        DispositionLedger? ledger =
            await store.GetDispositionLedgerAsync(actor.TenantId, ledgerId, cancellationToken).ConfigureAwait(false);

        if (ledger is null)
        {
            return PlatformResult<DispositionLedger>.Fail(404, "No ledger with that identifier is available to you.");
        }

        PlatformResult<PlatformMembership> membership = await platform
            .RequireMembershipAsync(actor, ledger.ProjectId, requiredRole, cancellationToken).ConfigureAwait(false);

        return membership.Succeeded
            ? PlatformResult<DispositionLedger>.Ok(ledger)
            : PlatformResult<DispositionLedger>.Fail(membership.Status, membership.Error);
    }

    /// <summary>
    /// Why this ledger no longer describes the source being migrated, or null while it still does.
    ///
    /// A newer completed run against different source means the decisions here were made about material
    /// nobody is converting. The rows are kept — they are the audit trail — but they stop being editable
    /// and stop being able to reach Complete.
    /// </summary>
    private async Task<string?> StaleReasonAsync(DispositionLedger ledger, CancellationToken cancellationToken)
    {
        MigrationRunRecord? origin =
            await runs.GetAsync(ledger.TenantId, ledger.RunId, cancellationToken).ConfigureAwait(false);

        if (origin is null)
        {
            return "The run this ledger was read from is no longer retained, so its source can no longer be confirmed.";
        }

        if (!string.Equals(origin.SourceSnapshotHash, ledger.SourceSnapshotHash, StringComparison.Ordinal))
        {
            return "The run this ledger was read from now records a different source snapshot.";
        }

        MigrationRunRecord? newest = (await runs
            .ForProjectAsync(ledger.TenantId, ledger.ProjectId, 50, cancellationToken).ConfigureAwait(false))
            .Where(run => run.State == MigrationRunState.Succeeded)
            .OrderByDescending(run => run.EnqueuedUtc)
            .FirstOrDefault();

        return newest is not null && !string.Equals(newest.SourceSnapshotHash, ledger.SourceSnapshotHash, StringComparison.Ordinal)
            ? "A newer completed run read a different source snapshot, so these decisions describe source that has been superseded."
            : null;
    }

    private async Task<DispositionLedgerSummary> SummaryAsync(
        DispositionLedger ledger,
        IReadOnlyList<DispositionLedgerEntry> entries,
        CancellationToken cancellationToken)
    {
        DispositionLedgerCounts counts = DispositionLedgerRules.Count(entries);
        DispositionCompletionState completion = DispositionLedgerRules.Completion(counts);
        string? stale = await StaleReasonAsync(ledger, cancellationToken).ConfigureAwait(false);

        if (stale is not null && completion.CanComplete)
        {
            completion = new DispositionCompletionState(false, [stale]);
        }

        return new DispositionLedgerSummary(
            ledger.LedgerId,
            ledger.ProjectId,
            ledger.RunId,
            ledger.SourceSnapshotHash,
            ledger.SourceRoot,
            ledger.CreatedUtc,
            ledger.CreatedByObjectId,
            ledger.ModuleCount,
            counts,
            completion,
            stale is not null,
            stale);
    }

    private async Task<DispositionLedgerView> ViewAsync(
        DispositionLedger ledger,
        IReadOnlyList<DispositionLedgerEntry> entries,
        CancellationToken cancellationToken)
    {
        DispositionScreenView[] screens =
        [
            .. entries
                .GroupBy(entry => entry.Identity.ModuleName, StringComparer.Ordinal)
                .OrderBy(group => group.Key, StringComparer.Ordinal)
                .Select(group => new DispositionScreenView(
                    group.Key,
                    group.First().Identity.FilePath,
                    DispositionLedgerRules.Count(group),
                    [
                        .. group
                            .GroupBy(entry => entry.Identity.BehaviorGroup, StringComparer.Ordinal)
                            .OrderBy(behavior => behavior.Key, StringComparer.Ordinal)
                            .Select(behavior => new DispositionBehaviorGroupView(
                                behavior.Key, DispositionLedgerRules.Count(behavior))),
                    ])),
        ];

        return new DispositionLedgerView(
            await SummaryAsync(ledger, entries, cancellationToken).ConfigureAwait(false),
            screens,
            DispositionMappingRules.All);
    }

    /// <summary>Human text for a count, used by the console copy and by the run report.</summary>
    public static string Describe(DispositionLedgerCounts counts)
    {
        ArgumentNullException.ThrowIfNull(counts);

        return string.Create(CultureInfo.InvariantCulture,
            $"{counts.Discovered} discovered, {counts.Decided} decided, {counts.Generated} generated, {counts.Verified} verified");
    }
}
