// Copyright (c) Microsoft. All rights reserved.

namespace OracleFormsMigrationFleet.Fleet.Execution;

/// <summary>
/// What a progress frame means, stated by the producer rather than inferred from its text.
///
/// A stream that stops without a terminal frame is neither <see cref="Completed"/> nor
/// <see cref="Failed"/>: the consumer knows only that frames stopped arriving, which is a different
/// and less trustworthy fact than either outcome.
/// </summary>
public enum ProgressState
{
    /// <summary>Work is under way and further frames are expected.</summary>
    Running,

    /// <summary>The producer is alive and still working, but has nothing new to report yet.</summary>
    Waiting,

    /// <summary>Terminal. The operation finished and its result frame carries what it produced.</summary>
    Completed,

    /// <summary>Terminal. The operation stopped without producing its result.</summary>
    Failed,
}

/// <summary>Stable operation identifiers. The browser maps these to wording; it never parses text.</summary>
public static class ProgressOperations
{
    public const string SourceAcquisition = "source.acquire";
    public const string MigrationRun = "migration.run";
}

/// <summary>Stable action identifiers within an operation.</summary>
public static class ProgressActions
{
    public const string WorkspaceCreated = "source.workspace.created";
    public const string RepositoryClone = "source.clone";
    public const string ArchiveExtract = "source.extract";
    public const string RemoteDetached = "source.remote.detached";
    public const string Index = "source.index";
    public const string ArtifactCounted = "source.artifact.counted";
    public const string LockedReadOnly = "source.locked";
    public const string SourceReady = "source.ready";
    public const string SourceFailed = "source.failed";

    public const string PlanAuthorized = "run.authorized";
    public const string PhaseStarted = "run.phase.started";
    public const string PhaseSkipped = "run.phase.skipped";
    public const string PhaseNoAdapter = "run.phase.no-adapter";
    public const string PhaseBlocked = "run.phase.blocked";
    public const string PhaseFinished = "run.phase.finished";
    public const string RunHeartbeat = "run.heartbeat";
    public const string RunCompleted = "run.completed";
    public const string RunFailed = "run.failed";
}

/// <summary>
/// The typed framing of one progress frame: which operation and action produced it, what state the
/// operation is in, and the deterministic wording the browser shows instead of the raw log line.
///
/// Counts travel in <see cref="ArtifactCount"/> so the browser never has to read a number out of a
/// sentence. A frame that carries no count leaves it null rather than reporting zero.
/// </summary>
/// <param name="Operation">One of <see cref="ProgressOperations"/>.</param>
/// <param name="Action">One of <see cref="ProgressActions"/>.</param>
/// <param name="State">Whether the operation is running, waiting, or has reached a terminal outcome.</param>
/// <param name="Purpose">Why this operation is running at all.</param>
/// <param name="Observed">What has actually been observed so far. Never a prediction.</param>
/// <param name="NextAction">What happens next, or what the operator can do next.</param>
/// <param name="ArtifactKind">The artifact kind this frame counted, when it counted one.</param>
/// <param name="ArtifactCount">The count the producer measured. Null when nothing was counted.</param>
public sealed record ProgressSignal(
    string Operation,
    string Action,
    ProgressState State,
    string Purpose,
    string Observed,
    string NextAction,
    string? ArtifactKind = null,
    int? ArtifactCount = null);
