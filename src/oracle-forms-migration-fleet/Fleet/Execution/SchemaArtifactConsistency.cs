// Copyright (c) Microsoft. All rights reserved.

using System.Text.Json;

namespace OracleFormsMigrationFleet.Fleet.Execution;

/// <summary>What a schema artifact claims about its own size, read out of its <c>coverage</c> block.</summary>
public readonly record struct SchemaArtifactCoverage(
    int Objects,
    int Tables,
    int Columns,
    int Constraints,
    int Sequences,
    int Indexes,
    int Grants,
    int ProgramUnits);

/// <summary>
/// Reconciles what an Oracle schema artifact SAYS it contains against the canonical statements it
/// actually carries.
///
/// The artifact is untrusted input with a verified digest, and a digest only proves the gateway meant to
/// send these bytes. Before this existed the host believed the <c>coverage</c> counts and a coarse look
/// for a program-unit boundary, so an artifact that hashed correctly, named the allowed schemas, and
/// reported "4 tables" while shipping three — or shipping a fourth table nothing in its own inventory
/// names — became a trusted prepared schema. Everything downstream (the provenance record, the trust
/// store claim, the conversion phase) then describes an estate the source never reported.
///
/// So the DDL is parsed, not scanned. The worker's emitter writes a closed set of statement shapes with
/// every identifier double-quoted, so this reader accepts exactly those shapes and refuses anything
/// else rather than guessing. It is a real reader for the same reason the rest of this file is: a regex
/// counting the word CREATE would count one inside a check condition, a PL/SQL comment, or a quoted
/// literal, which is precisely the spoof it would need to catch.
///
/// Two asymmetries are deliberate and are NOT laxity:
/// <list type="bullet">
/// <item>Oracle stores a NOT NULL column as a check constraint and the emitter refuses to write it twice,
/// so emitted constraint statements are bounded ABOVE by <c>coverage.constraints</c>, not equal to it.</item>
/// <item>An index that backs a primary or unique key carries no CREATE INDEX statement, so emitted index
/// statements are likewise bounded above by <c>coverage.indexes</c>. Every index that IS written must
/// still be named by the inventory.</item>
/// </list>
/// Everything else reconciles exactly and in both directions.
/// </summary>
public static class SchemaArtifactConsistency
{
    /// <summary>Object kinds the worker lists in <c>objects</c> for a program unit.</summary>
    public static readonly string[] ProgramUnitKinds =
        ["PACKAGE BODY", "TYPE BODY", "FUNCTION", "PACKAGE", "PROCEDURE", "TYPE"];

    /// <summary>Object privileges the worker writes back as a GRANT. Repeated by value, not shared.</summary>
    public static readonly string[] ObjectPrivileges =
        ["ALTER", "DEBUG", "DELETE", "EXECUTE", "INDEX", "INSERT", "READ", "REFERENCES", "SELECT", "UPDATE", "WRITE"];

