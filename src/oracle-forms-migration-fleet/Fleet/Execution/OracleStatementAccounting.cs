// Copyright (c) Microsoft. All rights reserved.

using System.Text.RegularExpressions;

namespace OracleFormsMigrationFleet.Fleet.Execution;

/// <summary>What a statement the schema parser did not recognise actually was.</summary>
public enum OracleStatementDisposition
{
    /// <summary>A program unit whose identity was read and which the PL/SQL translator reports on itself.</summary>
    ClassifiedProgramUnit,

    /// <summary>A SQL*Plus or session directive. It changes no persistent object.</summary>
    ClientDirective,

    /// <summary>Only comment text or whitespace. It declares nothing and is not a loss.</summary>
    Comment,

    /// <summary>DML, transaction control, or an anonymous block. Rows are the data phase's work, not the schema's.</summary>
    DataStatement,

    /// <summary>Privileges, comments, statistics, or a drop. Recorded, never emitted.</summary>
    Administrative,

    /// <summary>Creates or alters a persistent schema object that nothing in this conversion emits.</summary>
    OmittedSchemaBearing,

    /// <summary>Recognised as neither. Reported so it is never silently dropped.</summary>
    Unclassified,
}

public sealed record OracleStatementAccount(OracleStatementDisposition Disposition, string Kind, string Snippet);

/// <summary>
/// Accounts for every statement the Oracle schema parser left in <see cref="OracleSchema.Unparsed"/>.
///
/// The converter used to test the concatenated source for a fixed list of construct signatures, so a
/// statement it had no signature for — a CREATE TYPE, a materialized view, a database link — produced no
/// DDL, no finding, and no failure. The schema simply arrived on PostgreSQL missing an object, and the
/// report next to it read clean. Every statement is now given a disposition, and the ones that create or
/// alter a persistent object nothing emits are named individually so the run can refuse.
///
/// Pure and offline: it reads statement text only.
/// </summary>
public static partial class OracleStatementAccounting
{
    private const int SnippetLength = 160;

    /// <summary>CREATE kinds the schema emitter or the PL/SQL translator is responsible for.</summary>
    private static readonly string[] s_translatedCreateKinds =
        ["VIEW", "PACKAGE", "PACKAGE BODY", "TRIGGER", "PROCEDURE", "FUNCTION"];

    private static readonly string[] s_clientDirectives =
    [
        "SET", "SPOOL", "PROMPT", "WHENEVER", "EXIT", "QUIT", "CONNECT", "DISCONNECT", "DEFINE", "UNDEFINE",
        "COLUMN", "SHOW", "REM", "REMARK", "TTITLE", "BTITLE", "VARIABLE", "ACCEPT", "PAUSE", "HOST", "START",
        "STORE", "TIMING", "CLEAR", "ALTER SESSION", "ALTER SYSTEM",
    ];

    private static readonly string[] s_dataStatements =
    [
        "INSERT", "UPDATE", "DELETE", "MERGE", "SELECT", "WITH", "COMMIT", "ROLLBACK", "SAVEPOINT",
        "LOCK", "CALL", "EXEC", "EXECUTE", "DECLARE", "BEGIN",
    ];

    private static readonly string[] s_administrative =
    [
        "GRANT", "REVOKE", "COMMENT", "ANALYZE", "AUDIT", "NOAUDIT", "DROP", "RENAME", "TRUNCATE",
        "PURGE", "FLASHBACK", "ALTER SEQUENCE", "ALTER INDEX", "ALTER TRIGGER", "ALTER USER", "ALTER ROLE",
    ];

    public static IReadOnlyList<OracleStatementAccount> Account(OracleSchema? schema)
    {
        if (schema is null)
        {
            return [];
        }

        HashSet<string> classified = new(
            schema.ProgramUnits.Select(unit => unit.Statement),
            StringComparer.Ordinal);

        List<OracleStatementAccount> accounts = [];

        foreach (string statement in schema.Unparsed)
        {
            if (string.IsNullOrWhiteSpace(statement))
            {
                continue;
            }

            accounts.Add(classified.Contains(statement)
                ? new OracleStatementAccount(OracleStatementDisposition.ClassifiedProgramUnit, ProgramUnitKind(schema, statement), Snippet(statement))
                : Classify(statement));
        }

        return accounts;
    }

