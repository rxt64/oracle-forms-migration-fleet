// Copyright (c) Microsoft. All rights reserved.

namespace OracleFormsMigrationFleet.Fleet;

/// <summary>What actually carries out a phase, so the console can stop implying every phase is agentic.</summary>
public enum PhaseEngine
{
    /// <summary>No adapter is registered. The phase appears in the plan and nothing performs it.</summary>
    NotImplemented,

    /// <summary>Parsing and emitting only. No model is consulted and the output is reproducible.</summary>
    Deterministic,

    /// <summary>Deterministic work, then a model reads the result and adds advisory findings.</summary>
    DeterministicWithModelReview,
}

/// <summary>
/// Who owns a phase, what performs it, and on which model deployment if any.
///
/// <see cref="Role"/> is a label on the plan, not a dispatch target: no code selects behaviour from it.
/// Saying so here is the point, because a list of specialist role names invites the opposite assumption.
/// </summary>
public sealed record PhaseAttribution(
    MigrationPhase Phase,
    FleetRole Role,
    PhaseEngine Engine,
    string? ModelDeployment,
    string Summary);

/// <summary>Which model backs a named capability, for display next to the work it performs.</summary>
public sealed record ModelAttribution(string Capability, string? Deployment, string Summary);

public sealed record FleetAttribution(
    IReadOnlyList<PhaseAttribution> Phases,
    IReadOnlyList<ModelAttribution> Models,
    IReadOnlyList<string> Disclaimers);

/// <summary>
/// Describes, without contacting anything, which part of the system performs each phase. Pure by design:
/// the answer must not depend on whether a model happens to be reachable.
/// </summary>
public static class FleetAttributionMap
{
    public static FleetAttribution Describe(
        IReadOnlyCollection<MigrationPhase> registeredAdapters,
        string? conversationDeployment,
        string? reviewDeployment)
    {
        ArgumentNullException.ThrowIfNull(registeredAdapters);

        List<PhaseAttribution> phases = [];
        foreach (PhaseOwnership ownership in MigrationRunPlanner.Lifecycle)
        {
            bool implemented = registeredAdapters.Contains(ownership.Phase);
            bool reviewed = implemented
                && ownership.Phase == MigrationPhase.DatabaseConversion
                && !string.IsNullOrWhiteSpace(reviewDeployment);

            PhaseEngine engine = (implemented, reviewed) switch
            {
                (false, _) => PhaseEngine.NotImplemented,
                (true, true) => PhaseEngine.DeterministicWithModelReview,
                _ => PhaseEngine.Deterministic,
            };

            phases.Add(new PhaseAttribution(
                ownership.Phase,
                ownership.Owner,
                engine,
                reviewed ? reviewDeployment : null,
                engine switch
                {
                    PhaseEngine.NotImplemented =>
                        "No adapter is registered. The plan covers this phase in writing; nothing performs it.",
                    PhaseEngine.DeterministicWithModelReview =>
                        $"Converted by deterministic code, then read by {reviewDeployment} for advisory findings that gate nothing.",
                    _ => "Performed by deterministic code. No model is consulted and the output is reproducible.",
                }));
        }

        List<ModelAttribution> models =
        [
            new("Conversation and tool calling",
                conversationDeployment,
                "Answers questions about a plan and calls the deterministic fleet as tools. It cannot change a plan, a gate, or an attestation."),
            new("Artifact review",
                reviewDeployment,
                "Reads generated DDL and reports suspected defects. Its findings are written to their own report and authorize nothing."),
        ];

        return new FleetAttribution(
            phases,
            models,
            [
                "Specialist role names label ownership in the plan. No code selects behaviour from a role, so the roles are not independent agents.",
                "Every gate, approval, attestation, and the target platform recommendation is decided by deterministic code. No model output reaches any of them.",
                "A model is consulted at exactly one point in a run: reading the converted schema after it is written.",
            ]);
    }
}