    /// <summary>Null when the artifact describes itself exactly; otherwise the refusal to report.</summary>
    public static string? Reconcile(
        JsonElement root,
        IReadOnlyList<string> schemas,
        SchemaArtifactCoverage coverage,
        string schemaDdl,
        string? programUnitSql)
    {
        ArgumentNullException.ThrowIfNull(schemas);
        ArgumentNullException.ThrowIfNull(schemaDdl);

        if (ReadInventory(root, schemas, out Inventory inventory) is { } inventoryError)
        {
            return inventoryError;
        }

        if (inventory.Total != coverage.Tables + coverage.Indexes + coverage.Sequences + coverage.ProgramUnits ||
            coverage.Objects < inventory.Total ||
            inventory.Tables.Count != coverage.Tables ||
            inventory.Indexes.Count != coverage.Indexes ||
            inventory.Sequences.Count != coverage.Sequences ||
            inventory.ProgramUnits.Count != coverage.ProgramUnits)
        {
            return
                "The inlined schema artifact lists an inventory that does not add up to the coverage it reports, so " +
                "the two halves of the same document disagree and nothing was stored.";
        }

        if (ReadSchemaSection(schemaDdl, schemas, out Section section) is { } sectionError)
        {
            return sectionError;
        }

        if (ReadProgramUnits(programUnitSql, schemas, out HashSet<Declared> units) is { } unitError)
        {
            return unitError;
        }

        if (!section.Tables.SetEquals(inventory.Tables) || section.Columns != coverage.Columns)
        {
            return
                "The inlined schema artifact writes CREATE TABLE statements its own inventory and column count do not " +
                "describe, so the DDL and the coverage are not the same schema and nothing was stored.";
        }

        if (!section.Sequences.SetEquals(inventory.Sequences))
        {
            return
                "The inlined schema artifact writes CREATE SEQUENCE statements its own inventory does not name, or " +
                "omits one it does, and nothing was stored.";
        }

        if (!units.SetEquals(inventory.ProgramUnits))
        {
            return
                "The inlined schema artifact's program units are not the ones its own inventory names, so the PL/SQL " +
                "it carries was not adjudicated against anything and nothing was stored.";
        }

        // An index that backs a key is real but carries no statement, so this direction is one-way: every
        // index that IS written must be in the inventory, and no more may be written than it declares.
        if (section.Indexes.Count > coverage.Indexes || !section.Indexes.IsSubsetOf(inventory.Indexes))
        {
            return
                "The inlined schema artifact writes a CREATE INDEX its own inventory does not name, or writes more " +
                "indexes than it declares, and nothing was stored.";
        }

        // A NOT NULL column is stored as a check constraint the emitter deliberately never repeats, so the
        // same one-way bound applies. What must hold exactly is that every constraint lands on a table the
        // inventory names and that no constraint name is written twice.
        if (section.Constraints.Count > coverage.Constraints)
        {
            return
                "The inlined schema artifact writes more constraints than its own coverage reports, so nothing was stored.";
        }

        if (section.Grants != coverage.Grants)
        {
            return
                "The inlined schema artifact writes a different number of grants than its own coverage reports, so " +
                "nothing was stored.";
        }

        foreach (Declared target in section.ConstraintTargets)
        {
            if (!inventory.Tables.Contains(target))
            {
                return
                    "The inlined schema artifact constrains a table its own inventory does not name, so the statement " +
                    "describes an object nobody reported and nothing was stored.";
            }
        }

        foreach (Declared target in section.IndexTargets)
        {
            if (!inventory.Tables.Contains(target))
            {
                return
                    "The inlined schema artifact indexes a table its own inventory does not name, so nothing was stored.";
            }
        }

        foreach (Declared target in section.GrantTargets)
        {
            if (!inventory.Grantable.Contains(target))
            {
                return
                    "The inlined schema artifact grants a privilege on an object its own inventory does not name, so " +
                    "the access control it reports was never reconciled and nothing was stored.";
            }
        }

        return null;
    }

    /// <summary>One object as either half of the document names it. Owner and name are case-sensitive.</summary>
    private readonly record struct Declared(string Schema, string Kind, string Name);

    private sealed class Inventory
    {
        public HashSet<Declared> Tables { get; } = [];

        public HashSet<Declared> Indexes { get; } = [];

        public HashSet<Declared> Sequences { get; } = [];

        public HashSet<Declared> ProgramUnits { get; } = [];

        /// <summary>Objects a GRANT may target: everything except an index.</summary>
        public HashSet<Declared> Grantable { get; } = [];

        public int Total { get; set; }
    }

    private sealed class Section
    {
        public HashSet<Declared> Tables { get; } = [];

        public HashSet<Declared> Sequences { get; } = [];

        public HashSet<Declared> Indexes { get; } = [];

        public HashSet<Declared> Constraints { get; } = [];

        public List<Declared> ConstraintTargets { get; } = [];

        public List<Declared> IndexTargets { get; } = [];

        public List<Declared> GrantTargets { get; } = [];

        public int Columns { get; set; }

        public int Grants { get; set; }
    }