    private static string ProgramUnitKind(OracleSchema schema, string statement) =>
        schema.ProgramUnits.FirstOrDefault(unit => string.Equals(unit.Statement, statement, StringComparison.Ordinal))
            is { } unit
            ? $"CREATE {unit.Kind} {unit.Name}"
            : "program unit";

    private static OracleStatementAccount Classify(string statement)
    {
        string head = Whitespace().Replace(statement.Trim(), " ").ToUpperInvariant();

        // The schema parser strips comments before it splits, so this only fires for a statement handed
        // in some other way. It is here because an unclassified statement now fails the phase, and a
        // comment must never be the thing that fails it.
        if (Whitespace().Replace(CommentText().Replace(statement, " "), " ").Trim() is { Length: 0 })
        {
            return new OracleStatementAccount(OracleStatementDisposition.Comment, "comment", Snippet(statement));
        }

        if (CreateHead().Match(head) is { Success: true } create)
        {
            string kind = Whitespace().Replace(create.Groups["kind"].Value.Trim(), " ");

            // A CREATE TABLE, SEQUENCE, or INDEX reaching this list means the structural parser failed on
            // it. The object is neither modelled nor emitted, which is the silent loss, not an exemption.
            return s_translatedCreateKinds.Contains(kind, StringComparer.Ordinal)
                ? new OracleStatementAccount(OracleStatementDisposition.ClassifiedProgramUnit, $"CREATE {kind}", Snippet(statement))
                : new OracleStatementAccount(OracleStatementDisposition.OmittedSchemaBearing, $"CREATE {kind}", Snippet(statement));
        }

        if (Starts(head, s_clientDirectives) is { } directive)
        {
            return new OracleStatementAccount(OracleStatementDisposition.ClientDirective, directive, Snippet(statement));
        }

        if (Starts(head, s_administrative) is { } administrative)
        {
            return new OracleStatementAccount(OracleStatementDisposition.Administrative, administrative, Snippet(statement));
        }

        if (Starts(head, s_dataStatements) is { } data)
        {
            return new OracleStatementAccount(OracleStatementDisposition.DataStatement, data, Snippet(statement));
        }

        // ALTER TABLE reaches here when its clause did not resolve to a constraint on a table this run
        // parsed, so a column or constraint change would otherwise vanish between the export and the target.
        if (head.StartsWith("ALTER ", StringComparison.Ordinal))
        {
            return new OracleStatementAccount(
                OracleStatementDisposition.OmittedSchemaBearing,
                string.Join(' ', head.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(3)),
                Snippet(statement));
        }

        return new OracleStatementAccount(OracleStatementDisposition.Unclassified, FirstWord(head), Snippet(statement));
    }

    private static string? Starts(string head, string[] prefixes) => prefixes
        .Where(prefix => head.Equals(prefix, StringComparison.Ordinal)
                         || head.StartsWith(prefix + " ", StringComparison.Ordinal)
                         || head.StartsWith(prefix + ";", StringComparison.Ordinal))
        .OrderByDescending(prefix => prefix.Length)
        .FirstOrDefault();

    private static string FirstWord(string head) =>
        head.Split([' ', '\t', '(', ';'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "(empty)";

    private static string Snippet(string statement)
    {
        string collapsed = Whitespace().Replace(statement.Trim(), " ");
        return collapsed.Length <= SnippetLength ? collapsed : collapsed[..SnippetLength] + " ...";
    }

    [GeneratedRegex(@"\s+", RegexOptions.None, 2000)]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"--[^\n]*|/\*.*?\*/", RegexOptions.Singleline, 2000)]
    private static partial Regex CommentText();

    [GeneratedRegex(
        @"^CREATE\s+(?:OR\s+REPLACE\s+)?(?:(?:NON)?EDITIONABLE\s+)?(?:FORCE\s+|NO\s+FORCE\s+)?(?:PUBLIC\s+)?(?:GLOBAL\s+TEMPORARY\s+|PRIVATE\s+TEMPORARY\s+|SHARDED\s+|DUPLICATED\s+|UNIQUE\s+|BITMAP\s+|MULTIVALUE\s+)?(?<kind>PACKAGE\s+BODY|TYPE\s+BODY|MATERIALIZED\s+VIEW\s+LOG|MATERIALIZED\s+VIEW|DATABASE\s+LINK|[A-Z]+)\b",
        RegexOptions.None,
        2000)]
    private static partial Regex CreateHead();
}
