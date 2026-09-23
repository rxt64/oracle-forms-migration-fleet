using OracleFormsMigrationFleet.Fleet.Execution;
using OracleFormsMigrationFleet.Hosting;

namespace OracleFormsMigrationFleet.Tests;

public class DotNetApplicationToolchainTests
{
    private const string TrxNamespace = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";

    [Fact]
    public async Task The_dotnet_legs_refuse_to_run_outside_the_isolating_sandbox()
    {
        if (OperatingSystem.IsLinux())
        {
            return;
        }

        using TemporaryWorkspace workspace = new();
        string reports = workspace.Absolute("reports");
        Directory.CreateDirectory(reports);

        ApplicationBuildResult build = await new ProcessApplicationBuildGateway()
            .BuildDotNetAsync(workspace.Root, CancellationToken.None);
        ApplicationTestRun test = await new ProcessApplicationTestGateway()
            .RunDotNetBackendTestsAsync(workspace.Root, reports, CancellationToken.None);

        Assert.False(build.ToolAvailable);
        Assert.Equal(".NET/ASP.NET Core", build.Component);
        Assert.Contains("bubblewrap", build.Output, StringComparison.OrdinalIgnoreCase);
        Assert.False(test.ToolAvailable);
        Assert.Contains("bubblewrap", test.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(Directory.GetFiles(reports));
    }

    [Fact]
    public void The_sandbox_tells_the_generated_suite_a_target_is_required_while_giving_it_none()
    {
        Dictionary<string, string> environment = ProcessApplicationTestGateway.DotNetSandboxEnvironment
            .ToDictionary(entry => entry.Name, entry => entry.Value, StringComparer.Ordinal);

        Assert.Equal("true", environment["GENERATED_SUITE_REQUIRE_TARGET"]);
        Assert.Equal(string.Empty, environment["TARGET_POSTGRES_CONNECTION"]);
        Assert.Equal("1", environment["DOTNET_CLI_TELEMETRY_OPTOUT"]);
        Assert.StartsWith("/home/tester", environment["NUGET_PACKAGES"], StringComparison.Ordinal);
        Assert.DoesNotContain(
            environment,
            entry => entry.Key.StartsWith("AZURE_", StringComparison.OrdinalIgnoreCase) ||
                entry.Key.Contains("TOKEN", StringComparison.OrdinalIgnoreCase) ||
                entry.Key.Contains("IDENTITY", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void A_trx_run_becomes_the_one_junit_document_the_verification_phase_reads()
    {
        using TemporaryWorkspace workspace = new();
        string results = Results(workspace, Trx(
            ("passing_case", "Passed", null),
            ("second_passing_case", "Passed", null),
            ("ignored_case", "NotExecuted", null)));
        string reports = workspace.Absolute("reports");
        DateTimeOffset started = DateTimeOffset.UtcNow.AddSeconds(-1);

        ApplicationTestRun adapted = ProcessApplicationTestGateway.WriteDotNetReport(
            Run(exitCode: 0),
            results,
            reports);

        Assert.Equal(0, adapted.ExitCode);
        Assert.Equal(["dotnet-backend.xml"], Directory.GetFiles(reports).Select(Path.GetFileName));
        ApplicationVerificationLegResult leg = JUnitReportReader.Classify(
            ApplicationVerificationLeg.BackendTests, adapted, reports, started, DateTimeOffset.UtcNow);
        Assert.Equal(ApplicationVerificationState.Executed, leg.State);
        Assert.Equal(3, leg.TestsRun);
        Assert.Equal(2, leg.TestsPassed);
        Assert.Equal(1, leg.TestsSkipped);
    }

    [Fact]
    public void A_failed_case_carries_its_trx_message_into_the_junit_failure()
    {
        using TemporaryWorkspace workspace = new();
        string results = Results(workspace, Trx(
            ("passing_case", "Passed", null),
            ("broken_case", "Failed", "Assert.Equal() Failure: expected 1, actual 0")));
        string reports = workspace.Absolute("reports");

        ApplicationTestRun adapted = ProcessApplicationTestGateway.WriteDotNetReport(
            Run(exitCode: 1),
            results,
            reports);

        ApplicationVerificationLegResult leg = JUnitReportReader.Classify(
            ApplicationVerificationLeg.BackendTests,
            adapted,
            reports,
            DateTimeOffset.UtcNow.AddSeconds(-1),
            DateTimeOffset.UtcNow);
        Assert.Equal(ApplicationVerificationState.TestsFailed, leg.State);
        Assert.Equal(1, leg.TestsFailed);
        Assert.Contains("expected 1, actual 0", Assert.Single(leg.Failures), StringComparison.Ordinal);
    }

    [Fact]
    public void A_run_that_executed_nothing_is_not_reported_as_a_pass()
    {
        using TemporaryWorkspace workspace = new();
        string skippedResults = Results(workspace, Trx(("ignored_case", "NotExecuted", null)));
        string emptyResults = workspace.Absolute("empty");
        Directory.CreateDirectory(emptyResults);

        ApplicationTestRun allSkipped = ProcessApplicationTestGateway.WriteDotNetReport(
            Run(exitCode: 0), skippedResults, workspace.Absolute("skipped-reports"));
        ApplicationTestRun noResult = ProcessApplicationTestGateway.WriteDotNetReport(
            Run(exitCode: 0), emptyResults, workspace.Absolute("empty-reports"));

        Assert.NotEqual(0, allSkipped.ExitCode);
        Assert.Contains("nothing was asserted", allSkipped.Output, StringComparison.Ordinal);
        Assert.NotEqual(0, noResult.ExitCode);
        Assert.Contains("no TRX result", noResult.Output, StringComparison.Ordinal);
        Assert.False(Directory.Exists(workspace.Absolute("empty-reports")));

        Assert.Equal(
            ApplicationVerificationState.ReportMalformed,
            JUnitReportReader.Classify(
                ApplicationVerificationLeg.BackendTests,
                allSkipped,
                workspace.Absolute("skipped-reports"),
                DateTimeOffset.UtcNow.AddSeconds(-1),
                DateTimeOffset.UtcNow).State);
    }

    private static ApplicationTestRun Run(int exitCode) =>
        new("dotnet test", ToolAvailable: true, TimedOut: false, exitCode, "runner output");

    private static string Results(TemporaryWorkspace workspace, string trx)
    {
        workspace.WriteFile("results/dotnet-backend.trx", trx);
        return workspace.Absolute("results");
    }

    private static string Trx(params (string Name, string Outcome, string? Message)[] cases)
    {
        IEnumerable<string> results = cases.Select((test, index) =>
            $"""
                 <UnitTestResult testId="t{index}" testName="{test.Name}" outcome="{test.Outcome}">
                   {(test.Message is null ? string.Empty : $"<Output><ErrorInfo><Message>{test.Message}</Message></ErrorInfo></Output>")}
                 </UnitTestResult>
             """);
        IEnumerable<string> definitions = cases.Select((test, index) =>
            $"""
                 <UnitTest id="t{index}" name="{test.Name}">
                   <TestMethod className="GeneratedApplication.Tests.AcceptanceTests" name="{test.Name}" />
                 </UnitTest>
             """);

        return $"""
            <?xml version="1.0" encoding="utf-8"?>
            <TestRun id="00000000-0000-0000-0000-000000000000" xmlns="{TrxNamespace}">
              <Results>
            {string.Join('\n', results)}
              </Results>
              <TestDefinitions>
            {string.Join('\n', definitions)}
              </TestDefinitions>
            </TestRun>
            """;
    }
}
