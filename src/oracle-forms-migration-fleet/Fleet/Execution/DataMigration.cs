// Copyright (c) Microsoft. All rights reserved.

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace OracleFormsMigrationFleet.Fleet.Execution;

public sealed record DataMigrationStatement(string Table, string Sql);

public sealed record TableRowCount(string Table, long Rows);

/// <summary>Outcome of moving rows into the target. Counts are read back from the target, not assumed.</summary>
public sealed record DataMigrationOutcome(
    int StatementsExecuted,
    int StatementsFailed,
    IReadOnlyList<string> Failures,
    IReadOnlyList<TableRowCount> RowCounts);

/// <summary>
/// Outcome of applying the converted schema to the sandbox.
///
/// <c>AlreadyPresent</c> is counted separately from <c>Applied</c> so a re-run reads as idempotent rather
/// than as a success it did not earn: an object that already existed was not created by this run.
/// </summary>
public sealed record SchemaDeploymentOutcome(
    int Applied,
    int AlreadyPresent,
    IReadOnlyList<string> Failures);

/// <summary>
/// Writes rows into the sandbox target.
///
/// Adapters never hold a connection string. The host supplies this, already bound to the configured
/// target, so a request from the browser can ask for a migration but can never say where it lands —
/// which keeps an operator, or a prompt-injected model, from pointing a data copy at a host of its choice.
/// </summary>
public interface IDataMigrationGateway
{
    /// <summary>Applies the DDL this fleet generated. Never source DDL, which is not vetted.</summary>
    Task<SchemaDeploymentOutcome> PrepareAsync(
        IReadOnlyList<string> statements,
        CancellationToken cancellationToken);

    Task<DataMigrationOutcome> ApplyAsync(
        IReadOnlyList<DataMigrationStatement> statements,
        IReadOnlyList<string> tables,
        CancellationToken cancellationToken);
}

/// <summary>
/// Rewrites the Oracle INSERT statements in a supplied data export into PostgreSQL.
///
/// Only INSERT is translated. Anything else in the export is left alone and reported, because a data
/// migration that quietly ran a DDL or DML statement it did not understand would be worse than one that
/// refused. Values are passed through verbatim; this converts syntax, never contents.
/// </summary>
public static partial class DataMigrationTranslator
{
    private static readonly Regex s_insert = InsertPattern();
    private static readonly Regex s_toDate = ToDatePattern();
    private static readonly Regex s_toTimestamp = ToTimestampPattern();
    private static readonly Regex s_hexToRaw = HexToRawPattern();

    public static IReadOnlyList<DataMigrationStatement> Translate(string oracleScript, out IReadOnlyList<string> skipped)
    {
        List<DataMigrationStatement> statements = [];
        List<string> ignored = [];

        foreach (string raw in SplitStatements(oracleScript ?? string.Empty))
        {
            string statement = raw.Trim();
            if (statement.Length == 0)
            {
                continue;
            }

            Match match = s_insert.Match(statement);
            if (!match.Success)
            {
                string head = statement.Split('\n')[0].Trim();
                ignored.Add(head.Length > 120 ? head[..120] : head);
                continue;
            }

            statements.Add(new DataMigrationStatement(
                match.Groups["table"].Value.ToLowerInvariant(),
                Rewrite(statement)));
        }

        skipped = ignored;
        return statements;
    }

    private static string Rewrite(string statement)
    {
        string sql = statement.TrimEnd(';', ' ', '\r', '\n');

        sql = s_toTimestamp.Replace(sql, match => $"TIMESTAMP {match.Groups["value"].Value}");
        sql = s_toDate.Replace(sql, match => $"DATE {match.Groups["value"].Value}");
        sql = s_hexToRaw.Replace(sql, match => $"decode({match.Groups["value"].Value}, 'hex')");

        sql = Regex.Replace(sql, @"\bSYSTIMESTAMP\b", "now()", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(2));
        sql = Regex.Replace(sql, @"\bSYSDATE\b", "CURRENT_DATE", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(2));
        sql = Regex.Replace(sql, @"(\w+)\.NEXTVAL", match => $"nextval('{match.Groups[1].Value.ToLowerInvariant()}')",
            RegexOptions.IgnoreCase, TimeSpan.FromSeconds(2));

        return sql;
    }

