// Copyright (c) Microsoft. All rights reserved.

using OracleFormsMigrationFleet.Fleet;
using OracleFormsMigrationFleet.Fleet.Execution;

namespace OracleFormsMigrationFleet.Hosting;

public enum MigrationRunState
{
    Queued,
    Leased,
    Running,
    Succeeded,
    Failed,
    Cancelled,
    Interrupted,
}

public sealed record MigrationRunRecord
{
    public required string RunId { get; init; }
    public required string TenantId { get; init; }
    public required string ProjectId { get; init; }
    public required string ActorObjectId { get; init; }
    public required string WorkspaceId { get; init; }
    public required string WorkspaceNodeId { get; init; }
    public required string WorkspaceOwnerId { get; init; }
    public required string SourceSnapshotHash { get; init; }
    public required string PlanInputHash { get; init; }
    public required string TargetProfileId { get; init; }
    public required int TargetProfileVersion { get; init; }
    public required string TargetProfileHash { get; init; }
    public required MigrationRunRequest Request { get; init; }
    public MigrationRunState State { get; init; } = MigrationRunState.Queued;
    public required DateTimeOffset EnqueuedUtc { get; init; }
    public DateTimeOffset? StartedUtc { get; init; }
    public DateTimeOffset? CompletedUtc { get; init; }
    public string? LeaseOwner { get; init; }
    public DateTimeOffset? LeaseExpiresUtc { get; init; }
    public long FenceToken { get; init; }
    public DateTimeOffset? CancelRequestedUtc { get; init; }
    public string? CancelRequestedByObjectId { get; init; }
    public long LastSequence { get; init; }
    public WorkbenchExecutionView? Outcome { get; init; }
    public string? FailureReason { get; init; }
    public int Version { get; init; } = 1;

    public bool IsTerminal => State is MigrationRunState.Succeeded or MigrationRunState.Failed or
        MigrationRunState.Cancelled or MigrationRunState.Interrupted;
}

public sealed record MigrationRunEvent(
    string RunId,
    long Sequence,
    DateTimeOffset RecordedUtc,
    string Level,
    string Text,
    ProgressSignal? Signal,
    WorkbenchExecutionView? Outcome = null);

public sealed record MigrationRunArtifact(
    string RunId,
    string Path,
    string Kind,
    string Description,
    long ByteLength,
    string ContentSha256);

public sealed record MigrationRunClaim(MigrationRunRecord Run, long FenceToken);

public interface IMigrationRunStore
{
    Task<MigrationRunRecord> EnqueueAsync(MigrationRunRecord run, CancellationToken cancellationToken);
    Task<MigrationRunRecord?> GetAsync(string tenantId, string runId, CancellationToken cancellationToken);
    Task<IReadOnlyList<MigrationRunRecord>> ForProjectAsync(
        string tenantId, string projectId, int limit, CancellationToken cancellationToken);
    Task<MigrationRunClaim?> ClaimAsync(
        string nodeId, string workerId, DateTimeOffset now, TimeSpan lease, CancellationToken cancellationToken);
    Task<int> ReconcileExpiredAsync(
        string currentNodeId, DateTimeOffset now, string reason, CancellationToken cancellationToken);
    Task<bool> MarkRunningAsync(
        string runId, long fenceToken, DateTimeOffset now, CancellationToken cancellationToken);
    Task<bool> RenewAsync(
        string runId, long fenceToken, TimeSpan lease, CancellationToken cancellationToken);
    Task<MigrationRunEvent?> AppendEventAsync(
        string runId,
        long fenceToken,
        DateTimeOffset recordedUtc,
        string level,
        string text,
        ProgressSignal? signal,
        WorkbenchExecutionView? outcome,
        CancellationToken cancellationToken);
    Task<IReadOnlyList<MigrationRunEvent>> EventsAsync(
        string tenantId, string runId, long afterSequence, CancellationToken cancellationToken);
    Task<bool> CompleteAsync(
        string runId,
        long fenceToken,
        MigrationRunState state,
        DateTimeOffset completedUtc,
        WorkbenchExecutionView? outcome,
        string? failureReason,
        IReadOnlyList<MigrationRunArtifact> artifacts,
        string terminalLevel,
        ProgressSignal terminalSignal,
        CancellationToken cancellationToken);
    Task<IReadOnlyList<MigrationRunArtifact>> ArtifactsAsync(
        string tenantId, string runId, CancellationToken cancellationToken);
    Task<bool> RequestCancellationAsync(
        string tenantId, string runId, string actorObjectId, DateTimeOffset requestedUtc, CancellationToken cancellationToken);
}
