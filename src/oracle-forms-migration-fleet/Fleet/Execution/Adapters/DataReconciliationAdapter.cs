// Copyright (c) Microsoft. All rights reserved.

using System.Globalization;
using System.Text;

namespace OracleFormsMigrationFleet.Fleet.Execution.Adapters;

public sealed record TableReconciliation(string Table, long Expected, long Actual)
{
    public bool Matches => Expected == Actual;
}

/// <summary>
/// Compares what the supplied export said should be there with what the target actually holds.
///
/// Row counts read back after a load are not a reconciliation: they say what arrived, not what was meant
/// to. This is the first phase that can fail because the target is wrong rather than because a statement
/// errored, which is exactly the case a load reports as success when rows were rejected earlier.
/// </summary>
public sealed class DataReconciliationAdapter(IDataMigrationGateway? gateway = null) : IPhaseAdapter
{
    private const int MaxFiles = 20_000;
    private const long MaxTextBytes = 16L * 1024 * 1024;

    public MigrationPhase Phase => MigrationPhase.DataReconciliation;

    public async Task<PhaseExecutionResult> ExecuteAsync(PhaseExecutionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (gateway is null)
        {
            return PhaseExecutionResult.Failure(
                "No data migration gateway is configured, so this build cannot read the target. Nothing was " +
                "reconciled and nothing was attested.");
        }

        string sourceRoot = WorkspacePath.Normalize(context.SourceRoot);
        string outputRoot = WorkspacePath.Normalize(context.OutputRoot);

        if (!context.Workspace.DirectoryExists(sourceRoot))
        {
            return PhaseExecutionResult.Failure($"The source root '{sourceRoot}' does not exist in the workspace.");
        }

        Dictionary<string, long> expected = new(StringComparer.Ordinal);

        foreach (WorkspaceFile file in context.Workspace.EnumerateFiles(sourceRoot, MaxFiles))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!OracleSourceFile.IsSqlText(file.RelativePath))
            {
                continue;
            }

            try
            {
                IReadOnlyList<DataMigrationStatement> statements = DataMigrationTranslator.Translate(
                    context.Workspace.ReadText(file.RelativePath, MaxTextBytes), out _);

                foreach (DataMigrationStatement statement in statements)
                {
                    expected[statement.Table] = expected.GetValueOrDefault(statement.Table) + 1;
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                context.Warn($"{file.RelativePath} could not be read and was skipped.");
            }
        }

        if (expected.Count == 0)
        {
            return PhaseExecutionResult.Failure(
                "No INSERT statement was found in the supplied source, so there was nothing to reconcile against.");
        }

        string[] tables = [.. expected.Keys.Order(StringComparer.Ordinal)];

        IReadOnlyList<TableRowCount> actual;
        try
        {
            actual = await gateway.CountAsync(tables, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return PhaseExecutionResult.Failure($"The target could not be read: {exception.Message}");
        }

        List<TableReconciliation> rows =
            [.. actual.Select(count => new TableReconciliation(count.Table, expected[count.Table], count.Rows))];

        string reportPath = $"{outputRoot}/data/reconciliation.md";
        context.Workspace.WriteText(reportPath, Render(context.Request.ApplicationName, rows));

        foreach (TableReconciliation row in rows)
        {
            string message =
                $"{row.Table}: expected {row.Expected.ToString(CultureInfo.InvariantCulture)}, " +
                $"target holds {(row.Actual < 0 ? "no such table" : row.Actual.ToString(CultureInfo.InvariantCulture))}.";

            if (row.Matches)
            {
                context.Info(message);
            }
            else
            {
                context.Warn(message);
            }
        }

        IReadOnlyList<TableReconciliation> differences = [.. rows.Where(row => !row.Matches)];

        ArtifactReference[] artifacts =
        [
            new(reportPath, ArtifactKind.ReconciliationReport, "Source row counts compared against the target."),
        ];

        if (differences.Count > 0)
        {
            return PhaseExecutionResult.Failure(
                $"{differences.Count.ToString(CultureInfo.InvariantCulture)} of " +
                $"{rows.Count.ToString(CultureInfo.InvariantCulture)} tables do not match the source. The target is " +
                "not a faithful copy, so no reconciliation was attested.",
                [.. differences.Select(row =>
                    $"{row.Table}: expected {row.Expected.ToString(CultureInfo.InvariantCulture)}, " +
                    $"found {row.Actual.ToString(CultureInfo.InvariantCulture)}")]);
        }

        return PhaseExecutionResult.Success(
            artifacts,
            [.. rows.Select(row => $"Reconciled: {row.Table} — {row.Expected.ToString(CultureInfo.InvariantCulture)} rows")]);
    }

    private static string Render(string applicationName, IReadOnlyList<TableReconciliation> rows)
    {
        StringBuilder builder = new();
        builder.AppendLine("# Data reconciliation").AppendLine();
        builder.Append("Application: ").AppendLine(applicationName);
        builder.AppendLine();

        int matched = rows.Count(row => row.Matches);
        builder.Append(matched.ToString(CultureInfo.InvariantCulture)).Append(" of ")
               .Append(rows.Count.ToString(CultureInfo.InvariantCulture)).AppendLine(" tables match the source.");
        builder.AppendLine();

        builder.AppendLine("| Table | Expected | In target | Match |");
        builder.AppendLine("| --- | --- | --- | --- |");
        foreach (TableReconciliation row in rows)
        {
            builder.Append("| ").Append(row.Table)
                   .Append(" | ").Append(row.Expected.ToString(CultureInfo.InvariantCulture))
                   .Append(" | ").Append(row.Actual < 0 ? "no such table" : row.Actual.ToString(CultureInfo.InvariantCulture))
                   .Append(" | ").Append(row.Matches ? "yes" : "**no**")
                   .AppendLine(" |");
        }

        builder.AppendLine();
        builder.AppendLine("Expected counts are the INSERT statements found in the supplied export. This compares how");
        builder.AppendLine("many rows arrived, not what is in them: it would not detect a row that loaded with the");
        builder.AppendLine("wrong values, and it says nothing about behaviour.");

        return builder.ToString();
    }
}
