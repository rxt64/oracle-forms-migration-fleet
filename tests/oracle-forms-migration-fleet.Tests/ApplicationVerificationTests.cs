// Copyright (c) Microsoft. All rights reserved.

using OracleFormsMigrationFleet.Fleet;
using OracleFormsMigrationFleet.Fleet.Execution;
using OracleFormsMigrationFleet.Fleet.Execution.Adapters;

namespace OracleFormsMigrationFleet.Tests;

internal sealed class StubApplicationTestGateway(
    ApplicationTestRun backend,
    ApplicationTestRun? frontend = null,
    Action<string, ApplicationVerificationLeg>? writeReport = null) : IApplicationTestGateway
{
    public Task<ApplicationTestRun> RunBackendTestsAsync(
        string workingDirectory,
        string reportDirectory,
        CancellationToken cancellationToken)
    {
        writeReport?.Invoke(reportDirectory, ApplicationVerificationLeg.BackendTests);
        return Task.FromResult(backend);
    }

    public Task<ApplicationTestRun> RunFrontendTestsAsync(
        string workingDirectory,
        string reportDirectory,
        CancellationToken cancellationToken)
    {
        writeReport?.Invoke(reportDirectory, ApplicationVerificationLeg.FrontendInteractionTests);
        return Task.FromResult(frontend ?? backend);
    }
}

internal sealed class StubTargetVerificationGateway(
    ApplicationTestRun result,
    Action<string, ApplicationVerificationLeg>? writeReport = null) : ITargetApplicationVerificationGateway
{
    public Task<ApplicationTestRun> VerifyAsync(
        string schemaPath,
        string reportDirectory,
        CancellationToken cancellationToken)
    {
        writeReport?.Invoke(reportDirectory, ApplicationVerificationLeg.TargetDatabaseTests);
        return Task.FromResult(result);
    }
}

public sealed class ApplicationVerificationTests
{
    private static readonly ApplicationTestRun s_pass = new("test", true, false, 0, "passed");

