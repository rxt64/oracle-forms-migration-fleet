// Copyright (c) Microsoft. All rights reserved.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace OracleFormsMigrationFleet.Fleet.Execution.Adapters;

public sealed class GeneratedApplicationVerificationAdapter(
    IApplicationTestGateway? testGateway = null,
    ITargetApplicationVerificationGateway? targetGateway = null) : IPhaseAdapter
{
    private static readonly JsonSerializerOptions s_json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public MigrationPhase Phase => MigrationPhase.GeneratedApplicationVerification;

    public async Task<PhaseExecutionResult> ExecuteAsync(
        PhaseExecutionContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        string outputRoot = WorkspacePath.Normalize(context.OutputRoot);
        string reportPath = $"{outputRoot}/reports/generated-application-verification.json";
        string backend = $"{outputRoot}/application/backend";
        string frontend = $"{outputRoot}/application/frontend";
        List<ApplicationVerificationLegResult> legs = [];

        legs.Add(await ExecuteLegAsync(
            context,
            ApplicationVerificationLeg.BackendTests,
            $"{backend}/pom.xml",
            backend,
            "backend",
            testGateway is null ? null : testGateway.RunBackendTestsAsync,
            "No generated-application test gateway is configured.",
            cancellationToken).ConfigureAwait(false));
        legs.Add(await ExecuteLegAsync(
            context,
            ApplicationVerificationLeg.FrontendInteractionTests,
            $"{frontend}/package.json",
            frontend,
            "frontend",
            testGateway is null ? null : testGateway.RunFrontendTestsAsync,
            "No generated-application test gateway is configured.",
            cancellationToken).ConfigureAwait(false));
        legs.Add(await ExecuteLegAsync(
            context,
            ApplicationVerificationLeg.TargetDatabaseTests,
            $"{outputRoot}/database/postgresql/schema/schema.sql",
            $"{outputRoot}/database/postgresql/schema/schema.sql",
            "target-database",
            targetGateway is null ? null : targetGateway.VerifyAsync,
            "No target-database verification gateway is configured.",
            cancellationToken).ConfigureAwait(false));

        GeneratedApplicationVerificationReport report = new(
            "1.0",
            context.Request.ApplicationName,
            legs.All(leg => leg.Succeeded),
            legs);
        context.Workspace.WriteText(reportPath, JsonSerializer.Serialize(report, s_json));

        ArtifactReference artifact = new(
            reportPath,
            ArtifactKind.ExecutableVerificationReport,
            "Executable test results for the generated application; this does not assert Oracle runtime equivalence.");

        return report.Succeeded
            ? PhaseExecutionResult.Success([artifact])
            : new PhaseExecutionResult(
                false,
                [artifact],
                [.. legs.Where(leg => !leg.Succeeded).SelectMany(leg =>
                    leg.Failures.Count > 0
                        ? leg.Failures
                        : [$"{leg.Leg} ended in state {leg.State}."])],
                "One or more generated application verification legs did not pass.");
    }

    private static async Task<ApplicationVerificationLegResult> ExecuteLegAsync(
        PhaseExecutionContext context,
        ApplicationVerificationLeg leg,
        string requiredPath,
        string workingPath,
        string reportFolder,
        Func<string, string, CancellationToken, Task<ApplicationTestRun>>? execute,
        string unavailableReason,
        CancellationToken cancellationToken)
    {
        if (execute is null)
        {
            return Synthetic(leg, ApplicationVerificationState.ToolUnavailable, unavailableReason);
        }

        if (!context.Workspace.FileExists(requiredPath))
        {
            return Synthetic(leg, ApplicationVerificationState.SetupFailed, $"Required generated artifact '{requiredPath}' is missing.");
        }

        string reports = Path.Combine(Path.GetTempPath(), $"ofm-verification-{reportFolder}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(reports);
        try
        {
            DateTimeOffset started = DateTimeOffset.UtcNow;
            ApplicationTestRun run = await execute(
                context.Workspace.Resolve(workingPath),
                reports,
                cancellationToken).ConfigureAwait(false);
            DateTimeOffset completed = DateTimeOffset.UtcNow;
            ApplicationVerificationLegResult result = JUnitReportReader.Classify(
                leg,
                run,
                reports,
                started,
                completed);
            string output = FleetGuardrails.ContainsPotentialSecret(result.Output)
                ? "[Test output withheld because it may contain credential material.]"
                : result.Output;
            IReadOnlyList<string> failures = result.Failures.Any(FleetGuardrails.ContainsPotentialSecret)
                ? ["[Test failure details withheld because they may contain credential material.] "]
                : result.Failures;
            return result with { Output = output, Failures = failures };
        }
        finally
        {
            try
            {
                Directory.Delete(reports, recursive: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    private static ApplicationVerificationLegResult Synthetic(
        ApplicationVerificationLeg leg,
        ApplicationVerificationState state,
        string reason)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        return new(leg, state, string.Empty, -1, 0, 0, 0, 0, now, now, [reason], string.Empty);
    }
}