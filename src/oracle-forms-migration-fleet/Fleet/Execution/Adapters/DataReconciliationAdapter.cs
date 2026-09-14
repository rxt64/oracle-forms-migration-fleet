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
    private const int MaxCompared = 10_000;

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
        Dictionary<string, List<(IReadOnlyList<string> Columns, IReadOnlyList<string> Values)>> rowsByTable = new(StringComparer.Ordinal);
        List<OracleSchema> schemas = [];

        foreach (WorkspaceFile file in context.Workspace.EnumerateFiles(sourceRoot, MaxFiles))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!OracleSourceFile.IsSqlText(file.RelativePath))
            {
                continue;
            }

            try
            {
                string text = context.Workspace.ReadText(file.RelativePath, MaxTextBytes);
                schemas.Add(OracleSchemaParser.Parse(text));

                IReadOnlyList<DataMigrationStatement> statements = DataMigrationTranslator.Translate(text, out _);

                foreach (DataMigrationStatement statement in statements)
                {
                    expected[statement.Table] = expected.GetValueOrDefault(statement.Table) + 1;

                    if (DataMigrationTranslator.TryReadRow(statement.Sql, out string table, out IReadOnlyList<string> columns, out IReadOnlyList<string> values))
                    {
                        if (!rowsByTable.TryGetValue(table, out var list))
                        {
                            list = [];
                            rowsByTable[table] = list;
                        }

                        list.Add((columns, values));
                    }
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

        // Counts first: comparing values in a table that is short would bury the shortfall in detail.
        OracleSchema schema = OracleSchema.Merge(schemas);
        List<RowDifference> valueDifferences = [];
        int notComparable = 0;

        foreach (TableReconciliation row in rows.Where(row => row.Matches))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!rowsByTable.TryGetValue(row.Table, out var source) || source.Count == 0)
            {
                continue;
            }

            IReadOnlyList<string> keys = PrimaryKey(schema, row.Table);
            if (keys.Count == 0)
            {
                // The same rule AWS DMS applies: without a key there is no way to pair the rows up.
                context.Warn($"{row.Table}: no primary key was parsed, so its values were not compared.");
                continue;
            }

            string[] columns = [.. source.SelectMany(entry => entry.Columns).Distinct(StringComparer.OrdinalIgnoreCase)];

            IReadOnlyList<IReadOnlyList<string?>> target;
            try
            {
                target = await gateway.FetchAsync(row.Table, columns, MaxCompared, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                context.Warn($"{row.Table}: the target could not be read for comparison ({exception.Message}).");
                continue;
            }

            TableComparison comparison = RowComparer.Compare(row.Table, keys, source, columns, target);
            valueDifferences.AddRange(comparison.Differences);
            notComparable += comparison.NotComparable;

            if (comparison.Differences.Count > 0)
            {
                context.Warn($"{row.Table}: {comparison.Differences.Count.ToString(CultureInfo.InvariantCulture)} rows differ from the source.");
            }
        }

        if (notComparable > 0)
        {
            context.Info(
                $"{notComparable.ToString(CultureInfo.InvariantCulture)} values were expressions such as SYSDATE or a " +
                "sequence call, which differ by definition and were not compared.");
        }

        string reportPath2 = reportPath;
        context.Workspace.WriteText(reportPath2, Render(context.Request.ApplicationName, rows, valueDifferences, notComparable));

        ArtifactReference[] artifacts =
        [
            new(reportPath, ArtifactKind.ReconciliationReport, "Source row counts and values compared against the target."),
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

        if (valueDifferences.Count > 0)
        {
            return PhaseExecutionResult.Failure(
                $"Every table holds the right number of rows, but {valueDifferences.Count.ToString(CultureInfo.InvariantCulture)} " +
                "values do not match the source. No reconciliation was attested.",
                [.. valueDifferences.Take(50).Select(Describe)]);
        }

        return PhaseExecutionResult.Success(
            artifacts,
            [.. rows.Select(row => $"Reconciled: {row.Table} — {row.Expected.ToString(CultureInfo.InvariantCulture)} rows")]);
    }

    private static string Describe(RowDifference difference) => difference.Kind switch
    {
        RowDifferenceKind.MissingInTarget => $"{difference.Table} key {difference.Key}: missing from the target",
        _ => $"{difference.Table} key {difference.Key}: {difference.Column} expected '{difference.Expected}', found '{difference.Actual}'",
    };

    private static IReadOnlyList<string> PrimaryKey(OracleSchema schema, string table)
    {
        OracleTable? match = schema.Tables.FirstOrDefault(candidate =>
            string.Equals(candidate.Name, table, StringComparison.OrdinalIgnoreCase));

        if (match is null)
        {
            return [];
        }

        return [.. match.Constraints
            .Where(constraint => constraint.Kind == OracleConstraintKind.PrimaryKey)
            .SelectMany(constraint => constraint.Columns)];
    }

    private static string Render(
        string applicationName,
        IReadOnlyList<TableReconciliation> rows,
        IReadOnlyList<RowDifference> valueDifferences,
        int notComparable)
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

        if (valueDifferences.Count > 0)
        {
            builder.AppendLine("## Rows whose values differ").AppendLine();
            builder.AppendLine("| Table | Key | Column | Expected | In target |");
            builder.AppendLine("| --- | --- | --- | --- | --- |");
            foreach (RowDifference difference in valueDifferences.Take(200))
            {
                builder.Append("| ").Append(difference.Table)
                       .Append(" | ").Append(difference.Key)
                       .Append(" | ").Append(difference.Column ?? "(whole row)")
                       .Append(" | ").Append(difference.Expected ?? "—")
                       .Append(" | ").Append(difference.Kind == RowDifferenceKind.MissingInTarget ? "missing" : difference.Actual ?? "—")
                       .AppendLine(" |");
            }

            builder.AppendLine();
        }

        builder.AppendLine("Expected counts are the INSERT statements found in the supplied export. Values are compared");
        builder.AppendLine("row by row on the primary key, so a row that loaded with the wrong value is reported here.");
        builder.AppendLine("A table with no parsed primary key is counted but not compared, and this says nothing about");
        builder.AppendLine("behaviour.");

        if (notComparable > 0)
        {
            builder.AppendLine();
            builder.Append(notComparable.ToString(CultureInfo.InvariantCulture))
                   .AppendLine(" values were expressions such as SYSDATE or a sequence call. Those differ by definition");
            builder.AppendLine("and were not compared.");
        }

        return builder.ToString();
    }
}
