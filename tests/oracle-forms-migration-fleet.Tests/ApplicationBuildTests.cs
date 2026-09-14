// Copyright (c) Microsoft. All rights reserved.

using OracleFormsMigrationFleet.Fleet;
using OracleFormsMigrationFleet.Fleet.Execution;
using OracleFormsMigrationFleet.Fleet.Execution.Adapters;
using OracleFormsMigrationFleet.Hosting;
using System.Diagnostics;

namespace OracleFormsMigrationFleet.Tests;

internal sealed class StubApplicationBuildGateway(
    ApplicationBuildResult java,
    ApplicationBuildResult react) : IApplicationBuildGateway
{
    public int JavaCalls { get; private set; }

    public int ReactCalls { get; private set; }

    public Task<ApplicationBuildResult> BuildJavaAsync(string workingDirectory, CancellationToken cancellationToken)
    {
        JavaCalls++;
        return Task.FromResult(java);
    }

    public Task<ApplicationBuildResult> BuildReactAsync(string workingDirectory, CancellationToken cancellationToken)
    {
        ReactCalls++;
        return Task.FromResult(react);
    }
}

public class BuildAndStaticValidationPhaseTests
{
    private static MigrationRunRequest Request() => new()
    {
        EngagementId = "ENG-BUILD",
        ApplicationName = "ORDERS",
        RequestedMode = ExecutionMode.GenerateArtifacts,
        Target = new TargetStack { Database = DatabaseTarget.PostgreSql },
        SourceRoot = "legacy/forms",
        OutputRoot = "out/orders",
        Evidence = Requests.CompleteEvidence(),
        PlanApproval = Requests.Approved("plan-owner@contoso.com"),
    };

    private static TemporaryWorkspace WorkspaceWithBuildDescriptors()
    {
        TemporaryWorkspace workspace = new();
        workspace.WriteFile("legacy/forms/db/schema.sql", OracleSamples.Schema);
        workspace.WriteFile("out/orders/application/backend/pom.xml", "<project />");
        workspace.WriteFile("out/orders/application/frontend/package.json", "{}");
        return workspace;
    }

    private static Task<MigrationExecutionResult> RunAsync(
        TemporaryWorkspace workspace,
        IApplicationBuildGateway? gateway) =>
        new MigrationExecutor(workspace.Root, [new BuildAndStaticValidationAdapter(gateway)])
            .ExecuteAsync(Request(), "builder@contoso.com");

    [Fact]
    public async Task Both_generated_tiers_must_build_before_the_phase_succeeds()
    {
        using TemporaryWorkspace workspace = WorkspaceWithBuildDescriptors();
        StubApplicationBuildGateway gateway = new(
            new ApplicationBuildResult("Java/Spring Boot", "mvn package", true, 0, "BUILD SUCCESS"),
            new ApplicationBuildResult("React/TypeScript", "npm run build", true, 0, "built"));

        MigrationExecutionResult result = await RunAsync(workspace, gateway);

        PhaseOutcome outcome = result.Phases.Single(phase => phase.Phase == MigrationPhase.BuildAndStaticValidation);
        Assert.Equal(PhaseExecutionState.Executed, outcome.State);
        Assert.Equal(1, gateway.JavaCalls);
        Assert.Equal(1, gateway.ReactCalls);
        Assert.True(workspace.Exists("out/orders/reports/build-and-static-analysis.json"));
    }

    [Fact]
    public async Task A_compiler_failure_fails_the_phase_and_retains_the_report()
    {
        using TemporaryWorkspace workspace = WorkspaceWithBuildDescriptors();
        StubApplicationBuildGateway gateway = new(
            new ApplicationBuildResult("Java/Spring Boot", "mvn package", true, 1, "COMPILATION ERROR"),
            new ApplicationBuildResult("React/TypeScript", "npm run build", true, 0, "built"));

        MigrationExecutionResult result = await RunAsync(workspace, gateway);

        PhaseOutcome outcome = result.Phases.Single(phase => phase.Phase == MigrationPhase.BuildAndStaticValidation);
        Assert.Equal(PhaseExecutionState.Failed, outcome.State);
        Assert.Single(outcome.Artifacts);
        Assert.Contains("COMPILATION ERROR", workspace.Read("out/orders/reports/build-and-static-analysis.json"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Missing_build_tools_fail_instead_of_claiming_static_validation()
    {
        using TemporaryWorkspace workspace = WorkspaceWithBuildDescriptors();
        StubApplicationBuildGateway gateway = new(
            new ApplicationBuildResult("Java/Spring Boot", "mvn package", false, -1, "mvn not found"),
            new ApplicationBuildResult("React/TypeScript", "npm run build", true, 0, "built"));

        MigrationExecutionResult result = await RunAsync(workspace, gateway);

        Assert.Equal(
            PhaseExecutionState.Failed,
            result.Phases.Single(phase => phase.Phase == MigrationPhase.BuildAndStaticValidation).State);
    }

    [Fact]
    public async Task Missing_generated_descriptors_never_invokes_the_gateway()
    {
        using TemporaryWorkspace workspace = new();
        workspace.WriteFile("legacy/forms/db/schema.sql", OracleSamples.Schema);
        StubApplicationBuildGateway gateway = new(
            new ApplicationBuildResult("Java", "mvn", true, 0, ""),
            new ApplicationBuildResult("React", "npm", true, 0, ""));

        MigrationExecutionResult result = await RunAsync(workspace, gateway);

        Assert.Equal(PhaseExecutionState.Failed, result.Phases.Single(phase => phase.Phase == MigrationPhase.BuildAndStaticValidation).State);
        Assert.Equal(0, gateway.JavaCalls);
        Assert.Equal(0, gateway.ReactCalls);
    }
}

public class ProcessApplicationBuildGatewayTests
{
    [Fact]
    public void Output_tail_is_bounded_and_preserves_the_end()
    {
        string output = new string('a', 40_000) + "THE END";

        string tail = ProcessApplicationBuildGateway.Tail(output);

        Assert.Equal(32_000, tail.Length);
        Assert.EndsWith("THE END", tail, StringComparison.Ordinal);
    }

    [Fact]
    public void Child_environment_is_allowlisted_and_drops_identity_material()
    {
        ProcessStartInfo start = new("dotnet");
        start.Environment["PATH"] = "safe-path";
        start.Environment["AZURE_CLIENT_ID"] = "secret-client";
        start.Environment["IDENTITY_ENDPOINT"] = "http://identity";
        start.Environment["NPM_CONFIG_TOKEN"] = "secret-token";

        ProcessApplicationBuildGateway.ApplyRestrictedEnvironment(start);

        Assert.Equal("safe-path", start.Environment["PATH"]);
        Assert.False(start.Environment.ContainsKey("AZURE_CLIENT_ID"));
        Assert.False(start.Environment.ContainsKey("IDENTITY_ENDPOINT"));
        Assert.False(start.Environment.ContainsKey("NPM_CONFIG_TOKEN"));
    }
}