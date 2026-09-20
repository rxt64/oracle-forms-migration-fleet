// Copyright (c) Microsoft. All rights reserved.

using System.Globalization;
using OracleFormsMigrationFleet.Fleet;
using OracleFormsMigrationFleet.Fleet.Execution;

namespace OracleFormsMigrationFleet.Hosting;

/// <summary>Assigns one-based sequence numbers to a single progress stream.</summary>
internal sealed class WorkbenchProgressSequence
{
    private int _emitted;

    public int Next() => ++_emitted;
}

/// <summary>Builds the additive wire contract for one ordered progress frame.</summary>
internal static class WorkbenchProgressFrame
{
    public static Dictionary<string, object?> Replay(MigrationRunEvent item)
    {
        Dictionary<string, object?> frame = new(StringComparer.Ordinal)
        {
            ["level"] = item.Level,
            ["text"] = item.Text,
            ["sequence"] = item.Sequence,
            ["timestampUtc"] = item.RecordedUtc.ToString("O", CultureInfo.InvariantCulture),
        };
        if (item.Signal is { } signal)
        {
            frame["operation"] = signal.Operation;
            frame["action"] = signal.Action;
            frame["state"] = signal.State.ToString();
            frame["purpose"] = signal.Purpose;
            frame["observed"] = signal.Observed;
            frame["nextAction"] = signal.NextAction;
            frame["artifactKind"] = signal.ArtifactKind;
            frame["artifactCount"] = signal.ArtifactCount;
        }
        if (item.Outcome is not null)
        {
            frame["result"] = item.Outcome;
        }
        return frame;
    }

    public static Dictionary<string, object?> Create(
        WorkbenchProgressSequence sequence,
        string level,
        string text,
        ProgressSignal? signal,
        IReadOnlyDictionary<string, object?>? extra = null,
        Func<DateTimeOffset>? clock = null)
    {
        ArgumentNullException.ThrowIfNull(sequence);

        Dictionary<string, object?> frame = new(StringComparer.Ordinal)
        {
            ["level"] = level,
            ["text"] = text,
            ["sequence"] = sequence.Next(),
            ["timestampUtc"] = (clock?.Invoke() ?? DateTimeOffset.UtcNow).ToString("O", CultureInfo.InvariantCulture),
        };

        if (signal is not null)
        {
            frame["operation"] = signal.Operation;
            frame["action"] = signal.Action;
            frame["state"] = signal.State.ToString();
            frame["purpose"] = signal.Purpose;
            frame["observed"] = signal.Observed;
            frame["nextAction"] = signal.NextAction;
            frame["artifactKind"] = signal.ArtifactKind;
            frame["artifactCount"] = signal.ArtifactCount;
        }

        if (extra is not null)
        {
            foreach ((string key, object? value) in extra)
            {
                frame[key] = value;
            }
        }

        return frame;
    }
}

/// <summary>Classifies a run outcome without relying on log text.</summary>
internal static class WorkbenchRunOutcome
{
    public static bool Failed(IReadOnlyList<PhaseOutcome> phases)
    {
        int executed = phases.Count(phase => phase.State == PhaseExecutionState.Executed);
        return executed == 0 ||
            phases.Any(phase => phase.PlannedStatus == PhaseStatus.Planned && phase.State != PhaseExecutionState.Executed) ||
            phases.Any(phase => phase.State is PhaseExecutionState.Failed or PhaseExecutionState.BlockedByDependency);
    }
}