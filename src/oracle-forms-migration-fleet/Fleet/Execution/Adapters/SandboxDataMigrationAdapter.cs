// Copyright (c) Microsoft. All rights reserved.

using System.Globalization;

namespace OracleFormsMigrationFleet.Fleet.Execution.Adapters;

/// <summary>
/// Loads the supplied Oracle data export into the sandbox target.
///
/// This is the first adapter that writes outside the workspace, so the gate matters more here than
/// anywhere else: the executor only calls it when the planner resolved SandboxDataMigration to
/// <see cref="PhaseStatus.Planned"/>, which needs an execution approval distinct from plan approval.
/// Without a configured gateway the phase fails rather than reporting success it did not achieve.
/// </summary>
public sealed class SandboxDataMigrationAdapter(IDataMigrationGateway? gateway = null) : IPhaseAdapter
{
    private const int MaxFiles = 20_000;
    private const long MaxTextBytes = 16L * 1024 * 1024;

    public MigrationPhase Phase => MigrationPhase.SandboxDataMigration;

    public async Task<PhaseExecutionResult> ExecuteAsync(PhaseExecutionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (gateway is null)
        {
            return PhaseExecutionResult.Failure(
                "No data migration gateway is configured, so this build cannot reach a database. The phase was " +
                "authorized but nothing was moved, and no row was claimed to have been migrated.");
        }

        if (context.Request.Target.Database != DatabaseTarget.PostgreSql)
        {
            return PhaseExecutionResult.Failure(
                $"Data migration is implemented for PostgreSQL only, not {context.Request.Target.Database}.");
        }

        string sourceRoot = WorkspacePath.Normalize(context.SourceRoot);
        string outputRoot = WorkspacePath.Normalize(context.OutputRoot);

        if (!context.Workspace.DirectoryExists(sourceRoot))
        {
            return PhaseExecutionResult.Failure($"The source root '{sourceRoot}' does not exist in the workspace.");
        }

        List<DataMigrationStatement> statements = [];
        List<string> skipped = [];

        foreach (WorkspaceFile file in context.Workspace.EnumerateFiles(sourceRoot, MaxFiles))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!Path.GetExtension(file.RelativePath).Equals(".sql", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                IReadOnlyList<DataMigrationStatement> translated = DataMigrationTranslator.Translate(
                    context.Workspace.ReadText(file.RelativePath, MaxTextBytes), out IReadOnlyList<string> ignored);

                if (translated.Count > 0)
                {
                    context.Info($"{file.RelativePath}: {translated.Count.ToString(CultureInfo.InvariantCulture)} rows to load.");
                }

                statements.AddRange(translated);
                skipped.AddRange(ignored);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                context.Warn($"{file.RelativePath} could not be read and was skipped.");
            }
        }

        if (statements.Count == 0)
        {
            return PhaseExecutionResult.Failure(
                "No INSERT statement was found in the supplied source, so there was no data to migrate.");
        }

        string[] tables = [.. statements.Select(statement => statement.Table).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];

        context.Info(
            $"Loading {statements.Count.ToString(CultureInfo.InvariantCulture)} rows into " +
            $"{tables.Length.ToString(CultureInfo.InvariantCulture)} tables.");

        DataMigrationOutcome outcome;
        try
        {
            outcome = await gateway.ApplyAsync(statements, tables, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return PhaseExecutionResult.Failure($"The data migration did not complete: {exception.Message}");
        }

        string reportPath = $"{outputRoot}/data/migration-report.md";
        context.Workspace.WriteText(reportPath, DataMigrationReport.Render(context.Request.ApplicationName, outcome, skipped));

        foreach (TableRowCount count in outcome.RowCounts)
        {
            context.Info($"{count.Table}: {count.Rows.ToString(CultureInfo.InvariantCulture)} rows in the target.");
        }

        if (outcome.StatementsFailed > 0)
        {
            context.Warn($"{outcome.StatementsFailed.ToString(CultureInfo.InvariantCulture)} statements failed. The target is partially loaded.");

            return PhaseExecutionResult.Failure(
                $"{outcome.StatementsFailed.ToString(CultureInfo.InvariantCulture)} of " +
                $"{statements.Count.ToString(CultureInfo.InvariantCulture)} statements failed, so the target holds an " +
                "incomplete copy. See the migration report.",
                [.. outcome.Failures]);
        }

        ArtifactReference[] artifacts =
        [
            new(reportPath, ArtifactKind.ReconciliationReport, "Statements executed and the row counts read back from the target."),
        ];

        return PhaseExecutionResult.Success(
            artifacts,
            [.. outcome.RowCounts.Select(count => $"Loaded: {count.Table} — {count.Rows.ToString(CultureInfo.InvariantCulture)} rows")]);
    }
}
