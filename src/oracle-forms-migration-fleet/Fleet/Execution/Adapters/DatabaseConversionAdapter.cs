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
/// </summary>
public sealed class DatabaseConversionAdapter : IPhaseAdapter
{
    private const int MaxFiles = 20_000;
    private const long MaxTextBytes = 8L * 1024 * 1024;

    public MigrationPhase Phase => MigrationPhase.DatabaseConversion;

    public Task<PhaseExecutionResult> ExecuteAsync(PhaseExecutionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        DatabaseTarget target = context.Request.Target.Database;
        if (target != DatabaseTarget.PostgreSql)
        {
            return Task.FromResult(PhaseExecutionResult.Failure(
                $"No database conversion adapter is implemented for {target}. Emitting PostgreSQL DDL for a " +
                "different engine would produce invalid schema, so nothing was written. Use SSMA for the SQL Server family."));
        }

        string sourceRoot = WorkspacePath.Normalize(context.SourceRoot);
        string outputRoot = WorkspacePath.Normalize(context.OutputRoot);

        if (!context.Workspace.DirectoryExists(sourceRoot))
        {
            return Task.FromResult(PhaseExecutionResult.Failure(
                $"The source root '{sourceRoot}' does not exist in the workspace. Nothing was converted."));
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
            return Task.FromResult(PhaseExecutionResult.Failure(
                $"No '.sql' files were found under '{sourceRoot}', so there was no schema to convert."));
        }

        OracleSchema schema = OracleSchema.Merge(schemas);
        if (schema.Tables.Count == 0 && schema.Sequences.Count == 0)
        {
            return Task.FromResult(PhaseExecutionResult.Failure(
                "The supplied SQL contained no CREATE TABLE or CREATE SEQUENCE statement, so no PostgreSQL DDL was emitted."));
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

        ArtifactReference[] artifacts =
        [
            Declared(context, ddlPath, ArtifactKind.DatabaseSchema, "Converted PostgreSql schema and programmable objects."),
            Declared(context, reportPath, ArtifactKind.ValidationReport, "Type mappings, unsupported constructs, and manual remediation list."),
        ];

        return Task.FromResult(PhaseExecutionResult.Success(
            artifacts,
            [.. conversion.Report.Findings.Select(finding => $"{finding.Severity}: {finding.Category} — {finding.Construct}: {finding.Reason}")]));
    }

    /// <summary>Prefers the blueprint's own artifact description when the plan declares this exact path.</summary>
    private static ArtifactReference Declared(PhaseExecutionContext context, string path, ArtifactKind kind, string description) =>
        context.Plan.ExpectedOutputs.FirstOrDefault(artifact =>
            string.Equals(artifact.Path, path, StringComparison.OrdinalIgnoreCase)) ?? new ArtifactReference(path, kind, description);
}
