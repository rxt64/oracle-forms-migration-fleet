// Copyright (c) Microsoft. All rights reserved.

using OracleFormsMigrationFleet.Fleet;
using OracleFormsMigrationFleet.Fleet.Execution;

namespace OracleFormsMigrationFleet.Tests;

public class DataReconciliationPhaseTests
{
    private const string Operator = "migration-operator@contoso.com";

    private static MigrationRunRequest Request() => new()
    {
        EngagementId = "ENG-RECON",
        ApplicationName = "ORDERS",
        RequestedMode = ExecutionMode.SandboxMigration,
        Target = new TargetStack { Database = DatabaseTarget.PostgreSql },
        SourceRoot = "legacy/forms",
        OutputRoot = "out/orders",
        Evidence = Requests.CompleteEvidence(),
        ExecutionApproval = Requests.Approved("release-manager@contoso.com"),
    };

    private static TemporaryWorkspace SeededWorkspace()
    {
        TemporaryWorkspace workspace = new();
        workspace.WriteFile("legacy/forms/db/001_schema.sql", OracleSamples.Schema);
        workspace.WriteFile(
            "legacy/forms/db/002_data.sql",
            "INSERT INTO ORDERS (ID) VALUES (1);\nINSERT INTO ORDERS (ID) VALUES (2);\nINSERT INTO ORDERS (ID) VALUES (3);");
        return workspace;
    }

    private static async Task<MigrationExecutionResult> RunAsync(StubDataGateway? gateway)
    {
        using TemporaryWorkspace workspace = SeededWorkspace();

        return await new MigrationExecutor(
                workspace.Root,
                [new Fleet.Execution.Adapters.DataReconciliationAdapter(gateway)])
            .ExecuteAsync(Request(), Operator);
    }

    private static PhaseOutcome Outcome(MigrationExecutionResult result) =>
        result.Phases.Single(phase => phase.Phase == MigrationPhase.DataReconciliation);

    [Fact]
    public async Task A_target_that_matches_the_source_is_attested()
    {
        StubDataGateway gateway = new(new DataMigrationOutcome(0, 0, [], []))
        {
            Counts = [new TableRowCount("orders", 3)],
        };

        MigrationExecutionResult result = await RunAsync(gateway);

        Assert.Equal(PhaseExecutionState.Executed, Outcome(result).State);
        Assert.Contains(result.Attestations, attestation => attestation.Kind == AttestationKind.DataReconciliationPassed);
    }

    [Fact]
    public async Task A_short_target_fails_and_attests_nothing()
    {
        // This is the case a load reports as success: the rows that errored are simply not there.
        StubDataGateway gateway = new(new DataMigrationOutcome(0, 0, [], []))
        {
            Counts = [new TableRowCount("orders", 1)],
        };

        MigrationExecutionResult result = await RunAsync(gateway);

        Assert.Equal(PhaseExecutionState.Failed, Outcome(result).State);
        Assert.Empty(result.Attestations);
        Assert.Contains(Outcome(result).Findings, finding => finding.Contains("expected 3", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_missing_table_is_a_difference_rather_than_a_crash()
    {
        StubDataGateway gateway = new(new DataMigrationOutcome(0, 0, [], []))
        {
            Counts = [new TableRowCount("orders", -1)],
        };

        MigrationExecutionResult result = await RunAsync(gateway);

        Assert.Equal(PhaseExecutionState.Failed, Outcome(result).State);
        Assert.Empty(result.Attestations);
    }

    [Fact]
    public async Task Without_a_gateway_nothing_is_reconciled_and_nothing_is_attested()
    {
        MigrationExecutionResult result = await RunAsync(null);

        Assert.Equal(PhaseExecutionState.Failed, Outcome(result).State);
        Assert.Empty(result.Attestations);
    }
}