    [Fact]
    public async Task Backend_tests_that_exit_zero_without_a_report_are_not_a_pass()
    {
        using TemporaryWorkspace workspace = new();
        workspace.WriteFile("out/orders/application/backend/pom.xml", "<project />");
        StubApplicationTestGateway gateway = new(new ApplicationTestRun(
            "mvn test",
            ToolAvailable: true,
            TimedOut: false,
            ExitCode: 0,
            Output: "BUILD SUCCESS"));
        GeneratedApplicationVerificationAdapter adapter = new(gateway);
        PhaseExecutionContext context = new(
            workspace.Root,
            "legacy/forms",
            "out/orders",
            Plan(),
            Request(),
            (_, _) => { });

        PhaseExecutionResult result = await adapter.ExecuteAsync(context, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Single(result.Artifacts);
        string report = workspace.Read("out/orders/reports/generated-application-verification.json");
        Assert.Contains("\"state\": \"ReportMissing\"", report, StringComparison.Ordinal);
        Assert.Contains("No Oracle Forms runtime", report, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false, false, 1, ApplicationVerificationState.TestsFailed)]
    [InlineData(false, true, -2, ApplicationVerificationState.Timeout)]
    [InlineData(true, false, -1, ApplicationVerificationState.ToolUnavailable)]
    public async Task Process_failures_never_become_verified(
        bool unavailable,
        bool timedOut,
        int exitCode,
        ApplicationVerificationState expected)
    {
        using TemporaryWorkspace workspace = WorkspaceWithGeneratedApplication();
        ApplicationTestRun run = new("test", !unavailable, timedOut, exitCode, "failed");
        GeneratedApplicationVerificationAdapter adapter = new(
            new StubApplicationTestGateway(run, s_pass, WritePassingReport),
            new StubTargetVerificationGateway(s_pass, WritePassingReport));

        PhaseExecutionResult result = await adapter.ExecuteAsync(Context(workspace), CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains($"\"state\": \"{expected}\"", workspace.Read(ReportPath()), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Dependency_setup_failure_is_reported_distinctly()
    {
        using TemporaryWorkspace workspace = WorkspaceWithGeneratedApplication();
        ApplicationTestRun setupFailure = new("npm install", true, false, 1, "dependency resolution failed", SetupFailed: true);
        GeneratedApplicationVerificationAdapter adapter = new(
            new StubApplicationTestGateway(s_pass, setupFailure, WritePassingReport),
            new StubTargetVerificationGateway(s_pass, WritePassingReport));

        PhaseExecutionResult result = await adapter.ExecuteAsync(Context(workspace), CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains("\"state\": \"SetupFailed\"", workspace.Read(ReportPath()), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Frontend_and_target_failures_are_reported_as_independent_legs()
    {
        using TemporaryWorkspace workspace = WorkspaceWithGeneratedApplication();
        ApplicationTestRun failure = new("test", true, false, 1, "assertion failed");
        void WriteFailing(string directory, ApplicationVerificationLeg _) => WriteXml(
            directory,
            "<testsuite tests=\"1\" failures=\"1\" errors=\"0\" skipped=\"0\"><testcase><failure message=\"assertion failed\" /></testcase></testsuite>");
        GeneratedApplicationVerificationAdapter adapter = new(
            new StubApplicationTestGateway(s_pass, failure, (directory, leg) =>
            {
                if (leg == ApplicationVerificationLeg.BackendTests)
                {
                    WritePassingReport(directory, leg);
                }
                else
                {
                    WriteFailing(directory, leg);
                }
            }),
            new StubTargetVerificationGateway(failure, WriteFailing));

        PhaseExecutionResult result = await adapter.ExecuteAsync(Context(workspace), CancellationToken.None);

        Assert.False(result.Succeeded);
        string report = workspace.Read(ReportPath());
        Assert.Contains("\"leg\": \"FrontendInteractionTests\"", report, StringComparison.Ordinal);
        Assert.Contains("\"leg\": \"TargetDatabaseTests\"", report, StringComparison.Ordinal);
        Assert.Equal(2, Count(report, "\"state\": \"TestsFailed\""));
    }

    [Theory]
    [InlineData("<not-xml", ApplicationVerificationState.ReportMalformed)]
    [InlineData("<testsuite tests=\"0\" failures=\"0\" errors=\"0\" skipped=\"0\" />", ApplicationVerificationState.ReportMalformed)]
    [InlineData("<testsuite tests=\"1\" failures=\"0\" errors=\"0\" skipped=\"0\" />", ApplicationVerificationState.ReportMalformed)]
    [InlineData("<testsuite tests=\"1\" failures=\"0\" errors=\"0\" skipped=\"1\"><testcase><skipped /></testcase></testsuite>", ApplicationVerificationState.ReportMalformed)]
    [InlineData("<testsuite tests=\"1\" failures=\"0\" errors=\"0\" skipped=\"0\"><testcase><failure message=\"undeclared\" /></testcase></testsuite>", ApplicationVerificationState.ReportMalformed)]
    [InlineData("<testsuite tests=\"1\" failures=\"1\" errors=\"0\" skipped=\"0\"><testcase><wrapper><failure message=\"nested\" /></wrapper></testcase></testsuite>", ApplicationVerificationState.ReportMalformed)]
    [InlineData("<testsuite tests=\"1\" failures=\"1\" errors=\"0\" skipped=\"1\"><testcase><failure /><skipped /></testcase></testsuite>", ApplicationVerificationState.ReportMalformed)]
    [InlineData("<testsuites tests=\"1\" failures=\"0\" errors=\"0\" skipped=\"0\"><testsuite tests=\"1\" failures=\"1\" errors=\"0\" skipped=\"0\"><testcase /></testsuite></testsuites>", ApplicationVerificationState.ReportMalformed)]
    [InlineData("<testsuites tests=\"1\" failures=\"0\" errors=\"0\" skipped=\"0\"><testsuite tests=\"1\" failures=\"0\" errors=\"0\" skipped=\"0\"><testsuite tests=\"1\" failures=\"0\" errors=\"0\" skipped=\"0\"><testcase /></testsuite></testsuite></testsuites>", ApplicationVerificationState.ReportMalformed)]
    [InlineData("<testsuites tests=\"2\" failures=\"1\" errors=\"0\" skipped=\"0\"><testsuite tests=\"1\" failures=\"0\" errors=\"0\" skipped=\"0\"><testcase /></testsuite><testcase><failure /></testcase></testsuites>", ApplicationVerificationState.ReportMalformed)]
    [InlineData("<testsuite tests=\"2\" failures=\"1\" errors=\"0\" skipped=\"0\"><testcase /><wrapper><testcase><failure /></testcase></wrapper></testsuite>", ApplicationVerificationState.ReportMalformed)]
    [InlineData("<testsuite tests=\"1\" failures=\"1\" errors=\"0\" skipped=\"0\"><testcase><failure message=\"assertion failed\" /></testcase></testsuite>", ApplicationVerificationState.TestsFailed)]
    public async Task Invalid_or_failing_machine_reports_never_pass(
        string xml,
        ApplicationVerificationState expected)
    {
        using TemporaryWorkspace workspace = WorkspaceWithGeneratedApplication();
        void WriteReport(string directory, ApplicationVerificationLeg _) => WriteXml(directory, xml);
        GeneratedApplicationVerificationAdapter adapter = new(
            new StubApplicationTestGateway(s_pass, s_pass, WriteReport),
            new StubTargetVerificationGateway(s_pass, WriteReport));

        PhaseExecutionResult result = await adapter.ExecuteAsync(Context(workspace), CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains($"\"state\": \"{expected}\"", workspace.Read(ReportPath()), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Credential_like_junit_failure_text_is_withheld()
    {
        using TemporaryWorkspace workspace = WorkspaceWithGeneratedApplication();
        string? rawReportDirectory = null;
        void WriteSecretFailure(string directory, ApplicationVerificationLeg _)
        {
            rawReportDirectory = directory;
            WriteXml(
                directory,
                "<testsuite tests=\"1\" failures=\"1\" errors=\"0\" skipped=\"0\"><testcase><failure message=\"password=hunter2\" /></testcase></testsuite>");
        }
        ApplicationTestRun failure = new("test", true, false, 1, "password=hunter2");
        GeneratedApplicationVerificationAdapter adapter = new(
            new StubApplicationTestGateway(failure, s_pass, (directory, leg) =>
            {
                if (leg == ApplicationVerificationLeg.BackendTests) WriteSecretFailure(directory, leg);
                else WritePassingReport(directory, leg);
            }),
            new StubTargetVerificationGateway(s_pass, WritePassingReport));

        await adapter.ExecuteAsync(Context(workspace), CancellationToken.None);

        string report = workspace.Read(ReportPath());
        Assert.DoesNotContain("hunter2", report, StringComparison.Ordinal);
        Assert.Contains("withheld", report, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(rawReportDirectory);
        Assert.False(Directory.Exists(rawReportDirectory));
        Assert.False(Directory.Exists(workspace.Absolute("out/orders/.verification")));
    }

    [Fact]
    public async Task Stale_report_is_rejected()
    {
        using TemporaryWorkspace workspace = WorkspaceWithGeneratedApplication();
        void WriteStale(string directory, ApplicationVerificationLeg _)
        {
            string path = WriteXml(directory, PassingXml());
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(-5));
        }
        GeneratedApplicationVerificationAdapter adapter = new(
            new StubApplicationTestGateway(s_pass, s_pass, WriteStale),
            new StubTargetVerificationGateway(s_pass, WritePassingReport));

        PhaseExecutionResult result = await adapter.ExecuteAsync(Context(workspace), CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains("\"state\": \"ReportStale\"", workspace.Read(ReportPath()), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_report_left_by_an_earlier_run_is_deleted_before_execution()
    {
        using TemporaryWorkspace workspace = WorkspaceWithGeneratedApplication();
        workspace.WriteFile("out/orders/.verification/backend/old.xml", PassingXml());
        GeneratedApplicationVerificationAdapter adapter = new(
            new StubApplicationTestGateway(s_pass, s_pass, (directory, leg) =>
            {
                if (leg == ApplicationVerificationLeg.FrontendInteractionTests)
                {
                    WritePassingReport(directory, leg);
                }
            }),
            new StubTargetVerificationGateway(s_pass, WritePassingReport));

        PhaseExecutionResult result = await adapter.ExecuteAsync(Context(workspace), CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains("\"state\": \"ReportMissing\"", workspace.Read(ReportPath()), StringComparison.Ordinal);
    }

    [Fact]
    public async Task All_three_executed_legs_pass_and_assert_no_source_equivalence()
    {
        using TemporaryWorkspace workspace = WorkspaceWithGeneratedApplication();
        GeneratedApplicationVerificationAdapter adapter = PassingAdapter();

        PhaseExecutionResult result = await adapter.ExecuteAsync(Context(workspace), CancellationToken.None);

        Assert.True(result.Succeeded);
        string report = workspace.Read(ReportPath());
        Assert.Equal(3, Count(report, "\"state\": \"Executed\""));
        Assert.Contains(GeneratedApplicationVerificationReport.NoEquivalenceClaim, report, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Successful_phase_mints_only_generated_application_attestation()
    {
        using TemporaryWorkspace workspace = WorkspaceWithGeneratedApplication();
        MigrationExecutor executor = new(workspace.Root, [PassingAdapter()]);

        MigrationExecutionResult result = await executor.ExecuteAsync(Request() with
        {
            RequestedMode = ExecutionMode.SandboxMigration,
            ExecutionApproval = Requests.Approved("sandbox-approver@contoso.com"),
        }, "builder@contoso.com");

        MigrationAttestation attestation = Assert.Single(result.Attestations);
        Assert.Equal(AttestationKind.GeneratedApplicationTestsPassed, attestation.Kind);
        Assert.DoesNotContain(result.Attestations, item => item.Kind == AttestationKind.DifferentialBehaviorTestPassed);
    }

    private static GeneratedApplicationVerificationAdapter PassingAdapter() => new(
        new StubApplicationTestGateway(s_pass, s_pass, WritePassingReport),
        new StubTargetVerificationGateway(s_pass, WritePassingReport));

    private static TemporaryWorkspace WorkspaceWithGeneratedApplication()
    {
        TemporaryWorkspace workspace = new();
        workspace.WriteFile("out/orders/application/backend/pom.xml", "<project />");
        workspace.WriteFile("out/orders/application/frontend/package.json", "{}");
        workspace.WriteFile("out/orders/database/postgresql/schema/schema.sql", "create table verified(id integer);");
        return workspace;
    }

    private static PhaseExecutionContext Context(TemporaryWorkspace workspace) => new(
        workspace.Root,
        "legacy/forms",
        "out/orders",
        Plan(),
        Request(),
        (_, _) => { });

    private static void WritePassingReport(string directory, ApplicationVerificationLeg _) =>
        WriteXml(directory, PassingXml());

    private static string WriteXml(string directory, string xml)
    {
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "results.xml");
        File.WriteAllText(path, xml);
        return path;
    }

    private static string PassingXml() =>
        "<testsuite tests=\"1\" failures=\"0\" errors=\"0\" skipped=\"0\"><testcase name=\"passes\" /></testsuite>";

    private static string ReportPath() => "out/orders/reports/generated-application-verification.json";

    private static int Count(string value, string needle) =>
        (value.Length - value.Replace(needle, string.Empty, StringComparison.Ordinal).Length) / needle.Length;

    private static PhasePlan Plan() => new(
        MigrationPhase.GeneratedApplicationVerification,
        FleetRole.BuildAndTestEngineer,
        PhaseStatus.Planned,
        MutationClass.WorkspaceArtifactWrite,
        ExecutionMode.GenerateArtifacts,
        RequiresApproval: false,
        "Execute generated application tests.",
        [],
        [],
        [],
        []);

    private static MigrationRunRequest Request() => new()
    {
        EngagementId = "ENG-VERIFY",
        ApplicationName = "ORDERS",
        RequestedMode = ExecutionMode.GenerateArtifacts,
        Target = new TargetStack { Database = DatabaseTarget.PostgreSql },
        SourceRoot = "legacy/forms",
        OutputRoot = "out/orders",
        Evidence = Requests.CompleteEvidence(),
        PlanApproval = Requests.Approved("plan-owner@contoso.com"),
    };
}