// Copyright (c) Microsoft. All rights reserved.

using OracleFormsMigrationFleet.Fleet;

namespace OracleFormsMigrationFleet.Tests;

/// <summary>
/// <see cref="MigrationPhase"/> numbers are an identity scheme that persisted reports, manifests, and
/// stored plans already contain, and <see cref="MigrationLifecycle.Order"/> is the separate statement of
/// execution order. Conflating the two is what would run normalization after the conversion that reads it,
/// so both are pinned here rather than left to declaration order.
/// </summary>
public class MigrationLifecycleTests
{
    /// <summary>
    /// The exact wire values. Changing one renumbers a phase inside output that is already on disk, so a
    /// stored report would decode as a phase that never wrote it. New phases take a new value at the end.
    /// </summary>
    [Theory]
    [InlineData(MigrationPhase.SourceAcquisition, 0)]
    [InlineData(MigrationPhase.SourceAnalysis, 1)]
    [InlineData(MigrationPhase.DocumentationGeneration, 2)]
    [InlineData(MigrationPhase.ApplicationCodeConversion, 3)]
    [InlineData(MigrationPhase.DatabaseConversion, 4)]
    [InlineData(MigrationPhase.BuildAndStaticValidation, 5)]
    [InlineData(MigrationPhase.DifferentialBehaviorTesting, 6)]
    [InlineData(MigrationPhase.SandboxDataMigration, 7)]
    [InlineData(MigrationPhase.DataReconciliation, 8)]
    [InlineData(MigrationPhase.HumanAcceptance, 9)]
    [InlineData(MigrationPhase.ProductionCutover, 10)]
    [InlineData(MigrationPhase.SourceNormalization, 11)]
    public void Every_phase_keeps_its_persisted_numeric_value(MigrationPhase phase, int expected) =>
        Assert.Equal(expected, (int)phase);

    [Fact]
    public void The_numeric_map_is_exactly_these_twelve_phases()
    {
        // Catches a phase added without deciding its wire value, and a value reused for two phases.
        MigrationPhase[] all = Enum.GetValues<MigrationPhase>();

        Assert.Equal(12, all.Length);
        Assert.Equal([0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11], all.Select(phase => (int)phase).Order());
    }

    [Fact]
    public void The_execution_order_contains_every_phase_exactly_once()
    {
        Assert.Equal(Enum.GetValues<MigrationPhase>().Length, MigrationLifecycle.Order.Count);
        Assert.Equal(Enum.GetValues<MigrationPhase>().Order(), MigrationLifecycle.Order.Order());
        Assert.Equal(MigrationLifecycle.Order.Count, MigrationLifecycle.Order.Distinct().Count());
    }

    [Fact]
    public void Execution_order_is_the_lifecycle_order_and_not_the_numeric_order()
    {
        Assert.Equal(
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
            ],
            MigrationLifecycle.Order);
    }

    /// <summary>
    /// The one ordering relationship a numeric sort gets wrong. Normalization decides whether the Forms
    /// source is readable at all, so a conversion that ran first would generate from a tree nothing in the
    /// run had adjudicated.
    /// </summary>
    [Fact]
    public void Source_normalization_runs_before_application_code_conversion()
    {
        Assert.True(
            MigrationLifecycle.PositionOf(MigrationPhase.SourceNormalization)
            < MigrationLifecycle.PositionOf(MigrationPhase.ApplicationCodeConversion));

        Assert.True((int)MigrationPhase.SourceNormalization > (int)MigrationPhase.ApplicationCodeConversion);
    }

    [Fact]
    public void Every_phase_has_a_position_and_an_unknown_phase_sorts_last()
    {
        Assert.All(Enum.GetValues<MigrationPhase>(), phase => Assert.NotEqual(int.MaxValue, MigrationLifecycle.PositionOf(phase)));
        Assert.Equal(int.MaxValue, MigrationLifecycle.PositionOf((MigrationPhase)99));
    }
}
