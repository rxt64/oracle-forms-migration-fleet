// Copyright (c) Microsoft. All rights reserved.

using System.Text.Json;

namespace OracleFormsMigrationFleet.Fleet.Execution.Adapters;

/// <summary>Builds both generated application tiers and reports the tool output without guessing success.</summary>
public sealed class BuildAndStaticValidationAdapter(IApplicationBuildGateway? gateway = null) : IPhaseAdapter
{
    public MigrationPhase Phase => MigrationPhase.BuildAndStaticValidation;

    public async Task<PhaseExecutionResult> ExecuteAsync(
        PhaseExecutionContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (gateway is null)
        {
            return PhaseExecutionResult.Failure(
                "No application build gateway is configured, so generated Java and React were not compiled.");
        }

        string appRoot = $"{WorkspacePath.Normalize(context.OutputRoot)}/application";
        string backend = $"{appRoot}/backend";
        string frontend = $"{appRoot}/frontend";
        if (!context.Workspace.FileExists($"{backend}/pom.xml") ||
            !context.Workspace.FileExists($"{frontend}/package.json"))
        {
            return PhaseExecutionResult.Failure(
                "Generated Maven and React build descriptors were not both present, so nothing was built.");
        }

        context.Info("Building the generated Spring Boot back end.");
        ApplicationBuildResult java = await gateway
            .BuildJavaAsync(context.Workspace.Resolve(backend), cancellationToken)
            .ConfigureAwait(false);
        context.Info(java.Succeeded ? "Spring Boot build passed." : "Spring Boot build failed.");

        context.Info("Building the generated React and TypeScript front end.");
        ApplicationBuildResult react = await gateway
            .BuildReactAsync(context.Workspace.Resolve(frontend), cancellationToken)
            .ConfigureAwait(false);
        context.Info(react.Succeeded ? "React and TypeScript build passed." : "React and TypeScript build failed.");

        java = RedactPotentialSecret(java);
        react = RedactPotentialSecret(react);

        string reportPath = $"{WorkspacePath.Normalize(context.OutputRoot)}/reports/build-and-static-analysis.json";
        context.Workspace.WriteText(reportPath, JsonSerializer.Serialize(
            new
            {
                application = context.Request.ApplicationName,
                succeeded = java.Succeeded && react.Succeeded,
                components = new[] { java, react },
            },
            new JsonSerializerOptions { WriteIndented = true }));

        ArtifactReference artifact = new(
            reportPath,
            ArtifactKind.ValidationReport,
            "Compiler and static-build results for the generated Java and React application.");

        if (!java.Succeeded || !react.Succeeded)
        {
            string[] failures =
            [.. new[] { java, react }
                .Where(result => !result.Succeeded)
                .Select(result => $"{result.Component}: {result.Command} exited {result.ExitCode}. {result.Output}")];

            return new PhaseExecutionResult(
                false,
                [artifact],
                failures,
                "Generated application code did not pass build and static validation. See the build report.");
        }

        return PhaseExecutionResult.Success([artifact]);
    }

    private static ApplicationBuildResult RedactPotentialSecret(ApplicationBuildResult result) =>
        FleetGuardrails.ContainsPotentialSecret(result.Output)
            ? result with { Output = "[Build output withheld because it may contain credential material.]" }
            : result;
}