    private static string? ReadInventory(JsonElement root, IReadOnlyList<string> schemas, out Inventory inventory)
    {
        inventory = new Inventory();

        if (!root.TryGetProperty("objects", out JsonElement objects) || objects.ValueKind != JsonValueKind.Array)
        {
            return "The inlined schema artifact reports no object inventory, so its DDL could not be reconciled against anything.";
        }

        if (objects.GetArrayLength() > SourceGatewayProtocol.MaxSchemaObjects)
        {
            return $"The inlined schema artifact lists more than {SourceGatewayProtocol.MaxSchemaObjects} objects.";
        }

        HashSet<Declared> seen = [];
        foreach (JsonElement entry in objects.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object ||
                Text(entry, "schema") is not { } schema ||
                Text(entry, "kind") is not { } kind ||
                Text(entry, "name") is not { Length: > 0 } name ||
                name.Length > SourceGatewayProtocol.MaxSchemaIdentifierCharacters ||
                name.Any(char.IsControl))
            {
                return "The inlined schema artifact lists an object this build cannot read.";
            }

            if (!schemas.Contains(schema, StringComparer.Ordinal))
            {
                return
                    "The inlined schema artifact lists an object owned by a schema the request did not allow, so it " +
                    "was refused rather than stored.";
            }

            Declared declared = new(schema, kind, name);
            if (!seen.Add(declared))
            {
                return "The inlined schema artifact lists the same object twice, so its inventory counts nothing reliably.";
            }

            switch (kind)
            {
                case "TABLE":
                    inventory.Tables.Add(declared);
                    inventory.Grantable.Add(declared with { Kind = string.Empty });
                    break;
                case "INDEX":
                    inventory.Indexes.Add(declared);
                    break;
                case "SEQUENCE":
                    inventory.Sequences.Add(declared);
                    inventory.Grantable.Add(declared with { Kind = string.Empty });
                    break;
                default:
                    if (!ProgramUnitKinds.Contains(kind, StringComparer.Ordinal))
                    {
                        return "The inlined schema artifact lists an object kind this build does not extract.";
                    }

                    inventory.ProgramUnits.Add(declared);
                    inventory.Grantable.Add(declared with { Kind = string.Empty });
                    break;
            }
        }

