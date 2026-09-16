// Copyright (c) Microsoft. All rights reserved.

namespace OracleFormsMigrationFleet.Fleet;

/// <summary>
/// The order the lifecycle phases run in, stated once.
///
/// <see cref="MigrationPhase"/> carries frozen numeric values so a persisted report keeps decoding to the
/// phase that wrote it, which means the enum's numbers are an identity scheme and not a sequence. Sorting
/// by the enum used to be the execution order, and adding a phase at the end of the value range would
/// silently have run source normalization after the conversion that depends on it.
/// </summary>
public static class MigrationLifecycle
{
    /// <summary>Execution order. Every phase appears exactly once; this is asserted by a test.</summary>
    public static IReadOnlyList<MigrationPhase> Order { get; } =
    [
        MigrationPhase.SourceAcquisition,
        MigrationPhase.SourceAnalysis,
        MigrationPhase.DocumentationGeneration,
        MigrationPhase.SourceNormalization,
        MigrationPhase.ApplicationCodeConversion,
        MigrationPhase.DatabaseConversion,
        MigrationPhase.BuildAndStaticValidation,
        MigrationPhase.DifferentialBehaviorTesting,
        MigrationPhase.SandboxDataMigration,
        MigrationPhase.DataReconciliation,
        MigrationPhase.HumanAcceptance,
        MigrationPhase.ProductionCutover,
    ];

    /// <summary>Position of a phase in <see cref="Order"/>. Unknown phases sort last rather than throwing.</summary>
    public static int PositionOf(MigrationPhase phase) =>
        s_position.TryGetValue(phase, out int index) ? index : int.MaxValue;

    private static readonly Dictionary<MigrationPhase, int> s_position =
        Order.Select((phase, index) => (phase, index)).ToDictionary(entry => entry.phase, entry => entry.index);
}
