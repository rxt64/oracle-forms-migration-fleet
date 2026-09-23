// Copyright (c) Microsoft. All rights reserved.

using System.Xml.Linq;

namespace OracleFormsMigrationFleet.Fleet.Execution;

public enum ApplicationVerificationLeg
{
    BackendTests,
    FrontendInteractionTests,
    TargetDatabaseTests,
}

public enum ApplicationVerificationState
{
    NotAttempted,
    ToolUnavailable,
    SetupFailed,
    Timeout,
    TestsFailed,
    ReportMissing,
    ReportMalformed,
    ReportStale,
    Executed,
}

public sealed record ApplicationTestRun(
    string Command,
    bool ToolAvailable,
    bool TimedOut,
    int ExitCode,
    string Output,
    bool SetupFailed = false);

public sealed record ApplicationVerificationLegResult(
    ApplicationVerificationLeg Leg,
    ApplicationVerificationState State,
    string Command,
    int ExitCode,
    int TestsRun,
    int TestsPassed,
    int TestsFailed,
    int TestsSkipped,
    DateTimeOffset StartedUtc,
    DateTimeOffset CompletedUtc,
    IReadOnlyList<string> Failures,
    string Output)
{
    public bool Succeeded => State == ApplicationVerificationState.Executed && TestsPassed > 0;
}

public sealed record GeneratedApplicationVerificationReport(
    string SchemaVersion,
    string Application,
    bool Succeeded,
    IReadOnlyList<ApplicationVerificationLegResult> Legs)
{
    public const string NoEquivalenceClaim =
        "None. Only the generated application was executed. No Oracle Forms runtime and no Oracle Database instance was contacted, and no behavioral equivalence with the source is asserted.";

    public string EquivalenceClaim => NoEquivalenceClaim;
}

public interface IApplicationTestGateway
{
    Task<ApplicationTestRun> RunBackendTestsAsync(
        string workingDirectory,
        string reportDirectory,
        CancellationToken cancellationToken);

    Task<ApplicationTestRun> RunFrontendTestsAsync(
        string workingDirectory,
        string reportDirectory,
        CancellationToken cancellationToken);

    /// <summary>
    /// Runs a generated .NET back end's own test suite. The default states that this host cannot run one, so
    /// an unavailable toolchain classifies as <see cref="ApplicationVerificationState.ToolUnavailable"/>
    /// rather than as a leg that passed without executing anything.
    /// </summary>
    Task<ApplicationTestRun> RunDotNetBackendTestsAsync(
        string workingDirectory,
        string reportDirectory,
        CancellationToken cancellationToken) =>
        Task.FromResult(new ApplicationTestRun(
            "dotnet test",
            ToolAvailable: false,
            TimedOut: false,
            ExitCode: -1,
            Output: "This test gateway has no .NET toolchain, so the generated ASP.NET Core test suite was not started."));
}

public interface ITargetApplicationVerificationGateway
{
    Task<ApplicationTestRun> VerifyAsync(
        string schemaPath,
        string reportDirectory,
        CancellationToken cancellationToken);
}

