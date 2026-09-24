// Copyright (c) Microsoft. All rights reserved.

using System.Security.Cryptography;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OracleFormsMigrationFleet.Fleet;
using OracleFormsMigrationFleet.Fleet.Execution;
using OracleFormsMigrationFleet.Fleet.Execution.Adapters;

namespace OracleFormsMigrationFleet.Hosting;

public static class MigrationRunNode
{
    public static string Current =>
        Environment.GetEnvironmentVariable("CONTAINER_APP_REPLICA_NAME")
        ?? Environment.GetEnvironmentVariable("HOSTNAME")
        ?? Environment.MachineName;
}

public sealed class MigrationRunWorker(
    IMigrationRunStore store,
    SourceWorkspaceService workspaces,
    WorkbenchAuthorizationService authorization,
    IServiceProvider services,
    ILogger<MigrationRunWorker> logger) : BackgroundService
{
    private static readonly TimeSpan s_lease = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan s_renewal = TimeSpan.FromSeconds(10);
    private readonly string _workerId = $"{MigrationRunNode.Current}/{Guid.NewGuid():N}";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await store.ReconcileExpiredAsync(
                MigrationRunNode.Current,
                DateTimeOffset.UtcNow,
                "The prior worker lease expired. The run was not replayed because workspace locality or external effects could not be proven.",
                stoppingToken).ConfigureAwait(false);
            MigrationRunClaim? claim = await store.ClaimAsync(
                MigrationRunNode.Current,
                _workerId,
                DateTimeOffset.UtcNow,
                s_lease,
                stoppingToken).ConfigureAwait(false);
            if (claim is null)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken).ConfigureAwait(false);
                continue;
            }

            await ExecuteClaimAsync(claim, stoppingToken).ConfigureAwait(false);
        }
    }

    private async Task ExecuteClaimAsync(MigrationRunClaim claim, CancellationToken stoppingToken)
    {
        MigrationRunRecord run = claim.Run;
        long fence = claim.FenceToken;
        string? workspaceRoot = workspaces.ResolveRoot(run.WorkspaceOwnerId, run.WorkspaceId)
            ?? workspaces.ResolveDurableRoot(run.WorkspaceId);
        if (workspaceRoot is null)
        {
            await InterruptAsync(run, fence, "The source workspace is no longer available on its owning node.")
                .ConfigureAwait(false);
            return;
        }
        string? snapshot = workspaces.Describe(run.WorkspaceOwnerId, run.WorkspaceId, run.Request.SourceRoot)?.SnapshotHash
            ?? workspaces.DurableSnapshotHash(run.WorkspaceId, run.Request.SourceRoot);
        if (!MatchesSnapshot(snapshot, run.SourceSnapshotHash))
        {
            await InterruptAsync(
                run,
                fence,
                "The retained source bytes no longer match the snapshot authorized for this run.")
                .ConfigureAwait(false);
            return;
        }

        if (!await store.MarkRunningAsync(run.RunId, fence, DateTimeOffset.UtcNow, stoppingToken).ConfigureAwait(false))
        {
            MigrationRunRecord? current = await store.GetAsync(run.TenantId, run.RunId, stoppingToken)
                .ConfigureAwait(false);
            if (current?.FenceToken == fence && current.CancelRequestedUtc is not null)
            {
                const string cancelled = "Cancellation was requested before execution began. No phase was started.";
                await CompleteAsync(run, fence, MigrationRunState.Cancelled, null, cancelled, []).ConfigureAwait(false);
            }
            return;
        }

        // The stack was matched against the project's profile when the run was accepted, but a queued run
        // is executed later and the profile it is compared against is the one that stands now. Re-reading
        // it here is what stops a run that waited in the queue across a profile change from writing to a
        // destination identity nobody approved for it.
        if (await ProfileRefusalAsync(run, stoppingToken).ConfigureAwait(false) is { } incompatible)
        {
            await CompleteAsync(run, fence, MigrationRunState.Failed, null, incompatible, []).ConfigureAwait(false);
            return;
        }

        IDisposable retention = workspaces.Retain(run.WorkspaceId);

        await ExecuteStartedAsync(run, fence, workspaceRoot, retention, stoppingToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Why this run's requested target stack is not the one its project's immutable profile names now, or
    /// null while it still is.
    ///
    /// A deployment with no platform store cannot read the profile at all, so it cannot tell that the
    /// destination this run would write to is still the approved one. That is refused rather than waved
    /// through: an unreadable profile and a matching profile are not the same answer.
    /// </summary>
    private async Task<string?> ProfileRefusalAsync(MigrationRunRecord run, CancellationToken cancellationToken)
    {
        if (services.GetService<PlatformAccessService>() is not { } platform)
        {
            return "This host records no platform state, so the target profile this run was accepted against cannot be read and " +
                "the destination it would write to cannot be confirmed. Nothing was executed.";
        }

        if (string.IsNullOrWhiteSpace(run.TargetProfileId))
        {
            return "This run names no target profile, so there is no immutable record of the destination it would write to. " +
                "Nothing was executed.";
        }

        PlatformTargetProfile? profile = await platform.Store
            .GetTargetProfileAsync(run.TenantId, run.ProjectId, run.TargetProfileId, version: null, cancellationToken)
            .ConfigureAwait(false);

        if (profile is null)
        {
            return "The target profile this run was accepted against is no longer readable, so the destination it would write to " +
                "cannot be confirmed. Nothing was executed.";
        }

        return WorkbenchExecution.TryMatchTargetProfile(
            run.Request.Target, WorkbenchTargetProfileStack.From(profile), out string incompatible)
            ? null
            : $"{incompatible} This run was queued against an earlier profile and was not executed.";
    }

    private async Task ExecuteStartedAsync(
        MigrationRunRecord run,
        long fence,
        string workspaceRoot,
        IDisposable retention,
        CancellationToken stoppingToken)
    {
        Task<MigrationExecutionResult>? execution = null;
        Task? leaseMonitor = null;
        MigrationRunRequest request = run.Request;
        using CancellationTokenSource runCancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        using CancellationTokenSource monitorCancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        try
        {

        WorkbenchActor actor = WorkbenchActor.ForTenant(run.TenantId, run.ActorObjectId, []);
        request = await MaterializeAuthorityAsync(run, actor, stoppingToken).ConfigureAwait(false);
        WorkbenchMutationAuthorizer authorizer = new(
            authorization,
            actor,
            run.SourceSnapshotHash,
            run.PlanInputHash,
            run.TargetProfileHash,
            projectId: run.ProjectId,
            targetProfileId: run.TargetProfileId,
            targetProfileVersion: run.TargetProfileVersion);

        IDataMigrationGateway? gateway = services.GetService<IDataMigrationGateway>();
        if (gateway is not null)
        {
            gateway = RunScopedGateway.Wrap(gateway, authorizer, request, store, run.RunId, fence, s_lease);
        }

        // Re-asked immediately before the approved target is opened, not once when the run was queued. A
        // durable run can sit in the queue and can spend minutes between the migration phase and the read,
        // so a grant revoked or expired in that window stops the connection. The fence is asked after the
        // grant, not before it: the grant check is an awaited round trip, and a worker whose claim a newer
        // one replaced during that round trip is not the worker that may open this target.
        IEntryRuntimeVerificationGateway? entryVerification = services.GetService<IEntryRuntimeVerificationGateway>();
        if (entryVerification is not null)
        {
            entryVerification = RunScopedGateway.Wrap(
                entryVerification, authorizer, request, store, run.RunId, fence, s_lease);
        }

        // The provider, not an authorization: the generation phase asks it at the moment it emits, and it
        // answers from the ledger as it stands then. Every identity in it is fixed here — this run's
        // actor, this run's ledger, this run's identifier — so no adapter and no request body can widen
        // it. A run that names a ledger this process cannot resolve does not generate at all.
        DispositionLedgerService? ledgers = services.GetService<DispositionLedgerService>();
        string? ledgerId = request.DispositionLedgerId;
        LedgerGenerationAuthorizationProvider? generation = null;
        LedgerEntryVerificationProvider? verification = null;

        if (ledgerId is { Length: > 0 })
        {
            if (ledgers is null)
            {
                await CompleteAsync(
                    run,
                    fence,
                    MigrationRunState.Failed,
                    null,
                    "This run names a disposition ledger and this host records none, so the decisions it would generate under " +
                    "cannot be read. No phase was started.",
                    []).ConfigureAwait(false);
                return;
            }

            generation = new LedgerGenerationAuthorizationProvider(ledgers, actor, ledgerId, run.RunId);
            verification = new LedgerEntryVerificationProvider(
                ledgers, run.TenantId, ledgerId, run.RunId, new MigrationRunOwnership(fence, s_lease));
        }

        // Recorded when the phase that wrote it finishes, not when the run does. A later phase verifies
        // against the generation, so the generation has to be durable while the run is still in flight and
        // still holds the claim it was produced under — and a generation the ledger will not accept has to
        // stop the run there rather than after a verification phase has already read a customer database.
        bool generationRecorded = false;

        // The same ordering one step later. A deployment claims to publish a verified tier, so the
        // per-entry results have to be entered against the decisions they are about before the deployment
        // phase runs. Recording them only at the terminal state meant a record the ledger refuses — no
        // executed case, a decision moved since, output replaced — was discovered after an image was
        // already serving.
        bool verificationRecorded = false;

        // What the phases have written so far, one entry per path. The observer is handed only the
        // artifacts of the phase that just finished, and recording a verification needs this run's
        // generation coverage as well as its verification record, so the earlier phases' output is
        // carried forward here rather than re-enumerated off disk.
        Dictionary<string, ArtifactReference> produced = new(StringComparer.Ordinal);

        // Where this run may publish its generated application, resolved from the project's own profile
        // and the approval effective for it. Constructed here so the run's identity is fixed by the
        // server; a host with no platform store and no configured target app resolves nothing, and the
        // deployment phase then refuses instead of publishing under host configuration alone.
        ITargetApplicationDeploymentGateway? deploymentGateway = services.GetService<ITargetApplicationDeploymentGateway>();
        if (deploymentGateway is not null)
        {
            deploymentGateway = RunScopedGateway.Wrap(
                deploymentGateway, authorizer, request, store, run.RunId, fence, s_lease);
        }

        ITargetDeploymentAuthorityProvider? deploymentAuthority =
            services.GetService<PlatformAccessService>() is { } access &&
            services.GetService<GeneratedApplicationTargetName>() is { Name.Length: > 0 } configuredTarget
                ? new PlatformTargetDeploymentAuthorityProvider(access.Store, run, configuredTarget.Name)
                : null;

        MigrationExecutor executor = new(
            workspaceRoot,
            MigrationExecutor.DefaultAdapters(
                services.GetService<IArtifactReviewer>(),
                gateway,
                services.GetService<Fleet.Agents.CritiqueRepairOrchestrator>(),
                services.GetService<ProgramUnitRepairLoop>(),
                services.GetService<IApplicationBuildGateway>(),
                services.GetService<IApplicationTestGateway>(),
                services.GetService<ITargetApplicationVerificationGateway>(),
                entryVerification,
                deploymentGateway),
            authorizer,
            generation,
            verification,
            ledgers is null || ledgerId is not { Length: > 0 }
                ? null
                : async (outcome, _) =>
                {
                    foreach (ArtifactReference artifact in outcome.Artifacts)
                    {
                        produced[WorkspacePath.Normalize(artifact.Path)] = artifact;
                    }

                    switch (outcome.Phase)
                    {
                        case MigrationPhase.ApplicationCodeConversion:
                        {
                            string? unrecorded = await RecordGenerationAsync(
                                run, fence, ledgers, ledgerId, Manifest(run.RunId, workspaceRoot, [.. produced.Values]))
                                .ConfigureAwait(false);

                            generationRecorded = unrecorded is null;
                            return unrecorded;
                        }

                        case MigrationPhase.TargetContractVerification:
                        {
                            string? unrecorded = await RecordVerificationAsync(
                                run, fence, ledgers, ledgerId, Manifest(run.RunId, workspaceRoot, [.. produced.Values]))
                                .ConfigureAwait(false);

                            verificationRecorded = unrecorded is null;
                            return unrecorded;
                        }

                        default:
                            return null;
                    }
                },
            deploymentAuthority);

        WorkbenchExecution.ResetOutput(workspaceRoot, request.OutputRoot);
        Channel<ExecutionProgress> progress = Channel.CreateUnbounded<ExecutionProgress>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
        execution = Task.Run(async () =>
        {
            try
            {
                return await executor.ExecuteAsync(
                    request,
                    actor.OwnerId,
                    item => progress.Writer.TryWrite(item),
                    runCancellation.Token).ConfigureAwait(false);
            }
            finally
            {
                progress.Writer.TryComplete();
            }
        }, CancellationToken.None);
        leaseMonitor = MonitorLeaseAsync(run, fence, runCancellation, monitorCancellation.Token);

            while (!execution.IsCompleted)
            {
                if (leaseMonitor.IsCompleted)
                {
                    await leaseMonitor.ConfigureAwait(false);
                }
                Task<bool> available = progress.Reader.WaitToReadAsync(runCancellation.Token).AsTask();
                Task heartbeat = Task.Delay(s_renewal, runCancellation.Token);
                await Task.WhenAny(available, heartbeat, leaseMonitor).ConfigureAwait(false);
                if (leaseMonitor.IsCompleted)
                {
                    await leaseMonitor.ConfigureAwait(false);
                }
                await DrainAsync(progress.Reader, run.RunId, fence, runCancellation).ConfigureAwait(false);
            }

            await DrainAsync(progress.Reader, run.RunId, fence, runCancellation).ConfigureAwait(false);
            MigrationExecutionResult result = await execution.ConfigureAwait(false);
            WorkbenchExecutionView outcome = WorkbenchExecution.Project(result);
            MigrationRunRecord? current = await store.GetAsync(run.TenantId, run.RunId, CancellationToken.None)
                .ConfigureAwait(false);
            if (current?.CancelRequestedUtc is not null)
            {
                monitorCancellation.Cancel();
                await StopMonitorAsync(leaseMonitor).ConfigureAwait(false);
                const string cancelled = "Cancellation was observed after the current safe operation. Earlier effects and artifacts were retained.";
                await CompleteAsync(
                    run,
                    fence,
                    MigrationRunState.Cancelled,
                    outcome,
                    cancelled,
                    Manifest(run.RunId, workspaceRoot, result.Artifacts)).ConfigureAwait(false);
                return;
            }
            bool failed = WorkbenchRunOutcome.Failed(result.Phases);
            IReadOnlyList<MigrationRunArtifact> artifacts = Manifest(run.RunId, workspaceRoot, result.Artifacts);

            // Recorded from the manifest this worker just hashed off disk, and recorded before the run is
            // called anything. A generation whose evidence will not record is not a successful run: the
            // files exist and nothing in the ledger says which decisions they were produced under, which
            // is the state this whole path exists to prevent. The conversion phase normally records it as
            // soon as it writes it; this covers a run that produced coverage without that phase reporting
            // it, and is a no-op when the generation is already in the ledger.
            //
            // The lease monitor is still running here on purpose. Recording is a durable write made in
            // this run's name, so the claim that authorizes it has to still be renewable while it happens
            // rather than having been abandoned one statement earlier.
            string? evidenceFailure = ledgers is null || ledgerId is not { Length: > 0 } || generationRecorded
                ? null
                : await RecordGenerationAsync(run, fence, ledgers, ledgerId, artifacts).ConfigureAwait(false);

            // Strictly after the generation it speaks about, in the same run, under the same claim. A
            // result is a statement about an artifact, so the artifact has to be recorded first or there
            // is nothing for it to attach to; and if the generation would not record, entering results
            // about it would be recording proof of something the ledger holds no generation for.
            //
            // The verification phase normally records this as soon as it writes it, before the deployment
            // phase runs. This covers a run that produced a record without that phase reporting it, and is
            // skipped when the phase boundary already entered it.
            if (evidenceFailure is null && !verificationRecorded && ledgers is not null && ledgerId is { Length: > 0 })
            {
                evidenceFailure = await RecordVerificationAsync(run, fence, ledgers, ledgerId, artifacts).ConfigureAwait(false);
            }

            monitorCancellation.Cancel();
            await StopMonitorAsync(leaseMonitor).ConfigureAwait(false);

            MigrationRunState terminal = failed || evidenceFailure is not null
                ? MigrationRunState.Failed
                : MigrationRunState.Succeeded;
            await CompleteAsync(
                run,
                fence,
                terminal,
                outcome,
                evidenceFailure,
                artifacts).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            runCancellation.Cancel();
            if (execution is not null && !await WaitForQuiescenceAsync(execution).ConfigureAwait(false))
            {
                throw new FatalRunOwnershipException(
                    $"Run {run.RunId} did not stop after host shutdown cancellation.");
            }
            await InterruptAsync(run, fence, "The host stopped while this run was active. Review retained events before retrying.")
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (leaseMonitor?.IsFaulted == true)
            {
                runCancellation.Cancel();
                if (execution is not null)
                {
                    await RequireQuiescenceAsync(execution, run.RunId, "lease monitor failure").ConfigureAwait(false);
                }
                logger.LogWarning(
                    leaseMonitor.Exception,
                    "Run {RunId} lost its lease monitor and was left nonterminal for reconciliation.",
                    run.RunId);
                return;
            }
            await CompleteAsync(
                run,
                fence,
                MigrationRunState.Cancelled,
                null,
                "Cancellation was observed at a safe point. Earlier external writes were not undone.",
                ManifestDirectory(run.RunId, workspaceRoot, request.OutputRoot)).ConfigureAwait(false);
        }
        catch (StaleRunFenceException)
        {
            runCancellation.Cancel();
            if (execution is not null)
            {
                await RequireQuiescenceAsync(execution, run.RunId, "stale fence").ConfigureAwait(false);
            }
            logger.LogWarning("Run {RunId} worker was fenced by a newer claim.", run.RunId);
        }
        catch (LeaseMonitorException exception)
        {
            runCancellation.Cancel();
            if (execution is not null)
            {
                await RequireQuiescenceAsync(execution, run.RunId, "lease monitor failure").ConfigureAwait(false);
            }
            logger.LogWarning(
                exception,
                "Run {RunId} lost its lease monitor and was left nonterminal for reconciliation.",
                run.RunId);
        }
        catch (Exception exception)
        {
            runCancellation.Cancel();
            if (execution is not null)
            {
                await RequireQuiescenceAsync(execution, run.RunId, "execution or persistence failure").ConfigureAwait(false);
            }
            logger.LogError(exception, "Durable run {RunId} failed.", run.RunId);
            const string reason = "The run stopped before it finished. Review retained events and artifacts before retrying.";
            await CompleteAsync(
                run,
                fence,
                MigrationRunState.Failed,
                null,
                reason,
                ManifestDirectory(run.RunId, workspaceRoot, request.OutputRoot)).ConfigureAwait(false);
        }
        finally
        {
            monitorCancellation.Cancel();
            if (leaseMonitor is not null)
            {
                await IgnoreCancellationAsync(leaseMonitor).ConfigureAwait(false);
            }
            if (execution is null || execution.IsCompleted)
            {
                retention.Dispose();
            }
            else
            {
                _ = execution.ContinueWith(
                    _ => retention.Dispose(),
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
        }
    }

    /// <summary>
    /// Records what the generation phase produced against the ledger it generated under, and returns the
    /// reason nothing was recorded when there was something to record and it failed.
    ///
    /// A run with no coverage record in its manifest generated nothing under a ledger — the phase was
    /// skipped, or it refused — and that is not a recording failure. A coverage record that is present and
    /// will not record is: the tier is on disk and no decision in the ledger is bound to it.
    ///
    /// Only the generation phase's own evidence is recorded. A later phase failing does not withdraw it,
    /// and recording it does not make the run a success — the caller decides the terminal state, and
    /// <see cref="DispositionVerificationStatus"/> stays NotExecuted here because generating is not
    /// executing.
    /// </summary>
    private async Task<string?> RecordGenerationAsync(
        MigrationRunRecord run,
        long fence,
        DispositionLedgerService ledgers,
        string ledgerId,
        IReadOnlyList<MigrationRunArtifact> artifacts)
    {
        string coveragePath =
            $"{WorkspacePath.Normalize(run.Request.OutputRoot)}/{GenerationCoverage.RecordPath}";

        if (!artifacts.Any(artifact =>
                string.Equals(WorkspacePath.Normalize(artifact.Path), coveragePath, StringComparison.Ordinal)))
        {
            return null;
        }

        PlatformResult<DispositionLedgerGeneration> recorded;
        try
        {
            recorded = await ledgers.RecordGenerationFromRunAsync(
                run.TenantId,
                ledgerId,
                run.RunId,
                artifacts,
                new MigrationRunOwnership(fence, s_lease),
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "Recording generation evidence for run {RunId} failed.", run.RunId);
            return "The application tier was generated, but the evidence of which decisions it was generated under could not be " +
                $"recorded against ledger {ledgerId} ({FailureText.Describe(exception)}). The run is reported failed because the " +
                "output exists and nothing records what authorized it.";
        }

        string text = recorded.Succeeded
            ? $"Recorded this run's generation against ledger {ledgerId}: " +
              $"{recorded.Value!.FilesHashed} emitted file(s) re-hashed by the server, {recorded.Value.EntriesRecorded} decision(s) " +
              "now carrying a generated reference. Nothing here has been compiled, started, or executed."
            : $"The generation this run produced was not recorded against ledger {ledgerId}: {recorded.Error}";

        await store.AppendEventAsync(
            run.RunId,
            fence,
            DateTimeOffset.UtcNow,
            recorded.Succeeded ? "info" : "error",
            text,
            null,
            null,
            CancellationToken.None).ConfigureAwait(false);

        return recorded.Succeeded ? null : text;
    }

    /// <summary>
    /// Records what this run's trusted verifier executed against each recorded decision, and returns the
    /// reason nothing was recorded when there was something to record and it failed.
    ///
    /// A run whose manifest holds no verification record executed no attributable case — no ledger-bound
    /// generation, no configured verifier, or a verifier that refused — and that is not a recording
    /// failure. The phase result already says whether verification passed; this path only decides whether
    /// what ran was durably attributed to the decisions it was about.
    ///
    /// Failures and gaps are recorded exactly as they happened. A failed case makes its property read back
    /// failed and keeps the ledger incomplete, which is the outcome, not an error in recording it.
    /// </summary>
    private async Task<string?> RecordVerificationAsync(
        MigrationRunRecord run,
        long fence,
        DispositionLedgerService ledgers,
        string ledgerId,
        IReadOnlyList<MigrationRunArtifact> artifacts)
    {
        string recordPath =
            $"{WorkspacePath.Normalize(run.Request.OutputRoot)}/{EntryVerificationCoverage.RecordPath}";

        if (!artifacts.Any(artifact =>
                string.Equals(WorkspacePath.Normalize(artifact.Path), recordPath, StringComparison.Ordinal)))
        {
            return null;
        }

        PlatformResult<DispositionLedgerVerification> recorded;
        try
        {
            recorded = await ledgers.RecordVerificationFromRunAsync(
                run.TenantId,
                ledgerId,
                run.RunId,
                artifacts,
                new MigrationRunOwnership(fence, s_lease),
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "Recording verification evidence for run {RunId} failed.", run.RunId);
            return "Cases were executed against a configured verification target, but which recorded decision each one was about " +
                $"could not be entered against ledger {ledgerId} ({FailureText.Describe(exception)}). The run is reported failed " +
                "because results exist and nothing records what they proved.";
        }

        string text = recorded.Succeeded
            ? $"Recorded this run's executed verification against ledger {ledgerId}: " +
              $"{recorded.Value!.CasesRecorded} case(s) over {recorded.Value.EntriesRecorded} recorded decision(s) — " +
              $"{recorded.Value.CasesPassed} passed, {recorded.Value.CasesFailed} failed — against generated content " +
              $"{recorded.Value.TestedOutputSetSha256}, with {recorded.Value.GapsDeclared} stated gap(s). No generated service " +
              "was started, so none of this is evidence of application behaviour."
            : $"The verification this run executed was not recorded against ledger {ledgerId}: {recorded.Error}";

        await store.AppendEventAsync(
            run.RunId,
            fence,
            DateTimeOffset.UtcNow,
            recorded.Succeeded ? "info" : "error",
            text,
            null,
            null,
            CancellationToken.None).ConfigureAwait(false);

        return recorded.Succeeded ? null : text;
    }

    private async Task MonitorLeaseAsync(        MigrationRunRecord run,
        long fence,
        CancellationTokenSource runCancellation,
        CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(s_renewal, cancellationToken).ConfigureAwait(false);
                if (!await store.RenewAsync(run.RunId, fence, s_lease, cancellationToken).ConfigureAwait(false))
                {
                    runCancellation.Cancel();
                    throw new StaleRunFenceException();
                }
                MigrationRunRecord? current = await store.GetAsync(run.TenantId, run.RunId, cancellationToken)
                    .ConfigureAwait(false);
                if (current?.CancelRequestedUtc is not null)
                {
                    runCancellation.Cancel();
                }
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException and not StaleRunFenceException)
        {
            runCancellation.Cancel();
            throw new LeaseMonitorException(exception);
        }
    }

    private static async Task<bool> WaitForQuiescenceAsync(Task execution)
    {
        try
        {
            await execution.WaitAsync(TimeSpan.FromSeconds(20)).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
        catch
        {
            return true;
        }
    }

    private static async Task RequireQuiescenceAsync(Task execution, string runId, string cause)
    {
        if (!await WaitForQuiescenceAsync(execution).ConfigureAwait(false))
        {
            throw new FatalRunOwnershipException(
                $"Run {runId} did not stop after {cause}; the worker host must stop before claiming more work.");
        }
    }

    private static async Task IgnoreCancellationAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (StaleRunFenceException)
        {
        }
        catch (LeaseMonitorException)
        {
        }
    }

    private static async Task StopMonitorAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task<MigrationRunRequest> MaterializeAuthorityAsync(
        MigrationRunRecord run,
        WorkbenchActor actor,
        CancellationToken cancellationToken)
    {
        MigrationRunRequest request = run.Request with
        {
            ExecutionApproval = HumanApproval.Pending,
            ProductionApproval = HumanApproval.Pending,
            Attestations = [],
        };
        WorkbenchAuthorizationDecision decision = await authorization.AuthorizeAsync(
            new WorkbenchAuthorizationQuery(
                actor,
                request.EngagementId,
                run.SourceSnapshotHash,
                run.PlanInputHash,
                run.TargetProfileHash,
                WorkbenchMutationScope.SandboxDatabaseWrite,
                run.TenantId,
                run.ProjectId,
                run.TargetProfileId,
                run.TargetProfileVersion),
            DateTimeOffset.UtcNow,
            cancellationToken).ConfigureAwait(false);
        if (!decision.IsAuthorized)
        {
            return request;
        }

        return request with
        {
            ExecutionApproval = new HumanApproval
            {
                Decision = ApprovalDecision.Approved,
                ApproverId = decision.ApprovedByObjectId.Length == 0
                    ? decision.AuthorizationId
                    : decision.ApprovedByObjectId,
                Notes = $"Materialized from persisted authorization {decision.AuthorizationId}.",
            },
        };
    }

    private async Task DrainAsync(
        ChannelReader<ExecutionProgress> reader,
        string runId,
        long fence,
        CancellationTokenSource runCancellation)
    {
        while (reader.TryRead(out ExecutionProgress? item))
        {
            MigrationRunEvent? appended = await store.AppendEventAsync(
                runId,
                fence,
                DateTimeOffset.UtcNow,
                item.Level,
                item.Text,
                item.Signal,
                null,
                CancellationToken.None).ConfigureAwait(false);
            if (appended is null)
            {
                runCancellation.Cancel();
                throw new StaleRunFenceException();
            }
        }
    }

    private async Task<bool> CompleteAsync(
        MigrationRunRecord run,
        long fence,
        MigrationRunState state,
        WorkbenchExecutionView? outcome,
        string? reason,
        IReadOnlyList<MigrationRunArtifact> artifacts)
    {
        bool failed = state != MigrationRunState.Succeeded;
        bool completed = await store.CompleteAsync(
            run.RunId,
            fence,
            state,
            DateTimeOffset.UtcNow,
            outcome,
            reason,
            artifacts,
            failed ? "error" : "done",
            new ProgressSignal(
                ProgressOperations.MigrationRun,
                failed ? ProgressActions.RunFailed : ProgressActions.RunCompleted,
                failed ? ProgressState.Failed : ProgressState.Completed,
                "Running the phases the planner authorized and retaining their outcome.",
                reason ?? "The run reached a durable terminal outcome.",
                failed ? "Review retained events before retrying." : "Review the retained output and build evidence."),
            CancellationToken.None).ConfigureAwait(false);
        if (completed || state == MigrationRunState.Cancelled)
        {
            return completed;
        }

        MigrationRunRecord? current = await store.GetAsync(run.TenantId, run.RunId, CancellationToken.None)
            .ConfigureAwait(false);
        if (current?.FenceToken != fence || current.CancelRequestedUtc is null)
        {
            return false;
        }

        const string cancelled = "Cancellation won the terminal race. Earlier effects and artifacts were retained.";
        return await store.CompleteAsync(
            run.RunId,
            fence,
            MigrationRunState.Cancelled,
            DateTimeOffset.UtcNow,
            outcome,
            cancelled,
            artifacts,
            "error",
            new ProgressSignal(
                ProgressOperations.MigrationRun,
                ProgressActions.RunFailed,
                ProgressState.Failed,
                "Cancelling a durable migration run.",
                cancelled,
                "Review retained events and destination state before retrying."),
            CancellationToken.None).ConfigureAwait(false);
    }

    private async Task InterruptAsync(MigrationRunRecord run, long fence, string reason)
    {
        string? workspaceRoot = workspaces.ResolveRoot(run.WorkspaceOwnerId, run.WorkspaceId)
            ?? workspaces.ResolveDurableRoot(run.WorkspaceId);
        IReadOnlyList<MigrationRunArtifact> artifacts = workspaceRoot is null
            ? []
            : ManifestDirectory(run.RunId, workspaceRoot, run.Request.OutputRoot);
        await CompleteAsync(run, fence, MigrationRunState.Interrupted, null, reason, artifacts).ConfigureAwait(false);
    }

    private static IReadOnlyList<MigrationRunArtifact> Manifest(
        string runId,
        string workspaceRoot,
        IReadOnlyList<ArtifactReference> artifacts)
    {
        List<MigrationRunArtifact> manifest = [];
        foreach (ArtifactReference artifact in artifacts)
        {
            string relative = WorkspacePath.Normalize(artifact.Path);
            string absolute = Path.GetFullPath(Path.Combine(workspaceRoot, relative));
            if (!absolute.StartsWith(Path.GetFullPath(workspaceRoot) + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
                !File.Exists(absolute))
            {
                continue;
            }

            FileInfo file = new(absolute);
            using FileStream stream = file.OpenRead();
            string hash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
            manifest.Add(new MigrationRunArtifact(
                runId, relative, artifact.Kind.ToString(), artifact.Description, file.Length, hash));
        }
        return manifest;
    }

    private static IReadOnlyList<MigrationRunArtifact> ManifestDirectory(
        string runId,
        string workspaceRoot,
        string outputRoot)
    {
        string normalized = WorkspacePath.Normalize(outputRoot);
        string directory = Path.GetFullPath(Path.Combine(workspaceRoot, normalized));
        string workspacePrefix = Path.GetFullPath(workspaceRoot) + Path.DirectorySeparatorChar;
        if (!directory.StartsWith(workspacePrefix, StringComparison.Ordinal) || !Directory.Exists(directory))
        {
            return [];
        }

        List<MigrationRunArtifact> manifest = [];
        foreach (string filePath in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            FileInfo file = new(filePath);
            using FileStream stream = file.OpenRead();
            string hash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
            string relative = Path.GetRelativePath(workspaceRoot, filePath).Replace('\\', '/');
            manifest.Add(new MigrationRunArtifact(
                runId, relative, "RetainedFile", "File retained from an incomplete run.", file.Length, hash));
        }
        return manifest;
    }

    private static bool MatchesSnapshot(string? actual, string expected)
    {
        try
        {
            byte[] actualBytes = Convert.FromHexString(actual ?? string.Empty);
            byte[] expectedBytes = Convert.FromHexString(expected);
            return actualBytes.Length == expectedBytes.Length &&
                CryptographicOperations.FixedTimeEquals(actualBytes, expectedBytes);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private sealed class LeaseMonitorException(Exception inner) : Exception("The run lease monitor failed.", inner);

    private sealed class FatalRunOwnershipException(string message) : Exception(message);
}

/// <summary>
/// The one place the run-scoped gateway wrappers are composed, and the reason for their order.
///
/// Neither wrapper implies the order on its own, and the order is the property that matters. The grant
/// check is an awaited round trip to the authorization store, so a claim taken over while that check is
/// in flight is only observable after it returns. The fence therefore sits innermost — the last thing
/// asked before the call leaves this process — and the grant check outside it. Reversed, a worker that
/// renewed, then spent the authorization round trip losing its claim, would still reach the gateway.
///
/// This narrows the window; it does not close it. Nothing here makes a renewal and a dispatch one atomic
/// operation, so a takeover in the instant between them is still possible. What it does establish is that
/// the fence is checked after every other check this process performs, rather than before them.
/// </summary>
internal static class RunScopedGateway
{
    public static IDataMigrationGateway Wrap(
        IDataMigrationGateway inner,
        WorkbenchMutationAuthorizer authorizer,
        MigrationRunRequest request,
        IMigrationRunStore store,
        string runId,
        long fenceToken,
        TimeSpan lease) =>
        new AuthorizingDataMigrationGateway(
            new FencedDataMigrationGateway(inner, store, runId, fenceToken, lease), authorizer, request);

    public static IEntryRuntimeVerificationGateway Wrap(
        IEntryRuntimeVerificationGateway inner,
        WorkbenchMutationAuthorizer authorizer,
        MigrationRunRequest request,
        IMigrationRunStore store,
        string runId,
        long fenceToken,
        TimeSpan lease) =>
        new AuthorizingEntryRuntimeVerificationGateway(
            new FencedEntryRuntimeVerificationGateway(inner, store, runId, fenceToken, lease), authorizer, request);

    public static ITargetApplicationDeploymentGateway Wrap(
        ITargetApplicationDeploymentGateway inner,
        WorkbenchMutationAuthorizer authorizer,
        MigrationRunRequest request,
        IMigrationRunStore store,
        string runId,
        long fenceToken,
        TimeSpan lease)
    {
        // A publish is not one instant. The wrappers below answer for the instant before the gateway is
        // entered; the sentinel answers again inside it, after the uploads and the token acquisition that
        // sit between that instant and the dispatch that actually mints an image. A builder that supports
        // being bound gets the same two checks, in the same order, at the points that matter.
        ITargetApplicationDeploymentGateway bound = inner is IRevalidatingTargetApplicationDeploymentGateway revalidating
            ? revalidating.BoundTo(new RunScopedDispatchSentinel(authorizer, request, store, runId, fenceToken, lease))
            : inner;

        return new AuthorizingTargetApplicationDeploymentGateway(
            new FencedTargetApplicationDeploymentGateway(bound, store, runId, fenceToken, lease), authorizer, request);
    }
}

/// <summary>
/// The server's live answer, asked from inside a publish that is still running.
///
/// It asks the same two questions the run-scoped wrappers ask, in the same order and for the same reasons:
/// whether the operator's grant still authorizes this work, and then — because that answer is an awaited
/// round trip during which a claim can be taken over — whether this process still owns the run. The fence
/// is innermost so it is the last thing established before the caller acts on the answer.
///
/// Nothing here is supplied by the request being published. The authorizer, the run identifier and the
/// fence token are fixed when the run is claimed, so a bundle, a binding, or an adapter cannot widen what
/// is being asked about.
/// </summary>
internal sealed class RunScopedDispatchSentinel(
    WorkbenchMutationAuthorizer authorizer,
    MigrationRunRequest request,
    IMigrationRunStore store,
    string runId,
    long fenceToken,
    TimeSpan lease) : ITargetDeploymentDispatchSentinel
{
    public async Task<TargetDeploymentClearance> RevalidateAsync(
        TargetDeploymentCheckpoint checkpoint,
        string operationId,
        CancellationToken cancellationToken)
    {
        MutationAuthorizationResult decision = await authorizer
            .RecheckAsync(request, WorkbenchMutationScope.SandboxDatabaseWrite, cancellationToken)
            .ConfigureAwait(false);

        if (!decision.IsAuthorized)
        {
            return TargetDeploymentClearance.Withheld(
                $"The operator's authorization for this run no longer holds at the {checkpoint} checkpoint of operation " +
                $"{operationId}: {decision.Reason}");
        }

        if (!await store.RenewAsync(runId, fenceToken, lease, cancellationToken).ConfigureAwait(false))
        {
            return TargetDeploymentClearance.Withheld(
                $"A newer claim owns this run, so at the {checkpoint} checkpoint of operation {operationId} this worker is " +
                "no longer the one executing it.");
        }

        return TargetDeploymentClearance.Cleared(decision.Reason);
    }
}

internal sealed class FencedDataMigrationGateway(
    IDataMigrationGateway inner,
    IMigrationRunStore store,
    string runId,
    long fenceToken,
    TimeSpan lease) : IDataMigrationGateway
{
    public async Task<SchemaDeploymentOutcome> PrepareAsync(
        IReadOnlyList<string> statements, CancellationToken cancellationToken)
    {
        await GuardAsync(cancellationToken).ConfigureAwait(false);
        return await inner.PrepareAsync(statements, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<TableRowCount>> CountAsync(
        IReadOnlyList<string> tables, CancellationToken cancellationToken)
    {
        await GuardAsync(cancellationToken).ConfigureAwait(false);
        return await inner.CountAsync(tables, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<IReadOnlyList<string?>>> FetchAsync(
        string table, IReadOnlyList<string> columns, int maxRows, CancellationToken cancellationToken)
    {
        await GuardAsync(cancellationToken).ConfigureAwait(false);
        return await inner.FetchAsync(table, columns, maxRows, cancellationToken).ConfigureAwait(false);
    }

    public async Task<DataMigrationOutcome> ApplyAsync(
        IReadOnlyList<DataMigrationStatement> statements,
        IReadOnlyList<string> tables,
        CancellationToken cancellationToken)
    {
        await GuardAsync(cancellationToken).ConfigureAwait(false);
        return await inner.ApplyAsync(statements, tables, cancellationToken).ConfigureAwait(false);
    }

    private async Task GuardAsync(CancellationToken cancellationToken)
    {
        if (!await store.RenewAsync(
            runId,
            fenceToken,
            lease,
            cancellationToken).ConfigureAwait(false))
        {
            throw new UnauthorizedAccessException("This worker no longer owns the durable run lease.");
        }
    }
}

/// <summary>
/// A claim this worker no longer holds, raised at the boundary rather than after the call.
///
/// It derives from <see cref="UnauthorizedAccessException"/> so an adapter that already fails closed on a
/// denied gateway keeps doing so, while the worker can still tell a fencing apart from any other refusal.
/// </summary>
internal sealed class StaleRunFenceException(string? message = null)
    : UnauthorizedAccessException(message ?? "This worker no longer owns the durable run lease.");

/// <summary>
/// Renews the claim against the store immediately before the approved target is read.
///
/// The grant check above it answers whether the operator still approves this work. This answers a
/// different question: whether this process is still the one executing this run. A worker whose lease was
/// taken over by a newer claim renews nothing, so the read never reaches the gateway and no observation
/// is produced that a stale worker could enter against the ledger.
/// </summary>
internal sealed class FencedEntryRuntimeVerificationGateway(
    IEntryRuntimeVerificationGateway inner,
    IMigrationRunStore store,
    string runId,
    long fenceToken,
    TimeSpan lease) : IEntryRuntimeVerificationGateway
{
    public EntryVerificationTargetBinding Target => inner.Target;

    public async Task<EntryRuntimeVerificationRun> InspectAsync(
        EntryVerificationTargetBinding approved,
        IReadOnlyList<EntryVerificationExpectation> expectations,
        CancellationToken cancellationToken)
    {
        if (!await store.RenewAsync(runId, fenceToken, lease, cancellationToken).ConfigureAwait(false))
        {
            throw new StaleRunFenceException(
                "A newer claim owns this run, so the approved target was not read and nothing was observed under this run's name.");
        }

        return await inner.InspectAsync(approved, expectations, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>
/// Renews the claim against the store immediately before the bundle is handed to the trusted builder.
///
/// The builder both builds an image and updates a revision, and neither is undone by this process losing
/// the run afterwards. Renewing here is therefore the last point at which a stale worker can be stopped
/// from minting a build and a digest under a run another worker now owns.
/// </summary>
internal sealed class FencedTargetApplicationDeploymentGateway(
    ITargetApplicationDeploymentGateway inner,
    IMigrationRunStore store,
    string runId,
    long fenceToken,
    TimeSpan lease) : ITargetApplicationDeploymentGateway
{
    public string Description => inner.Description;

    public async Task<TargetDeploymentResult> PublishAsync(
        TargetDeploymentRequest request,
        CancellationToken cancellationToken)
    {
        if (!await store.RenewAsync(runId, fenceToken, lease, cancellationToken).ConfigureAwait(false))
        {
            throw new StaleRunFenceException(
                "A newer claim owns this run, so nothing was dispatched to the builder and no image was produced under this run's name.");
        }

        return await inner.PublishAsync(request, cancellationToken).ConfigureAwait(false);
    }
}
