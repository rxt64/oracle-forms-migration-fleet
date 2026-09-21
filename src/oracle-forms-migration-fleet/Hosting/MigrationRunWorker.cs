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
        IDisposable retention = workspaces.Retain(run.WorkspaceId);

        await ExecuteStartedAsync(run, fence, workspaceRoot, retention, stoppingToken).ConfigureAwait(false);
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
            gateway = new AuthorizingDataMigrationGateway(gateway, authorizer, request);
            gateway = new FencedDataMigrationGateway(gateway, store, run.RunId, fence, s_lease);
        }

        MigrationExecutor executor = new(
            workspaceRoot,
            MigrationExecutor.DefaultAdapters(
                services.GetService<IArtifactReviewer>(),
                gateway,
                services.GetService<Fleet.Agents.CritiqueRepairOrchestrator>(),
                services.GetService<ProgramUnitRepairLoop>(),
                services.GetService<IApplicationBuildGateway>(),
                services.GetService<IApplicationTestGateway>(),
                services.GetService<ITargetApplicationVerificationGateway>()),
            authorizer);

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
            monitorCancellation.Cancel();
            await StopMonitorAsync(leaseMonitor).ConfigureAwait(false);
            WorkbenchExecutionView outcome = WorkbenchExecution.Project(result);
            MigrationRunRecord? current = await store.GetAsync(run.TenantId, run.RunId, CancellationToken.None)
                .ConfigureAwait(false);
            if (current?.CancelRequestedUtc is not null)
            {
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
            MigrationRunState terminal = failed ? MigrationRunState.Failed : MigrationRunState.Succeeded;
            await CompleteAsync(
                run,
                fence,
                terminal,
                outcome,
                null,
                Manifest(run.RunId, workspaceRoot, result.Artifacts)).ConfigureAwait(false);
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

    private async Task MonitorLeaseAsync(
        MigrationRunRecord run,
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

    private sealed class StaleRunFenceException : Exception;

    private sealed class LeaseMonitorException(Exception inner) : Exception("The run lease monitor failed.", inner);

    private sealed class FatalRunOwnershipException(string message) : Exception(message);
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
