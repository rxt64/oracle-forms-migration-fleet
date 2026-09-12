// Copyright (c) Microsoft. All rights reserved.

using OracleFormsMigrationFleet.Fleet;
using OracleFormsMigrationFleet.Fleet.Agents;

namespace OracleFormsMigrationFleet.Tests;

/// <summary>Returns a scripted result per call so an exchange can be driven deterministically.</summary>
internal sealed class ScriptedAgent(FleetRole role, string name, params FleetAgentResult[] script) : IFleetAgent
{
    public int Calls { get; private set; }

    public FleetRole Role => role;

    public string Name => name;

    public Task<FleetAgentResult> RunAsync(FleetAgentRequest request, CancellationToken cancellationToken)
    {
        FleetAgentResult result = script[Math.Min(Calls, script.Length - 1)];
        Calls++;
        return Task.FromResult(result);
    }
}

internal sealed class ThrowingAgent(FleetRole role, string name) : IFleetAgent
{
    public FleetRole Role => role;

    public string Name => name;

    public Task<FleetAgentResult> RunAsync(FleetAgentRequest request, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("agent exploded");
}

public class CritiqueRepairOrchestratorTests
{
    private static FleetAgentRequest Request(string artifact = "CREATE TABLE t (a int);") =>
        new("ORDERS", DatabaseTarget.PostgreSql, artifact, []);

    private static FleetAgentResult Critique(params string[] findings) =>
        new(true, null, findings, findings.Length == 0 ? "clean" : $"{findings.Length} raised");

    private static FleetAgentResult Repair(string sql) => new(true, sql, [], "revised");

    [Fact]
    public async Task A_clean_critique_settles_without_calling_the_repairer()
    {
        ScriptedAgent critic = new(FleetRole.ValidationReviewer, "critic", Critique());
        ScriptedAgent repairer = new(FleetRole.DatabaseConverter, "repair", Repair("never"));

        OrchestrationResult result = await new CritiqueRepairOrchestrator(critic, repairer)
            .RunAsync(Request(), null, CancellationToken.None);

        Assert.Equal(TerminationReason.Settled, result.Termination);
        Assert.Equal(0, repairer.Calls);
        Assert.False(result.ProducedRevision);
    }

    [Fact]
    public async Task A_finding_is_repaired_and_then_re_reviewed()
    {
        ScriptedAgent critic = new(FleetRole.ValidationReviewer, "critic", Critique("t.a will fail"), Critique());
        ScriptedAgent repairer = new(FleetRole.DatabaseConverter, "repair", Repair("CREATE TABLE t (a bigint);"));

        OrchestrationResult result = await new CritiqueRepairOrchestrator(critic, repairer)
            .RunAsync(Request(), null, CancellationToken.None);

        Assert.Equal(TerminationReason.Settled, result.Termination);
        Assert.Equal(2, critic.Calls);
        Assert.Equal("CREATE TABLE t (a bigint);", result.ProposedArtifact);
    }

    [Fact]
    public async Task An_exchange_that_never_settles_stops_on_the_budget()
    {
        int round = 0;
        ScriptedAgent critic = new(FleetRole.ValidationReviewer, "critic", Critique("still broken"));
        ScriptedAgent repairer = new(
            FleetRole.DatabaseConverter,
            "repair",
            Repair("v1"),
            Repair("v2"),
            Repair("v3"));

        OrchestrationResult result = await new CritiqueRepairOrchestrator(critic, repairer, maxRounds: 2)
            .RunAsync(Request(), _ => round++, CancellationToken.None);

        Assert.Equal(TerminationReason.BudgetExhausted, result.Termination);
        Assert.Equal(2, critic.Calls);
        Assert.NotEmpty(result.OutstandingFindings);
    }

    [Fact]
    public async Task A_repairer_that_changes_nothing_stops_rather_than_looping()
    {
        const string same = "CREATE TABLE t (a int);";
        ScriptedAgent critic = new(FleetRole.ValidationReviewer, "critic", Critique("broken"));
        ScriptedAgent repairer = new(FleetRole.DatabaseConverter, "repair", Repair(same));

        OrchestrationResult result = await new CritiqueRepairOrchestrator(critic, repairer, maxRounds: 5)
            .RunAsync(Request(same), null, CancellationToken.None);

        Assert.Equal(TerminationReason.Settled, result.Termination);
        Assert.Equal(1, critic.Calls);
    }

    [Fact]
    public async Task A_throwing_agent_stops_the_exchange_instead_of_building_on_a_broken_step()
    {
        ScriptedAgent critic = new(FleetRole.ValidationReviewer, "critic", Critique("broken"));
        ThrowingAgent repairer = new(FleetRole.DatabaseConverter, "repair");

        OrchestrationResult result = await new CritiqueRepairOrchestrator(critic, repairer)
            .RunAsync(Request(), null, CancellationToken.None);

        Assert.Equal(TerminationReason.AgentFailed, result.Termination);
        Assert.False(result.ProducedRevision);
        Assert.Contains(result.Steps, step => !step.Succeeded);
    }

    [Fact]
    public async Task Every_step_records_the_role_and_agent_that_produced_it()
    {
        ScriptedAgent critic = new(FleetRole.ValidationReviewer, "critic", Critique("broken"), Critique());
        ScriptedAgent repairer = new(FleetRole.DatabaseConverter, "repair", Repair("fixed"));

        OrchestrationResult result = await new CritiqueRepairOrchestrator(critic, repairer)
            .RunAsync(Request(), null, CancellationToken.None);

        Assert.Equal([FleetRole.ValidationReviewer, FleetRole.DatabaseConverter, FleetRole.ValidationReviewer],
            result.Steps.Select(step => step.Role));
        Assert.Equal([1, 2, 3], result.Steps.Select(step => step.Index));
    }
}

public class SqlRepairAgentParsingTests
{
    [Fact]
    public void A_well_formed_reply_yields_the_repaired_sql()
    {
        Assert.True(SqlRepairAgent.TryReadSql("""{"sql":"CREATE TABLE t (a bigint);"}""", out string sql));
        Assert.Equal("CREATE TABLE t (a bigint);", sql);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{\"sql\":\"\"}")]
    [InlineData("{\"sql\":\"   \"}")]
    [InlineData("{\"other\":\"value\"}")]
    [InlineData("{\"sql\":123}")]
    public void Unusable_output_is_a_failed_step_not_a_silent_pass_through(string text) =>
        Assert.False(SqlRepairAgent.TryReadSql(text, out _));
}
