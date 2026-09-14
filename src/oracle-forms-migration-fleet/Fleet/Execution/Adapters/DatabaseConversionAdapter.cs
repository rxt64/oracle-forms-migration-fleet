// Copyright (c) Microsoft. All rights reserved.

using System.Globalization;
using OracleFormsMigrationFleet.Fleet.Agents;

namespace OracleFormsMigrationFleet.Fleet.Execution.Adapters;

/// <summary>
/// Converts the Oracle DDL found in the workspace source tree to the run's database target and writes the
/// DatabaseConversion artifacts the run blueprint declares.
///
/// Only <see cref="DatabaseTarget.PostgreSql"/> is implemented. Any other target fails the phase rather
/// than emitting DDL for the wrong engine. PL/SQL program units are never translated; they are reported
/// in the conversion report as manual PL/pgSQL rewrites.
///
/// An optional <see cref="IArtifactReviewer"/> reads the emitted DDL afterwards and adds advisory findings
/// to a separate artifact. That review cannot change the conversion, its report, or the phase outcome.
/// </summary>
public sealed class DatabaseConversionAdapter(
    IArtifactReviewer? reviewer = null,
    CritiqueRepairOrchestrator? orchestrator = null) : IPhaseAdapter
{
    private const int MaxFiles = 20_000;
    private const long MaxTextBytes = 8L * 1024 * 1024;

    public MigrationPhase Phase => MigrationPhase.DatabaseConversion;

    public async Task<PhaseExecutionResult> ExecuteAsync(PhaseExecutionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        DatabaseTarget target = context.Request.Target.Database;
        if (target != DatabaseTarget.PostgreSql)
        {
            return PhaseExecutionResult.Failure(
                $"No database conversion adapter is implemented for {target}. Emitting PostgreSQL DDL for a " +
                "different engine would produce invalid schema, so nothing was written. Use SSMA for the SQL Server family.");
        }

        string sourceRoot = WorkspacePath.Normalize(context.SourceRoot);
        string outputRoot = WorkspacePath.Normalize(context.OutputRoot);

        if (!context.Workspace.DirectoryExists(sourceRoot))
        {
            return PhaseExecutionResult.Failure(
                $"The source root '{sourceRoot}' does not exist in the workspace. Nothing was converted.");
        }

        List<string> sources = [];
        List<OracleSchema> schemas = [];
        List<BehaviourScenario> scenarios = [];

        foreach (WorkspaceFile file in context.Workspace.EnumerateFiles(sourceRoot, MaxFiles))
        {
            cancellationToken.ThrowIfCancellationRequested();

            string extension = Path.GetExtension(file.RelativePath);
            if (extension.Equals(".yaml", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".yml", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    if (ScenarioReader.Read(context.Workspace.ReadText(file.RelativePath, MaxTextBytes)) is { } scenario)
                    {
                        scenarios.Add(scenario);
                    }
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    context.Warn($"{file.RelativePath} could not be read and was skipped.");
                }

                continue;
            }

            if (!OracleSourceFile.IsSqlText(file.RelativePath))
            {
                continue;
            }

            try
            {
                string text = context.Workspace.ReadText(file.RelativePath, MaxTextBytes);
                sources.Add(text);
                schemas.Add(OracleSchemaParser.Parse(text));
                context.Info($"Parsed {file.RelativePath}.");
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                context.Warn($"{file.RelativePath} could not be read and was skipped.");
            }
        }

        if (schemas.Count == 0)
        {
            return PhaseExecutionResult.Failure(
                $"No '.sql' files were found under '{sourceRoot}', so there was no schema to convert.");
        }

        OracleSchema schema = OracleSchema.Merge(schemas);
        if (schema.Tables.Count == 0 && schema.Sequences.Count == 0)
        {
            return PhaseExecutionResult.Failure(
                "The supplied SQL contained no CREATE TABLE or CREATE SEQUENCE statement, so no PostgreSQL DDL was emitted.");
        }

        PostgreSqlConversion conversion = PostgreSqlEmitter.Convert(schema, string.Join('\n', sources));

        string ddlPath = $"{outputRoot}/database/postgresql/schema/schema.sql";
        string reportPath = $"{outputRoot}/database/postgresql/conversion-report.md";

        context.Workspace.WriteText(ddlPath, conversion.Ddl);
        context.Workspace.WriteText(reportPath, PostgreSqlEmitter.RenderReport(conversion.Report, context.Request.ApplicationName));

        int unsupported = conversion.Report.Findings.Count(finding => finding.Severity == ConversionSeverity.Unsupported);
        int review = conversion.Report.Findings.Count(finding => finding.Severity == ConversionSeverity.ManualReview);

        context.Info(
            $"Converted {conversion.Report.Tables.ToString(CultureInfo.InvariantCulture)} tables and " +
            $"{conversion.Report.Sequences.ToString(CultureInfo.InvariantCulture)} sequences to PostgreSQL.");

        if (unsupported > 0)
        {
            context.Warn($"{unsupported.ToString(CultureInfo.InvariantCulture)} constructs were not converted and need a manual rewrite.");
        }

        if (review > 0)
        {
            context.Warn($"{review.ToString(CultureInfo.InvariantCulture)} converted constructs need human review before use.");
        }

        List<ArtifactReference> artifacts =
        [
            Declared(context, ddlPath, ArtifactKind.DatabaseSchema, "Converted PostgreSql schema and programmable objects."),
            Declared(context, reportPath, ArtifactKind.ValidationReport, "Type mappings, unsupported constructs, and manual remediation list."),
        ];

        if (scenarios.Count > 0)
        {
            IReadOnlyList<ScenarioCoverage> coverage = ScenarioReader.Cover(scenarios, conversion.Report.Findings);
            string coveragePath = $"{outputRoot}/database/postgresql/behaviour-coverage.md";
            context.Workspace.WriteText(coveragePath, ScenarioReader.Render(context.Request.ApplicationName, coverage));

            int blocked = coverage.Count(entry => entry.BlockedBy.Count > 0);
            context.Info(
                $"Read {scenarios.Count.ToString(CultureInfo.InvariantCulture)} documented scenarios; " +
                $"{blocked.ToString(CultureInfo.InvariantCulture)} depend on logic that did not migrate.");

            foreach (ScenarioCoverage entry in coverage.Where(entry => entry.BlockedBy.Count > 0))
            {
                context.Warn(
                    $"{entry.Scenario.Name}: blocked by {entry.BlockedBy.Count.ToString(CultureInfo.InvariantCulture)} " +
                    $"untranslated program units in {entry.Scenario.LegacyPackage}.");
            }

            artifacts.Add(Declared(
                context,
                coveragePath,
                ArtifactKind.ValidationReport,
                "Documented behaviours matched against the program units this conversion refused. Nothing was executed."));
        }

        List<string> findings =
            [.. conversion.Report.Findings.Select(finding => $"{finding.Severity}: {finding.Category} — {finding.Construct}: {finding.Reason}")];

        if (reviewer is not null)
        {
            string reviewPath = $"{outputRoot}/database/postgresql/model-review.md";
            context.Info("Reviewing the generated DDL with the review model.");

            IReadOnlyList<AdvisoryFinding> advisories;
            try
            {
                advisories = await reviewer.ReviewAsync(
                    new ArtifactReviewRequest(context.Request.ApplicationName, target, conversion.Ddl, findings),
                    cancellationToken).ConfigureAwait(false);

                context.Workspace.WriteText(reviewPath, ArtifactReviewReport.Render(context.Request.ApplicationName, target, advisories));
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // The conversion already succeeded and is on disk; a failed review must not discard it.
                advisories = [];
                context.Warn($"The model review did not complete ({FailureText.Describe(exception)}). The converted schema is unaffected.");
                context.Workspace.WriteText(reviewPath, ArtifactReviewReport.RenderFailure(
                    context.Request.ApplicationName, target, exception.Message));
            }

            artifacts.Add(new ArtifactReference(
                reviewPath,
                ArtifactKind.ValidationReport,
                "Unverified model review of the generated DDL. Advisory only; it gates nothing."));

            int willFail = advisories.Count(advisory => advisory.Severity == AdvisorySeverity.WillFail);
            if (willFail > 0)
            {
                context.Warn(
                    $"The review model claims {willFail.ToString(CultureInfo.InvariantCulture)} statements will not execute on PostgreSQL. " +
                    "Unverified: confirm each against a real instance.");
            }

            // Prefixed so an advisory claim is never mistaken for a converter result in the phase findings.
            findings.AddRange(advisories.Select(advisory =>
                $"Advisory ({advisory.Severity}): {advisory.Construct} — {advisory.Reason}"));
        }

        if (orchestrator is not null)
        {
            await ProposeRepairAsync(context, conversion, target, appRootless: outputRoot, artifacts, findings, cancellationToken)
                .ConfigureAwait(false);
        }

        return PhaseExecutionResult.Success(artifacts, findings);
    }

    /// <summary>
    /// Runs the critic-and-repair exchange and writes any revision <em>beside</em> the deterministic DDL.
    /// The converter's own output is never overwritten: a model editing reviewed SQL must leave the
    /// difference visible rather than silently replacing what a human signed off.
    /// </summary>
    private async Task ProposeRepairAsync(
        PhaseExecutionContext context,
        PostgreSqlConversion conversion,
        DatabaseTarget target,
        string appRootless,
        List<ArtifactReference> artifacts,
        List<string> findings,
        CancellationToken cancellationToken)
    {
        context.Info("Running the critic and repair agents over the generated schema.");

        OrchestrationResult result = await orchestrator!.RunAsync(
            new FleetAgentRequest(context.Request.ApplicationName, target, conversion.Ddl, findings),
            step => context.Info($"  [{step.Role}] {step.Agent}: {step.Summary}"),
            cancellationToken).ConfigureAwait(false);

        context.Info($"Exchange ended: {result.Termination} after {result.Steps.Count.ToString(CultureInfo.InvariantCulture)} steps.");

        if (!result.ProducedRevision)
        {
            return;
        }

        string proposedPath = $"{appRootless}/database/postgresql/schema/schema.proposed.sql";
        context.Workspace.WriteText(proposedPath, result.ProposedArtifact!);

        artifacts.Add(new ArtifactReference(
            proposedPath,
            ArtifactKind.DatabaseSchema,
            "Agent-proposed repair of the generated DDL. Unverified, and not a replacement for schema.sql."));

        context.Warn(
            "The repair agent proposed a revised schema. It was written beside the converted DDL, not over it, " +
            "and has not been executed or reviewed.");

        findings.Add($"Proposal: a repair agent revised the schema after {result.Steps.Count.ToString(CultureInfo.InvariantCulture)} steps ({result.Termination}).");
    }

    /// <summary>Prefers the blueprint's own artifact description when the plan declares this exact path.</summary>
    private static ArtifactReference Declared(PhaseExecutionContext context, string path, ArtifactKind kind, string description) =>
        context.Plan.ExpectedOutputs.FirstOrDefault(artifact =>
            string.Equals(artifact.Path, path, StringComparison.OrdinalIgnoreCase)) ?? new ArtifactReference(path, kind, description);
}