    /// <summary>Splits on semicolons outside string literals, so a value containing one is not cut in half.</summary>
    /// <summary>
    /// Splits generated DDL into executable statements.
    ///
    /// This is only ever applied to schema this fleet produced. Source DDL stays untranslated and unrun.
    /// </summary>
    public static IReadOnlyList<string> SplitSchema(string script)
    {
        ArgumentNullException.ThrowIfNull(script);

        return [.. SplitStatements(script)
            .Select(statement => statement.Trim())
            .Where(statement => statement.Length > 0 && !IsOnlyComments(statement))];
    }

    private static bool IsOnlyComments(string statement) =>
        statement
            .Split('\n')
            .Select(line => line.Trim())
            .All(line => line.Length == 0 || line.StartsWith("--", StringComparison.Ordinal));

    private static IEnumerable<string> SplitStatements(string script)
    {
        StringBuilder current = new();
        bool inString = false;

        for (int index = 0; index < script.Length; index++)
        {
            char character = script[index];

            if (character == '\'')
            {
                inString = !inString;
                current.Append(character);
                continue;
            }

            if (character == ';' && !inString)
            {
                yield return current.ToString();
                current.Clear();
                continue;
            }

            current.Append(character);
        }

        if (current.Length > 0)
        {
            yield return current.ToString();
        }
    }

    [GeneratedRegex(@"^\s*INSERT\s+INTO\s+(?<table>[A-Za-z_][\w$#]*)", RegexOptions.IgnoreCase, 2000)]
    private static partial Regex InsertPattern();

    [GeneratedRegex(@"TO_DATE\s*\(\s*(?<value>'[^']*')\s*,\s*'[^']*'\s*\)", RegexOptions.IgnoreCase, 2000)]
    private static partial Regex ToDatePattern();

    [GeneratedRegex(@"TO_TIMESTAMP\s*\(\s*(?<value>'[^']*')\s*,\s*'[^']*'\s*\)", RegexOptions.IgnoreCase, 2000)]
    private static partial Regex ToTimestampPattern();

    [GeneratedRegex(@"HEXTORAW\s*\(\s*(?<value>'[^']*')\s*\)", RegexOptions.IgnoreCase, 2000)]
    private static partial Regex HexToRawPattern();
}

/// <summary>Renders the reconciliation a data migration produced.</summary>
public static class DataMigrationReport
{
    public static string Render(string applicationName, DataMigrationOutcome outcome, IReadOnlyList<string> skipped)
    {
        ArgumentNullException.ThrowIfNull(outcome);

        StringBuilder builder = new();
        builder.AppendLine("# Data migration").AppendLine();
        builder.Append("Application: ").AppendLine(applicationName);
        builder.AppendLine();
        builder.Append("Statements executed: ").Append(outcome.StatementsExecuted.ToString(CultureInfo.InvariantCulture));
        builder.Append(", failed: ").AppendLine(outcome.StatementsFailed.ToString(CultureInfo.InvariantCulture));
        builder.AppendLine();

        builder.AppendLine("## Rows in the target after the run").AppendLine();
        builder.AppendLine("| Table | Rows |");
        builder.AppendLine("| --- | --- |");
        foreach (TableRowCount count in outcome.RowCounts)
        {
            builder.Append("| ").Append(count.Table).Append(" | ")
                   .Append(count.Rows.ToString(CultureInfo.InvariantCulture)).AppendLine(" |");
        }

        builder.AppendLine();
        builder.AppendLine("These counts were read back from the target after loading. They are not a reconciliation:");
        builder.AppendLine("nothing here compares them against the source, so a row that silently failed to load shows only");
        builder.AppendLine("as a smaller number than you expected.").AppendLine();

        if (outcome.Failures.Count > 0)
        {
            builder.AppendLine("## Statements that failed").AppendLine();
            foreach (string failure in outcome.Failures)
            {
                builder.Append("- ").AppendLine(failure);
            }

            builder.AppendLine();
        }

        if (skipped.Count > 0)
        {
            builder.AppendLine("## Not executed").AppendLine();
            builder.AppendLine("Only INSERT statements are translated. These were left alone rather than run against the target:").AppendLine();
            foreach (string statement in skipped.Take(40))
            {
                builder.Append("- `").Append(statement).AppendLine("`");
            }
        }

        return builder.ToString();
    }
}
