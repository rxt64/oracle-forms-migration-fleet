// Copyright (c) Microsoft. All rights reserved.

using System.Globalization;

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
public sealed class DatabaseConversionAdapter(IArtifactReviewer? reviewer = null) : IPhaseAdapter
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

        foreach (WorkspaceFile file in context.Workspace.EnumerateFiles(sourceRoot, MaxFiles))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!Path.GetExtension(file.RelativePath).Equals(".sql", StringComparison.OrdinalIgnoreCase))
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

        List<string> findings =
            [.. conversion.Report.Findings.Select(finding => $"{finding.Severity}: {finding.Category} — {finding.Construct}: {finding.Reason}")];

        if (reviewer is not null)
        {
            string reviewPath = $"{outputRoot}/database/postgresql/model-review.md";
            IReadOnlyList<AdvisoryFinding> advisories = await ReviewAsync(context, conversion, findings, cancellationToken)
                .ConfigureAwait(false);

            context.Workspace.WriteText(reviewPath, ArtifactReviewReport.Render(context.Request.ApplicationName, target, advisories));
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

        return PhaseExecutionResult.Success(artifacts, findings);
    }

    private async Task<IReadOnlyList<AdvisoryFinding>> ReviewAsync(
        PhaseExecutionContext context,
        PostgreSqlConversion conversion,
        IReadOnlyList<string> deterministicFindings,
        CancellationToken cancellationToken)
    {
        context.Info("Reviewing the generated DDL with the review model.");
        try
        {
            return await reviewer!.ReviewAsync(
                new ArtifactReviewRequest(
                    context.Request.ApplicationName,
                    DatabaseTarget.PostgreSql,
                    conversion.Ddl,
                    deterministicFindings),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The conversion already succeeded and is on disk; a failed review must not discard it.
            context.Warn($"The model review did not complete ({exception.GetType().Name}). The converted schema is unaffected.");
            return [];
        }
    }

    /// <summary>Prefers the blueprint's own artifact description when the plan declares this exact path.</summary>
    private static ArtifactReference Declared(PhaseExecutionContext context, string path, ArtifactKind kind, string description) =>
        context.Plan.ExpectedOutputs.FirstOrDefault(artifact =>
            string.Equals(artifact.Path, path, StringComparison.OrdinalIgnoreCase)) ?? new ArtifactReference(path, kind, description);
}
