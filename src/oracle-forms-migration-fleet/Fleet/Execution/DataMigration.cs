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
    IReadOnlyList<TableRowCount> RowCounts)
{
    /// <summary>
    /// Rows the target already held under the same key.
    ///
    /// Counted apart from executed rows because this run did not insert them. A sandbox is loaded more
    /// than once, and a re-run that reported every existing row as a failure would bury a real one.
    /// </summary>
    public int RowsAlreadyPresent { get; init; }
}

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

    /// <summary>Reads row counts back. Reconciliation must not be able to change what it is measuring.</summary>
    Task<IReadOnlyList<TableRowCount>> CountAsync(
        IReadOnlyList<string> tables,
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
    private static readonly Regex s_standardHash = StandardHashPattern();
    private static readonly Regex s_programBody = ProgramBodyPattern();
    private static readonly Regex s_clientDirective = ClientDirectivePattern();

    /// <summary>
    /// Removes PL/SQL program bodies before any INSERT is looked for.
    ///
    /// An INSERT inside a package, procedure, or trigger is code, not data. Running one moves no row and
    /// fails on the parameters it references, so the load reports failures that were never rows to begin
    /// with. Oracle terminates these blocks with a lone slash, which is what bounds the region here.
    /// </summary>
    private static string StripProgramBodies(string script, List<string> skipped)
    {
        return s_programBody.Replace(script, match =>
        {
            string head = match.Value.Split('\n')[0].Trim();
            skipped.Add($"PL/SQL program body, not data: {(head.Length > 100 ? head[..100] : head)}");

            // Keep the line count stable so nothing downstream silently rejoins two statements.
            return string.Concat(Enumerable.Repeat("\n", match.Value.Count(character => character == '\n')));
        });
    }

    /// <summary>
    /// Removes SQL*Plus client directives, which are not statements and carry no terminator.
    ///
    /// Because they have no semicolon, the splitter joins one to the statement that follows it, and the
    /// combined chunk no longer starts with INSERT. The row is then dropped silently, which is the worst
    /// possible outcome for a data migration: a load that reports success having quietly lost a record.
    /// </summary>
    private static string StripClientDirectives(string script) =>
        s_clientDirective.Replace(script, string.Empty);

    private static string RewriteStandardHash(Match match)    {
        string value = match.Groups["value"].Value.Trim();
        string algorithm = match.Groups["algorithm"].Value.Trim().Trim('\'').ToUpperInvariant();

        // Oracle STANDARD_HASH returns RAW, and the schema converter maps RAW to bytea, so the digest has
        // to stay binary. Rendering it as hex text produces the right bytes in a type the column rejects.
        return algorithm switch
        {
            "SHA256" or "SHA-256" => $"sha256(convert_to({value}, 'UTF8'))",
            "SHA384" or "SHA-384" => $"sha384(convert_to({value}, 'UTF8'))",
            "SHA512" or "SHA-512" => $"sha512(convert_to({value}, 'UTF8'))",
            "MD5" => $"decode(md5({value}), 'hex')",
            _ => match.Value,
        };
    }

    public static IReadOnlyList<DataMigrationStatement> Translate(string oracleScript, out IReadOnlyList<string> skipped)
    {
        List<DataMigrationStatement> statements = [];
        List<string> ignored = [];

        foreach (string raw in SplitStatements(StripClientDirectives(StripProgramBodies(oracleScript ?? string.Empty, ignored))))
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
        sql = s_standardHash.Replace(sql, RewriteStandardHash);

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

    /// <summary>
    /// Splits on semicolons outside string literals, dollar-quoted bodies, and line comments.
    ///
    /// A PL/pgSQL body is full of semicolons, so splitting naively would shred every translated routine
    /// into fragments that each fail on their own.
    /// </summary>
    private static IEnumerable<string> SplitStatements(string script)
    {
        StringBuilder current = new();
        bool inString = false;
        string? dollarTag = null;

        for (int index = 0; index < script.Length; index++)
        {
            char character = script[index];

            if (dollarTag is not null)
            {
                if (character == '$' && HasTagAt(script, index, dollarTag))
                {
                    current.Append(dollarTag);
                    index += dollarTag.Length - 1;
                    dollarTag = null;
                    continue;
                }

                current.Append(character);
                continue;
            }

            if (!inString && character == '$' && TryReadTag(script, index, out string tag))
            {
                dollarTag = tag;
                current.Append(tag);
                index += tag.Length - 1;
                continue;
            }

            if (!inString && character == '-' && index + 1 < script.Length && script[index + 1] == '-')
            {
                int newline = script.IndexOf('\n', index);
                int stop = newline < 0 ? script.Length : newline;
                current.Append(script, index, stop - index);
                index = stop - 1;
                continue;
            }

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

    /// <summary>Reads a dollar-quote tag such as <c>$$</c> or <c>$legacy$</c> at <paramref name="index"/>.</summary>
    private static bool TryReadTag(string script, int index, out string tag)
    {
        tag = string.Empty;
        int cursor = index + 1;

        while (cursor < script.Length && (char.IsLetterOrDigit(script[cursor]) || script[cursor] == '_'))
        {
            // A tag may not begin with a digit, which is what keeps $1 from reading as one.
            if (cursor == index + 1 && char.IsDigit(script[cursor]))
            {
                return false;
            }

            cursor++;
        }

        if (cursor >= script.Length || script[cursor] != '$')
        {
            return false;
        }

        tag = script[index..(cursor + 1)];
        return true;
    }

    private static bool HasTagAt(string script, int index, string tag) =>
        index + tag.Length <= script.Length && string.CompareOrdinal(script, index, tag, 0, tag.Length) == 0;

    [GeneratedRegex(@"^\s*INSERT\s+INTO\s+(?<table>[A-Za-z_][\w$#]*)", RegexOptions.IgnoreCase, 2000)]
    private static partial Regex InsertPattern();

    [GeneratedRegex(@"TO_DATE\s*\(\s*(?<value>'[^']*')\s*,\s*'[^']*'\s*\)", RegexOptions.IgnoreCase, 2000)]
    private static partial Regex ToDatePattern();

    [GeneratedRegex(@"TO_TIMESTAMP\s*\(\s*(?<value>'[^']*')\s*,\s*'[^']*'\s*\)", RegexOptions.IgnoreCase, 2000)]
    private static partial Regex ToTimestampPattern();

    [GeneratedRegex(@"HEXTORAW\s*\(\s*(?<value>'[^']*')\s*\)", RegexOptions.IgnoreCase, 2000)]
    private static partial Regex HexToRawPattern();

    [GeneratedRegex(@"STANDARD_HASH\s*\(\s*(?<value>'[^']*'|[\w$#.]+)\s*,\s*(?<algorithm>'[^']*')\s*\)", RegexOptions.IgnoreCase, 2000)]
    private static partial Regex StandardHashPattern();

    [GeneratedRegex(
        @"^[ \t]*CREATE(\s+OR\s+REPLACE)?\s+(PACKAGE\s+BODY|PACKAGE|PROCEDURE|FUNCTION|TRIGGER|TYPE\s+BODY)\b.*?^[ \t]*/[ \t]*$",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Multiline,
        4000)]
    private static partial Regex ProgramBodyPattern();

    // SET is matched only with a known SQL*Plus option so that the SET clause of an UPDATE is left alone.
    [GeneratedRegex(
        @"^[ \t]*(WHENEVER|SPOOL|PROMPT|SHOW|CONNECT|REMARK|SET[ \t]+(DEFINE|ECHO|FEEDBACK|HEADING|LINESIZE|PAGESIZE|SERVEROUTPUT|TERMOUT|TRIMSPOOL|VERIFY|SQLBLANKLINES|ESCAPE|TIMING|TAB))\b[^\n]*$",
        RegexOptions.IgnoreCase | RegexOptions.Multiline,
        2000)]
    private static partial Regex ClientDirectivePattern();
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
        if (outcome.RowsAlreadyPresent > 0)
        {
            builder.AppendLine();
            builder.Append("Already present under the same key, so not inserted by this run: ")
                   .AppendLine(outcome.RowsAlreadyPresent.ToString(CultureInfo.InvariantCulture));
        }

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
