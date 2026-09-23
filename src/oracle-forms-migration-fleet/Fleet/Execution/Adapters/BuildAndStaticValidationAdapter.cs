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
                "No application build gateway is configured, so the generated back end and React were not compiled.");
        }

        string appRoot = $"{WorkspacePath.Normalize(context.OutputRoot)}/application";
        string backend = $"{appRoot}/backend";
        string frontend = $"{appRoot}/frontend";
        BackEndStack stack = context.Request.Target.BackEnd;
        string descriptor = $"{appRoot}/{GeneratedApplicationLayout.BackendDescriptor(stack)}";
        string component = GeneratedApplicationLayout.BackendComponent(stack);

        if (!context.Workspace.FileExists(descriptor) ||
            !context.Workspace.FileExists($"{appRoot}/{GeneratedApplicationLayout.FrontendDescriptor}"))
        {
            return PhaseExecutionResult.Failure(
                $"The generated {component} descriptor '{descriptor}' and the React one were not both present, " +
                "so nothing was built.");
        }

        context.Info($"Building the generated {component} back end.");
        ApplicationBuildResult server = stack == BackEndStack.AspNetCore
            ? await gateway.BuildDotNetAsync(context.Workspace.Resolve(backend), cancellationToken).ConfigureAwait(false)
            : await gateway.BuildJavaAsync(context.Workspace.Resolve(backend), cancellationToken).ConfigureAwait(false);
        context.Info(server.Succeeded ? $"{component} build passed." : $"{component} build failed.");

        context.Info("Building the generated React and TypeScript front end.");
        ApplicationBuildResult react = await gateway
            .BuildReactAsync(context.Workspace.Resolve(frontend), cancellationToken)
            .ConfigureAwait(false);
        context.Info(react.Succeeded ? "React and TypeScript build passed." : "React and TypeScript build failed.");

        server = RedactPotentialSecret(server);
        react = RedactPotentialSecret(react);

        string reportPath = $"{WorkspacePath.Normalize(context.OutputRoot)}/reports/build-and-static-analysis.json";
        context.Workspace.WriteText(reportPath, JsonSerializer.Serialize(
            new
            {
                application = context.Request.ApplicationName,
                backEnd = stack.ToString(),
                succeeded = server.Succeeded && react.Succeeded,
                components = new[] { server, react },
            },
            new JsonSerializerOptions { WriteIndented = true }));

        ArtifactReference artifact = new(
            reportPath,
            ArtifactKind.ValidationReport,
            $"Compiler and static-build results for the generated {component} and React application.");

        if (!server.Succeeded || !react.Succeeded)
        {
            string[] failures =
            [.. new[] { server, react }
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