        inventory.Total = seen.Count;
        return null;
    }

    // -----------------------------------------------------------------------------------------------
    // The schema half: a closed set of statement shapes, each fully read or the whole document refused.
    // -----------------------------------------------------------------------------------------------

    private static string? ReadSchemaSection(string ddl, IReadOnlyList<string> schemas, out Section section)
    {
        section = new Section();
        int start = 0;
        int index = 0;
        int depth = 0;
        int statements = 0;

        while (index < ddl.Length)
        {
            if (!TrySkipOpaque(ddl, ref index, out bool skipped))
            {
                return "The inlined schema artifact ends inside a literal or comment, so its DDL is truncated and nothing was stored.";
            }

            if (skipped)
            {
                continue;
            }

            char current = ddl[index];
            if (current == '(')
            {
                depth++;
            }
            else if (current == ')' && depth > 0)
            {
                depth--;
            }
            else if (current == ';' && depth == 0)
            {
                if (++statements > SourceGatewayProtocol.MaxSchemaStatements)
                {
                    return $"The inlined schema artifact carries more than {SourceGatewayProtocol.MaxSchemaStatements} statements.";
                }

                if (Classify(ddl[start..index], schemas, section) is { } error)
                {
                    return error;
                }

                start = ++index;
                continue;
            }

            index++;
        }

        return ddl.AsSpan(start).Trim().Length > 0
            ? "The inlined schema artifact ends with an unterminated statement, so its DDL is truncated and nothing was stored."
            : null;
    }

    private static string? Classify(string statement, IReadOnlyList<string> schemas, Section section)
    {
        const string Unreadable =
            "The inlined schema artifact carries a statement that is not one this build's own emitter writes, so it " +
            "could not be reconciled with the artifact's inventory and nothing was stored.";

        int index = 0;
        if (TryWord(statement, ref index, "CREATE"))
        {
            int afterCreate = index;
            if (TryWord(statement, ref index, "SEQUENCE"))
            {
                if (!TryQualified(statement, ref index, out Declared sequence) ||
                    !schemas.Contains(sequence.Schema, StringComparer.Ordinal))
                {
                    return Unreadable;
                }

                return section.Sequences.Add(sequence with { Kind = "SEQUENCE" }) ? null : Duplicate;
            }

            index = afterCreate;
            if (TryWord(statement, ref index, "TABLE"))
            {
                if (!TryQualified(statement, ref index, out Declared table) ||
                    !schemas.Contains(table.Schema, StringComparer.Ordinal) ||
                    !TryColumnList(statement, ref index, out int columns) ||
                    columns == 0 ||
                    !AtEnd(statement, index))
                {
                    return Unreadable;
                }

                if (!section.Tables.Add(table with { Kind = "TABLE" }))
                {
                    return Duplicate;
                }

                section.Columns += columns;
                return null;
            }

            index = afterCreate;
            TryWord(statement, ref index, "UNIQUE");
            if (TryWord(statement, ref index, "INDEX"))
            {
                if (!TryQualified(statement, ref index, out Declared indexName) ||
                    !schemas.Contains(indexName.Schema, StringComparer.Ordinal) ||
                    !TryWord(statement, ref index, "ON") ||
                    !TryQualified(statement, ref index, out Declared target) ||
                    !TryColumnList(statement, ref index, out int columns) ||
                    columns == 0 ||
                    !AtEnd(statement, index))
                {
                    return Unreadable;
                }

                if (!section.Indexes.Add(indexName with { Kind = "INDEX" }))
                {
                    return Duplicate;
                }

                section.IndexTargets.Add(target with { Kind = "TABLE" });
                return null;
            }

            return Unreadable;
        }

        if (TryWord(statement, ref index, "ALTER"))
        {
            if (!TryWord(statement, ref index, "TABLE") ||
                !TryQualified(statement, ref index, out Declared owner) ||
                !schemas.Contains(owner.Schema, StringComparer.Ordinal) ||
                !TryWord(statement, ref index, "ADD") ||
                !TryWord(statement, ref index, "CONSTRAINT") ||
                !TryQuoted(statement, ref index, out string constraint) ||
                !TryConstraint(statement, ref index, schemas, out Declared? referencedTable))
            {
                return Unreadable;
            }

            if (referencedTable is { } target)
            {
                section.ConstraintTargets.Add(target with { Kind = "TABLE" });
            }

            return section.Constraints.Add(new Declared(owner.Schema, "CONSTRAINT", constraint))
                ? Track(section.ConstraintTargets, owner with { Kind = "TABLE" })
                : Duplicate;
        }

        if (TryWord(statement, ref index, "GRANT"))
        {
            if (!TryBareName(statement, ref index, out string privilege) ||
                !ObjectPrivileges.Contains(privilege, StringComparer.Ordinal) ||
                !TryWord(statement, ref index, "ON") ||
                !TryQualified(statement, ref index, out Declared target) ||
                !schemas.Contains(target.Schema, StringComparer.Ordinal) ||
                !TryWord(statement, ref index, "TO") ||
                !TryGrantee(statement, ref index))
            {
                return Unreadable;
            }

            if (TryWord(statement, ref index, "WITH") &&
                (!TryWord(statement, ref index, "GRANT") || !TryWord(statement, ref index, "OPTION")))
            {
                return Unreadable;
            }

            if (!AtEnd(statement, index))
            {
                return Unreadable;
            }

            section.Grants++;
            section.GrantTargets.Add(target with { Kind = string.Empty });
            return null;
        }

        return Unreadable;
    }

    private const string Duplicate =
        "The inlined schema artifact writes the same object twice, so its statements and its coverage cannot both " +
        "be true and nothing was stored.";

    private static string? Track(List<Declared> targets, Declared target)
    {
        targets.Add(target);
        return null;
    }

    private static bool TryConstraint(
        string text, ref int index, IReadOnlyList<string> schemas, out Declared? referencedTable)
    {
        referencedTable = null;
        if (TryWord(text, ref index, "PRIMARY"))
        {
            if (!TryWord(text, ref index, "KEY") || !TryIdentifierList(text, ref index, out _))
            {
                return false;
            }
        }
        else if (TryWord(text, ref index, "UNIQUE"))
        {
            if (!TryIdentifierList(text, ref index, out _))
            {
                return false;
            }
        }
        else if (TryWord(text, ref index, "CHECK"))
        {
            if (!TryCheckCondition(text, ref index))
            {
                return false;
            }
        }
        else if (TryWord(text, ref index, "FOREIGN"))
        {
            if (!TryWord(text, ref index, "KEY") ||
                !TryIdentifierList(text, ref index, out int columns) ||
                !TryWord(text, ref index, "REFERENCES") ||
                !TryQualified(text, ref index, out Declared target) ||
                !schemas.Contains(target.Schema, StringComparer.Ordinal) ||
                !TryIdentifierList(text, ref index, out int referencedColumns) ||
                columns != referencedColumns)
            {
                return false;
            }

            referencedTable = target;
            if (TryWord(text, ref index, "ON") &&
                (!TryWord(text, ref index, "DELETE") ||
                 (!TryWord(text, ref index, "CASCADE") &&
                  !(TryWord(text, ref index, "SET") && TryWord(text, ref index, "NULL")))))
            {
                return false;
            }
        }
        else
        {
            return false;
        }

        TryWord(text, ref index, "DISABLE");
        return AtEnd(text, index);
    }

    private static bool TryIdentifierList(string text, ref int index, out int count)
    {
        count = 0;
        if (!TryLiteral(text, ref index, '('))
        {
            return false;
        }

        do
        {
            if (!TryQuoted(text, ref index, out _))
            {
                return false;
            }

            count++;
        }
        while (TryLiteral(text, ref index, ','));

        return TryLiteral(text, ref index, ')');
    }

    private static bool TryCheckCondition(string text, ref int index)
    {
        if (!TryLiteral(text, ref index, '('))
        {
            return false;
        }

        int depth = 1;
        bool hasContent = false;
        while (index < text.Length)
        {
            SkipSpace(text, ref index);
            if (index >= text.Length || !TrySkipOpaque(text, ref index, out bool skipped))
            {
                return false;
            }

            if (skipped)
            {
                hasContent = true;
                continue;
            }

            char current = text[index++];
            if (current == '(')
            {
                depth++;
            }
            else if (current == ')' && --depth == 0)
            {
                return hasContent;
            }
            else if (current == ';')
            {
                return false;
            }

            hasContent = true;
        }

        return false;
    }

    // -----------------------------------------------------------------------------------------------
    // The program-unit half: current-schema lines and bodies terminated by a line holding only '/'.
    // -----------------------------------------------------------------------------------------------

    private static string? ReadProgramUnits(string? sql, IReadOnlyList<string> schemas, out HashSet<Declared> units)
    {
        units = [];
        if (sql is null)
        {
            return null;
        }

        const string Unreadable =
            "The inlined schema artifact's program-unit section is not the shape this build's own emitter writes, so " +
            "the PL/SQL it carries could not be identified and nothing was stored.";

        string? current = null;
        int index = 0;
        while (true)
        {
            SkipSpace(sql, ref index);
            if (index >= sql.Length)
            {
                return null;
            }

            int cursor = index;
            if (TryWord(sql, ref cursor, "ALTER"))
            {
                if (!TryWord(sql, ref cursor, "SESSION") ||
                    !TryWord(sql, ref cursor, "SET") ||
                    !TryWord(sql, ref cursor, "CURRENT_SCHEMA") ||
                    !TryLiteral(sql, ref cursor, '=') ||
                    !TryQuoted(sql, ref cursor, out string schema) ||
                    !TryLiteral(sql, ref cursor, ';'))
                {
                    return Unreadable;
                }

                if (!schemas.Contains(schema, StringComparer.Ordinal))
                {
                    return
                        "The inlined schema artifact switches to a schema the request did not allow before writing " +
                        "program units, so nothing was stored.";
                }

                current = schema;
                index = cursor;
                continue;
            }

            if (current is null)
            {
                return Unreadable;
            }

            if (!TryWord(sql, ref cursor, "CREATE"))
            {
                return Unreadable;
            }

            if (TryWord(sql, ref cursor, "OR") && !TryWord(sql, ref cursor, "REPLACE"))
            {
                return Unreadable;
            }

            string? kind = null;
            foreach (string candidate in ProgramUnitKinds)
            {
                if (TryKind(sql, ref cursor, candidate))
                {
                    kind = candidate;
                    break;
                }
            }

            if (kind is null || !TryUnitName(sql, ref cursor, current, out string name))
            {
                return Unreadable;
            }

            int end = FindTerminator(sql, cursor);
            if (end < 0)
            {
                return
                    "The inlined schema artifact's last program unit is not terminated, so its PL/SQL is truncated and " +
                    "nothing was stored.";
            }

            if (units.Count >= SourceGatewayProtocol.MaxSchemaObjects)
            {
                return $"The inlined schema artifact carries more than {SourceGatewayProtocol.MaxSchemaObjects} program units.";
            }

            if (!units.Add(new Declared(current, kind, name)))
            {
                return Duplicate;
            }

            index = end;
        }
    }

    /// <summary>Index after the line that holds only '/', or -1 when the body never ends.</summary>
    private static int FindTerminator(string sql, int index)
    {
        while (index < sql.Length)
        {
            if ((index == 0 || sql[index - 1] == '\n') && IsSoloSlash(sql, index, out int next))
            {
                return next;
            }

            if (!TrySkipOpaque(sql, ref index, out bool skipped))
            {
                return -1;
            }

            if (!skipped)
            {
                index++;
            }
        }

        return -1;
    }

    private static bool IsSoloSlash(string sql, int index, out int next)
    {
        int end = sql.IndexOf('\n', index);
        int stop = end < 0 ? sql.Length : end;
        next = end < 0 ? sql.Length : end + 1;
        return sql.AsSpan(index, stop - index).Trim() is "/";
    }

    private static bool TryKind(string sql, ref int index, string kind)
    {
        int cursor = index;
        foreach (string word in kind.Split(' '))
        {
            if (!TryWord(sql, ref cursor, word))
            {
                return false;
            }
        }

        index = cursor;
        return true;
    }

    /// <summary>
    /// Reads the name a program unit declares, quoted or bare, and refuses a qualification that names an
    /// owner other than the schema the section switched to.
    /// </summary>
    private static bool TryUnitName(string sql, ref int index, string current, out string name)
    {
        int cursor = index;
        if (!TryQuoted(sql, ref cursor, out name) && !TryBareName(sql, ref cursor, out name))
        {
            return false;
        }

        if (cursor < sql.Length && sql[cursor] == '.')
        {
            if (!string.Equals(name, current, StringComparison.Ordinal))
            {
                return false;
            }

            cursor++;
            if (!TryQuoted(sql, ref cursor, out name) && !TryBareName(sql, ref cursor, out name))
            {
                return false;
            }
        }

        index = cursor;
        return true;
    }

    // -----------------------------------------------------------------------------------------------
    // Lexical primitives. Every one of them is here so that a keyword inside a literal, a comment or a
    // quoted identifier is never mistaken for a keyword in the statement.
    // -----------------------------------------------------------------------------------------------

    /// <summary>
    /// Advances past one literal, quoted identifier or comment at <paramref name="index"/>. Returns false
    /// only when the text ends inside one, which is what a truncated artifact looks like.
    /// </summary>
    private static bool TrySkipOpaque(string text, ref int index, out bool skipped)
    {
        skipped = true;
        char current = text[index];

        if (TryAlternateQuote(text, index, out int open))
        {
            char delimiter = text[open];
            char close = delimiter switch { '[' => ']', '{' => '}', '(' => ')', '<' => '>', _ => delimiter };
            for (int cursor = open + 1; cursor + 1 < text.Length; cursor++)
            {
                if (text[cursor] == close && text[cursor + 1] == '\'')
                {
                    index = cursor + 2;
                    return true;
                }
            }

            return false;
        }

        if (current == '\'')
        {
            for (int cursor = index + 1; cursor < text.Length; cursor++)
            {
                if (text[cursor] != '\'')
                {
                    continue;
                }

                if (cursor + 1 < text.Length && text[cursor + 1] == '\'')
                {
                    cursor++;
                    continue;
                }

                index = cursor + 1;
                return true;
            }

            return false;
        }

        if (current == '"')
        {
            int close = text.IndexOf('"', index + 1);
            if (close < 0)
            {
                return false;
            }

            index = close + 1;
            return true;
        }

        if (current == '-' && index + 1 < text.Length && text[index + 1] == '-')
        {
            int end = text.IndexOf('\n', index);
            index = end < 0 ? text.Length : end;
            return true;
        }

        if (current == '/' && index + 1 < text.Length && text[index + 1] == '*')
        {
            int end = text.IndexOf("*/", index + 2, StringComparison.Ordinal);
            if (end < 0)
            {
                return false;
            }

            index = end + 2;
            return true;
        }

        skipped = false;
        return true;
    }

    /// <summary>True at the opening quote of a q'...' literal, whose body may hold anything at all.</summary>
    private static bool TryAlternateQuote(string text, int index, out int open)
    {
        open = -1;
        if (index > 0 && IsNamePart(text[index - 1]))
        {
            return false;
        }

        int cursor = index;
        if (text[cursor] is 'n' or 'N')
        {
            cursor++;
        }

        if (cursor >= text.Length || text[cursor] is not ('q' or 'Q') || cursor + 2 >= text.Length || text[cursor + 1] != '\'')
        {
            return false;
        }

        open = cursor + 2;
        return true;
    }

    private static void SkipSpace(string text, ref int index)
    {
        while (index < text.Length)
        {
            if (char.IsWhiteSpace(text[index]))
            {
                index++;
                continue;
            }

            if (text[index] != '-' && text[index] != '/')
            {
                return;
            }

            int cursor = index;
            if (!TrySkipOpaque(text, ref cursor, out bool skipped))
            {
                index = text.Length;
                return;
            }

            if (!skipped || cursor == index)
            {
                return;
            }

            index = cursor;
        }
    }

    private static bool TryWord(string text, ref int index, string word)
    {
        int cursor = index;
        SkipSpace(text, ref cursor);
        if (cursor + word.Length > text.Length ||
            string.Compare(text, cursor, word, 0, word.Length, StringComparison.OrdinalIgnoreCase) != 0 ||
            (cursor + word.Length < text.Length && IsNamePart(text[cursor + word.Length])))
        {
            return false;
        }

        index = cursor + word.Length;
        return true;
    }

    private static bool TryLiteral(string text, ref int index, char expected)
    {
        int cursor = index;
        SkipSpace(text, ref cursor);
        if (cursor >= text.Length || text[cursor] != expected)
        {
            return false;
        }

        index = cursor + 1;
        return true;
    }

    private static bool TryQuoted(string text, ref int index, out string name)
    {
        name = string.Empty;
        int cursor = index;
        SkipSpace(text, ref cursor);
        if (cursor >= text.Length || text[cursor] != '"')
        {
            return false;
        }

        int close = text.IndexOf('"', cursor + 1);
        if (close < 0 || close - cursor - 1 is 0 or > SourceGatewayProtocol.MaxSchemaIdentifierCharacters)
        {
            return false;
        }

        name = text[(cursor + 1)..close];
        if (name.Any(char.IsControl))
        {
            return false;
        }

        index = close + 1;
        return true;
    }

    private static bool TryBareName(string text, ref int index, out string name)
    {
        name = string.Empty;
        int cursor = index;
        SkipSpace(text, ref cursor);
        int start = cursor;
        while (cursor < text.Length && IsNamePart(text[cursor]))
        {
            cursor++;
        }

        if (cursor == start || cursor - start > SourceGatewayProtocol.MaxSchemaIdentifierCharacters)
        {
            return false;
        }

        name = text[start..cursor];
        index = cursor;
        return true;
    }

    private static bool TryGrantee(string text, ref int index) =>
        TryQuoted(text, ref index, out _) || TryWord(text, ref index, "PUBLIC");

    private static bool TryQualified(string text, ref int index, out Declared declared)
    {
        declared = default;
        int cursor = index;
        if (!TryQuoted(text, ref cursor, out string owner) ||
            cursor >= text.Length ||
            text[cursor] != '.')
        {
            return false;
        }

        cursor++;
        if (!TryQuoted(text, ref cursor, out string name))
        {
            return false;
        }

        declared = new Declared(owner, string.Empty, name);
        index = cursor;
        return true;
    }

    /// <summary>Reads a parenthesised comma-separated list whose every item opens with a quoted name.</summary>
    private static bool TryColumnList(string text, ref int index, out int count)
    {
        count = 0;
        int cursor = index;
        SkipSpace(text, ref cursor);
        if (cursor >= text.Length || text[cursor] != '(')
        {
            return false;
        }

        cursor++;
        int depth = 1;
        int itemStart = cursor;
        List<string> items = [];
        while (cursor < text.Length)
        {
            if (!TrySkipOpaque(text, ref cursor, out bool skipped))
            {
                return false;
            }

            if (skipped)
            {
                continue;
            }

            char current = text[cursor];
            if (current == '(')
            {
                depth++;
            }
            else if (current == ')')
            {
                if (--depth == 0)
                {
                    items.Add(text[itemStart..cursor]);
                    index = cursor + 1;
                    count = items.Count;
                    return items.TrueForAll(item =>
                    {
                        int at = 0;
                        return TryQuoted(item, ref at, out _);
                    });
                }
            }
            else if (current == ',' && depth == 1)
            {
                items.Add(text[itemStart..cursor]);
                itemStart = cursor + 1;
            }

            cursor++;
        }

        return false;
    }

    private static bool AtEnd(string text, int index)
    {
        SkipSpace(text, ref index);
        return index >= text.Length;
    }

    private static bool IsNamePart(char value) => char.IsLetterOrDigit(value) || value is '_' or '$' or '#';

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
