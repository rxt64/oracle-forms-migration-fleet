// Copyright (c) Microsoft. All rights reserved.

using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using OracleFormsMigrationFleet.Fleet.Execution;

namespace OracleFormsMigrationFleet.Hosting;

public sealed class FileMigrationRunStore : IMigrationRunStore
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> s_locks =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly JsonSerializerOptions s_json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _path;
    private readonly SemaphoreSlim _gate;

    public FileMigrationRunStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = Path.GetFullPath(path);
        _gate = s_locks.GetOrAdd(_path, _ => new SemaphoreSlim(1, 1));
    }

    public Task<MigrationRunRecord> EnqueueAsync(MigrationRunRecord run, CancellationToken cancellationToken) =>
        MutateAsync(document =>
        {
            if (document.Runs.Any(candidate => candidate.RunId == run.RunId))
            {
                throw new InvalidOperationException("A run with that identifier already exists.");
            }

            document.Runs.Add(run);
            return run;
        }, cancellationToken);

    public Task<MigrationRunRecord?> GetAsync(string tenantId, string runId, CancellationToken cancellationToken) =>
        ReadAsync(document => document.Runs.FirstOrDefault(run =>
            string.Equals(run.TenantId, tenantId, StringComparison.OrdinalIgnoreCase) && run.RunId == runId), cancellationToken);

    public Task<IReadOnlyList<MigrationRunRecord>> ForProjectAsync(
        string tenantId, string projectId, int limit, CancellationToken cancellationToken) =>
        ReadAsync(document => (IReadOnlyList<MigrationRunRecord>)[.. document.Runs
            .Where(run => string.Equals(run.TenantId, tenantId, StringComparison.OrdinalIgnoreCase) && run.ProjectId == projectId)
            .OrderByDescending(run => run.EnqueuedUtc)
            .Take(Math.Clamp(limit, 1, 100))], cancellationToken);

    public Task<MigrationRunClaim?> ClaimAsync(
        string nodeId, string workerId, DateTimeOffset now, TimeSpan lease, CancellationToken cancellationToken) =>
        MutateAsync<MigrationRunClaim?>(document =>
        {
            MigrationRunRecord? candidate = document.Runs
                .Where(run => run.WorkspaceNodeId == nodeId &&
                    (run.State == MigrationRunState.Queued ||
                     (run.State == MigrationRunState.Leased && run.LeaseExpiresUtc <= now)))
                .OrderBy(run => run.EnqueuedUtc)
                .FirstOrDefault();
            if (candidate is null)
            {
                return null;
            }

            MigrationRunRecord claimed = candidate with
            {
                State = MigrationRunState.Leased,
                LeaseOwner = workerId,
                LeaseExpiresUtc = now.Add(lease),
                FenceToken = candidate.FenceToken + 1,
                Version = candidate.Version + 1,
            };
            Replace(document, candidate, claimed);
            return new MigrationRunClaim(claimed, claimed.FenceToken);
        }, cancellationToken);

    public Task<int> ReconcileExpiredAsync(
        string currentNodeId, DateTimeOffset now, string reason, CancellationToken cancellationToken) =>
        MutateAsync(document =>
        {
            MigrationRunRecord[] expired = [.. document.Runs.Where(run =>
                (run.State == MigrationRunState.Running ||
                 (run.State == MigrationRunState.Leased && run.WorkspaceNodeId != currentNodeId)) &&
                run.LeaseExpiresUtc <= now)];
            foreach (MigrationRunRecord run in expired)
            {
                MigrationRunEvent terminal = new(
                    run.RunId,
                    run.LastSequence + 1,
                    now,
                    "error",
                    reason,
                    InterruptedSignal(reason));
                document.Events.Add(terminal);
                Replace(document, run, run with
                {
                    State = MigrationRunState.Interrupted,
                    CompletedUtc = now,
                    LeaseOwner = null,
                    LeaseExpiresUtc = null,
                    FenceToken = run.FenceToken + 1,
                    LastSequence = terminal.Sequence,
                    FailureReason = reason,
                    Version = run.Version + 1,
                });
            }
            return expired.Length;
        }, cancellationToken);

    public Task<bool> MarkRunningAsync(
        string runId, long fenceToken, DateTimeOffset now, CancellationToken cancellationToken) =>
        MutateAsync(document =>
        {
            MigrationRunRecord? run = Fenced(document, runId, fenceToken);
            if (run is null || run.State != MigrationRunState.Leased ||
                run.LeaseExpiresUtc <= now || run.CancelRequestedUtc is not null)
            {
                return false;
            }
            Replace(document, run, run with
            {
                State = MigrationRunState.Running,
                StartedUtc = run.StartedUtc ?? now,
                Version = run.Version + 1,
            });
            return true;
        }, cancellationToken);

    public Task<bool> RenewAsync(
        string runId, long fenceToken, TimeSpan lease, CancellationToken cancellationToken) =>
        MutateAsync(document =>
        {
            MigrationRunRecord? run = Fenced(document, runId, fenceToken);
            DateTimeOffset now = DateTimeOffset.UtcNow;
            if (run is null || run.IsTerminal || run.LeaseExpiresUtc <= now)
            {
                return false;
            }
            Replace(document, run, run with { LeaseExpiresUtc = now.Add(lease), Version = run.Version + 1 });
            return true;
        }, cancellationToken);

    public Task<MigrationRunEvent?> AppendEventAsync(
        string runId,
        long fenceToken,
        DateTimeOffset recordedUtc,
        string level,
        string text,
        ProgressSignal? signal,
        WorkbenchExecutionView? outcome,
        CancellationToken cancellationToken) =>
        MutateAsync<MigrationRunEvent?>(document =>
        {
            MigrationRunRecord? run = Fenced(document, runId, fenceToken);
            if (run is null || run.IsTerminal || run.LeaseExpiresUtc <= recordedUtc)
            {
                return null;
            }

            MigrationRunEvent appended = new(runId, run.LastSequence + 1, recordedUtc, level, text, signal, outcome);
            document.Events.Add(appended);
            Replace(document, run, run with { LastSequence = appended.Sequence, Version = run.Version + 1 });
            return appended;
        }, cancellationToken);

    public Task<IReadOnlyList<MigrationRunEvent>> EventsAsync(
        string tenantId, string runId, long afterSequence, CancellationToken cancellationToken) =>
        ReadAsync(document =>
        {
            bool visible = document.Runs.Any(run =>
                string.Equals(run.TenantId, tenantId, StringComparison.OrdinalIgnoreCase) && run.RunId == runId);
            return visible
                ? (IReadOnlyList<MigrationRunEvent>)[.. document.Events
                    .Where(item => item.RunId == runId && item.Sequence > afterSequence)
                    .OrderBy(item => item.Sequence)]
                : [];
        }, cancellationToken);

    public Task<bool> CompleteAsync(
        string runId,
        long fenceToken,
        MigrationRunState state,
        DateTimeOffset completedUtc,
        WorkbenchExecutionView? outcome,
        string? failureReason,
        IReadOnlyList<MigrationRunArtifact> artifacts,
        string terminalLevel,
        ProgressSignal terminalSignal,
        CancellationToken cancellationToken) =>
        MutateAsync(document =>
        {
            if (state is not (MigrationRunState.Succeeded or MigrationRunState.Failed or
                MigrationRunState.Cancelled or MigrationRunState.Interrupted))
            {
                throw new ArgumentOutOfRangeException(nameof(state), state, "A completed run requires a terminal state.");
            }

            MigrationRunRecord? run = Fenced(document, runId, fenceToken);
            if (run is null || run.IsTerminal || run.LeaseExpiresUtc <= completedUtc ||
                (state != MigrationRunState.Cancelled && run.CancelRequestedUtc is not null))
            {
                return false;
            }

            MigrationRunEvent terminal = new(
                runId,
                run.LastSequence + 1,
                completedUtc,
                terminalLevel,
                failureReason ?? string.Empty,
                terminalSignal,
                outcome);
            document.Events.Add(terminal);
            Replace(document, run, run with
            {
                State = state,
                CompletedUtc = completedUtc,
                LeaseOwner = null,
                LeaseExpiresUtc = null,
                Outcome = outcome,
                FailureReason = failureReason,
                LastSequence = terminal.Sequence,
                Version = run.Version + 1,
            });
            document.Artifacts.RemoveAll(artifact => artifact.RunId == runId);
            document.Artifacts.AddRange(artifacts);
            return true;
        }, cancellationToken);

    public Task<IReadOnlyList<MigrationRunArtifact>> ArtifactsAsync(
        string tenantId, string runId, CancellationToken cancellationToken) =>
        ReadAsync(document =>
        {
            bool visible = document.Runs.Any(run =>
                string.Equals(run.TenantId, tenantId, StringComparison.OrdinalIgnoreCase) && run.RunId == runId);
            return visible
                ? (IReadOnlyList<MigrationRunArtifact>)[.. document.Artifacts.Where(item => item.RunId == runId)]
                : [];
        }, cancellationToken);

    public Task<bool> RequestCancellationAsync(
        string tenantId, string runId, string actorObjectId, DateTimeOffset requestedUtc, CancellationToken cancellationToken) =>
        MutateAsync(document =>
        {
            MigrationRunRecord? run = document.Runs.FirstOrDefault(candidate =>
                string.Equals(candidate.TenantId, tenantId, StringComparison.OrdinalIgnoreCase) && candidate.RunId == runId);
            if (run is null || run.IsTerminal)
            {
                return false;
            }

            Replace(document, run, run with
            {
                CancelRequestedUtc = requestedUtc,
                CancelRequestedByObjectId = actorObjectId,
                Version = run.Version + 1,
            });
            return true;
        }, cancellationToken);

    private Task<bool> UpdateFencedAsync(
        string runId,
        long fenceToken,
        Func<MigrationRunRecord, MigrationRunRecord> update,
        CancellationToken cancellationToken) =>
        MutateAsync(document =>
        {
            MigrationRunRecord? run = Fenced(document, runId, fenceToken);
            if (run is null || run.IsTerminal)
            {
                return false;
            }

            Replace(document, run, update(run));
            return true;
        }, cancellationToken);

    private static MigrationRunRecord? Fenced(Document document, string runId, long fenceToken) =>
        document.Runs.FirstOrDefault(run => run.RunId == runId && run.FenceToken == fenceToken);

    private static ProgressSignal InterruptedSignal(string reason) => new(
        ProgressOperations.MigrationRun,
        ProgressActions.RunFailed,
        ProgressState.Failed,
        "Recovering a durable migration run.",
        reason,
        "Review retained events and destination state before retrying.");

    private static void Replace(Document document, MigrationRunRecord current, MigrationRunRecord replacement)
    {
        document.Runs.Remove(current);
        document.Runs.Add(replacement);
    }

    private async Task<T> ReadAsync<T>(Func<Document, T> read, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return read(Read());
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<T> MutateAsync<T>(Func<Document, T> mutate, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Document document = Read();
            T result = mutate(document);
            await WriteAsync(document, cancellationToken).ConfigureAwait(false);
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    private Document Read()
    {
        if (!File.Exists(_path))
        {
            return new Document();
        }

        try
        {
            return JsonSerializer.Deserialize<Document>(File.ReadAllText(_path), s_json)
                ?? throw new JsonException("The run store was empty.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            throw new InvalidOperationException($"The durable run store at '{_path}' could not be read.", exception);
        }
    }

    private async Task WriteAsync(Document document, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        string temporary = $"{_path}.{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(document, s_json), cancellationToken)
                .ConfigureAwait(false);
            File.Move(temporary, _path, overwrite: true);
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    private sealed class Document
    {
        public List<MigrationRunRecord> Runs { get; set; } = [];
        public List<MigrationRunEvent> Events { get; set; } = [];
        public List<MigrationRunArtifact> Artifacts { get; set; } = [];
    }
}