internal static class JUnitReportReader
{
    public static ApplicationVerificationLegResult Classify(
        ApplicationVerificationLeg leg,
        ApplicationTestRun run,
        string reportDirectory,
        DateTimeOffset startedUtc,
        DateTimeOffset completedUtc)
    {
        if (!run.ToolAvailable)
        {
            return Result(ApplicationVerificationState.ToolUnavailable);
        }

        if (run.TimedOut)
        {
            return Result(ApplicationVerificationState.Timeout);
        }

        if (run.SetupFailed)
        {
            return Result(ApplicationVerificationState.SetupFailed);
        }

        string[] reports = Directory.Exists(reportDirectory)
            ? Directory.GetFiles(reportDirectory, "*.xml", SearchOption.AllDirectories)
            : [];
        if (reports.Length == 0)
        {
            return Result(ApplicationVerificationState.ReportMissing);
        }

        if (reports.Any(path => File.GetLastWriteTimeUtc(path) < startedUtc.UtcDateTime.AddSeconds(-1)))
        {
            return Result(ApplicationVerificationState.ReportStale);
        }

        try
        {
            List<XElement> roots = [];
            foreach (string report in reports)
            {
                XElement root = XDocument.Load(report).Root
                    ?? throw new InvalidDataException("The JUnit document has no root element.");
                if (root.Name.LocalName == "testsuite")
                {
                    roots.Add(root);
                }
                else if (root.Name.LocalName == "testsuites")
                {
                    roots.Add(root);
                }
                else
                {
                    throw new InvalidDataException("The JUnit document root is not testsuite or testsuites.");
                }
            }

            List<XElement> suites = [.. roots.SelectMany(root => root.Name.LocalName == "testsuite"
                ? [root]
                : root.Elements().Where(element => element.Name.LocalName == "testsuite"))];
            bool suiteNestingIsStrict = roots.All(root =>
                root.Descendants().Where(element => element.Name.LocalName == "testsuite").All(suite =>
                    root.Name.LocalName == "testsuite"
                        ? ReferenceEquals(suite, root)
                        : ReferenceEquals(suite.Parent, root)));
            bool testcasePlacementIsStrict = roots
                .SelectMany(root => root.Descendants().Where(element => element.Name.LocalName == "testcase"))
                .All(testcase => suites.Any(suite => ReferenceEquals(testcase.Parent, suite)));
            List<XElement> cases = [.. suites.SelectMany(suite =>
                suite.Elements().Where(element => element.Name.LocalName == "testcase"))];
            List<XElement> outcomes = [.. cases.SelectMany(test => test.Elements()
                .Where(element => element.Name.LocalName is "failure" or "error" or "skipped"))];
            bool structureIsStrict = roots.All(root =>
                root.Descendants()
                    .Where(element => element.Name.LocalName is "failure" or "error" or "skipped")
                    .All(element => element.Parent?.Name.LocalName == "testcase")) &&
                cases.All(test => test.Elements().Count(element =>
                    element.Name.LocalName is "failure" or "error" or "skipped") <= 1);
            int tests = cases.Count;
            int failures = outcomes.Count(element => element.Name.LocalName is "failure" or "error");
            int skipped = outcomes.Count(element => element.Name.LocalName == "skipped");
            bool declarationsMatch = suites.All(suite => CountersMatch(
                suite,
                suite.Elements().Where(element => element.Name.LocalName == "testcase"))) &&
                roots.Where(root => root.Name.LocalName == "testsuites").All(root => CountersMatch(
                    root,
                    root.Descendants().Where(element => element.Name.LocalName == "testcase")));
            if (tests <= 0 || tests - skipped <= 0 || failures + skipped > tests ||
                !suiteNestingIsStrict || !testcasePlacementIsStrict || !structureIsStrict || !declarationsMatch)
            {
                return Result(ApplicationVerificationState.ReportMalformed);
            }

            List<string> failureMessages = [.. outcomes
                .Where(element => element.Name.LocalName is "failure" or "error")
                .Select(element => (string?)element.Attribute("message") ?? element.Value)
                .Where(message => !string.IsNullOrWhiteSpace(message))];
            ApplicationVerificationState state = run.ExitCode == 0 && failures == 0
                ? ApplicationVerificationState.Executed
                : ApplicationVerificationState.TestsFailed;
            return new ApplicationVerificationLegResult(
                leg,
                state,
                run.Command,
                run.ExitCode,
                tests,
                tests - failures - skipped,
                failures,
                skipped,
                startedUtc,
                completedUtc,
                failureMessages,
                run.Output);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Xml.XmlException or InvalidDataException or FormatException or OverflowException)
        {
            return Result(ApplicationVerificationState.ReportMalformed, exception.Message);
        }

        ApplicationVerificationLegResult Result(ApplicationVerificationState state, string? failure = null) =>
            new(
                leg,
                state,
                run.Command,
                run.ExitCode,
                0,
                0,
                0,
                0,
                startedUtc,
                completedUtc,
                failure is null ? [] : [failure],
                run.Output);
    }

    private static int Attribute(XElement element, string name) =>
        int.Parse((string?)element.Attribute(name)
            ?? throw new InvalidDataException($"The JUnit root is missing its '{name}' counter."),
            System.Globalization.CultureInfo.InvariantCulture);

    private static bool CountersMatch(XElement element, IEnumerable<XElement> testcases)
    {
        List<XElement> cases = [.. testcases];
        List<XElement> outcomes = [.. cases.SelectMany(test => test.Elements())];
        return Attribute(element, "tests") == cases.Count &&
            Attribute(element, "failures") == outcomes.Count(outcome => outcome.Name.LocalName == "failure") &&
            Attribute(element, "errors") == outcomes.Count(outcome => outcome.Name.LocalName == "error") &&
            OptionalAttribute(element, "skipped") == outcomes.Count(outcome => outcome.Name.LocalName == "skipped");
    }

    private static int OptionalAttribute(XElement element, string name) =>
        int.Parse((string?)element.Attribute(name) ?? "0", System.Globalization.CultureInfo.InvariantCulture);
}