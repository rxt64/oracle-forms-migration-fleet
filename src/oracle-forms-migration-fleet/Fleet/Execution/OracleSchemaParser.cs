// Copyright (c) Microsoft. All rights reserved.

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace OracleFormsMigrationFleet.Fleet.Execution;

/// <summary>
/// Parses Oracle DDL text into <see cref="OracleSchema"/>. Pure: no file, process, or network access.
/// Unrecognised statements are preserved in <see cref="OracleSchema.Unparsed"/> instead of throwing, and
/// PL/SQL program units are kept whole rather than interpreted.
/// </summary>
public static partial class OracleSchemaParser
{
    private const string Identifier = """(?:"[^"]+"|[A-Za-z_$#][A-Za-z0-9_$#]*)""";
    private const string QualifiedName = $"""(?<name>{Identifier}(?:\.{Identifier})?)""";

    public static OracleSchema Parse(string? ddl)
    {
        if (string.IsNullOrWhiteSpace(ddl))
        {
            return OracleSchema.Empty;
        }

        List<TableBuilder> tables = [];
        List<OracleSequence> sequences = [];
        List<OracleIndex> indexes = [];
        List<string> unparsed = [];
        List<OracleProgramUnit> programUnits = [];

        foreach (string statement in SplitStatements(StripComments(ddl)))
        {
            if (TryParseTable(statement) is TableBuilder table)
            {
                tables.Add(table);
            }
            else if (TryParseSequence(statement) is OracleSequence sequence)
            {
                sequences.Add(sequence);
            }
            else if (TryParseIndex(statement) is OracleIndex index)
            {
                indexes.Add(index);
            }
            else if (TryParseAlterTableConstraint(statement) is (string owner, OracleConstraint constraint) &&
                     tables.Find(candidate => string.Equals(candidate.Name, owner, StringComparison.OrdinalIgnoreCase)) is TableBuilder target)
            {
                target.Constraints.Add(constraint);
            }
            else
            {
                unparsed.Add(statement);

                if (TryReadProgramUnitIdentity(statement) is OracleProgramUnit unit)
                {
                    programUnits.Add(unit);
                }
            }
        }

        return new OracleSchema(
            [.. tables.Select(table => new OracleTable(table.Name, table.Columns, table.Constraints))],
            sequences,
            indexes,
            unparsed)
        {
            ProgramUnits = programUnits,
        };
    }

    /// <summary>
    /// Reads what a PL/SQL statement declares from its CREATE header alone: the kind of object and its
    /// local name. The body is not interpreted and no behaviour is inferred; this exists so a caller can
    /// test object identity exactly instead of searching the text for a name that might appear anywhere.
    /// </summary>
    public static OracleProgramUnit? TryReadProgramUnitIdentity(string? statement)
    {
        if (string.IsNullOrWhiteSpace(statement))
        {
            return null;
        }

        string text = statement.TrimStart();

        (Regex Pattern, OracleProgramUnitKind Kind)[] candidates =
        [
            (CreatePackageBodyHead(), OracleProgramUnitKind.PackageBody),
            (CreatePackageHead(), OracleProgramUnitKind.PackageSpecification),
            (CreateTriggerHead(), OracleProgramUnitKind.Trigger),
            (CreateProcedureHead(), OracleProgramUnitKind.Procedure),
            (CreateFunctionHead(), OracleProgramUnitKind.Function),
        ];

        foreach ((Regex pattern, OracleProgramUnitKind kind) in candidates)
        {
            Match match = pattern.Match(text);
            if (match.Success)
            {
                return new OracleProgramUnit(kind, LocalName(match.Groups["name"].Value), statement);
            }
        }

        return null;
    }

    private sealed class TableBuilder(string name)
    {
        public string Name { get; } = name;

        public List<OracleColumn> Columns { get; } = [];

        public List<OracleConstraint> Constraints { get; } = [];
    }

    // ---------- statement segmentation ----------

    /// <summary>Removes '--' and block comments without disturbing single-quoted literals or line count.</summary>
    private static string StripComments(string text)
    {
        StringBuilder result = new(text.Length);
        bool inString = false;

        for (int i = 0; i < text.Length; i++)
        {
            char current = text[i];

            if (inString)
            {
                result.Append(current);
                if (current == '\'')
                {
                    inString = false;
                }

                continue;
            }

            if (current == '\'')
            {
                inString = true;
                result.Append(current);
                continue;
            }

            if (current == '-' && i + 1 < text.Length && text[i + 1] == '-')
            {
                while (i < text.Length && text[i] != '\n')
                {
                    i++;
                }

                if (i < text.Length)
                {
                    result.Append('\n');
                }

                continue;
            }

            if (current == '/' && i + 1 < text.Length && text[i + 1] == '*')
            {
                i += 2;
                while (i + 1 < text.Length && !(text[i] == '*' && text[i + 1] == '/'))
                {
                    i++;
                }

                i++;
                continue;
            }

            result.Append(current);
        }

        return result.ToString();
    }

    /// <summary>
    /// Splits a script into statements. PL/SQL program units end at a line containing only '/', so their
    /// internal semicolons never fragment them; SQL*Plus directives have no terminator and are emitted alone.
    /// </summary>
    private static List<string> SplitStatements(string text)
    {
        List<string> statements = [];
        StringBuilder buffer = new();
        bool inString = false;
        int depth = 0;
        bool? isBlock = null;

        void Flush()
        {
            string statement = buffer.ToString().Trim();
            if (statement.Length > 0)
            {
                statements.Add(statement);
            }

            buffer.Clear();
            depth = 0;
            isBlock = null;
        }

        foreach (string line in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            string trimmed = line.Trim();

            if (!inString && trimmed == "/")
            {
                Flush();
                continue;
            }

            if (!inString && isBlock is null && trimmed.Length > 0)
            {
                if (SqlPlusDirective().IsMatch(trimmed) && !trimmed.EndsWith(';'))
                {
                    statements.Add(trimmed);
                    continue;
                }

                isBlock = PlSqlBlockStart().IsMatch(trimmed);
            }

            if (isBlock == true)
            {
                buffer.Append(line).Append('\n');
                continue;
            }

            foreach (char current in line)
            {
                if (inString)
                {
                    buffer.Append(current);
                    if (current == '\'')
                    {
                        inString = false;
                    }

                    continue;
                }

                switch (current)
                {
                    case '\'':
                        inString = true;
                        buffer.Append(current);
                        break;
                    case '(':
                        depth++;
                        buffer.Append(current);
                        break;
                    case ')':
                        if (depth > 0)
                        {
                            depth--;
                        }

                        buffer.Append(current);
                        break;
                    case ';' when depth == 0:
                        Flush();
                        break;
                    default:
                        buffer.Append(current);
                        break;
                }
            }

            buffer.Append('\n');
        }

        Flush();
        return statements;
    }

    // ---------- CREATE TABLE ----------

    private static TableBuilder? TryParseTable(string statement)
    {
        Match head = CreateTableHead().Match(statement);
        if (!head.Success)
        {
            return null;
        }

        int open = IndexOfTopLevelOpen(statement, head.Index + head.Length);
        if (open < 0)
        {
            return null;
        }

        int close = MatchingParen(statement, open);
        if (close < 0)
        {
            return null;
        }

        TableBuilder table = new(LocalName(head.Groups["name"].Value));

        foreach (string item in SplitTopLevel(statement[(open + 1)..close]))
        {
            if (TryParseConstraintItem(item) is OracleConstraint constraint)
            {
                table.Constraints.Add(constraint);
            }
            else if (TryParseColumn(item, table.Constraints) is OracleColumn column)
            {
                table.Columns.Add(column);
            }
        }

        return table.Columns.Count > 0 || table.Constraints.Count > 0 ? table : null;
    }

    private static OracleConstraint? TryParseConstraintItem(string item)
    {
        Match head = ConstraintHead().Match(item);
        if (!head.Success)
        {
            return null;
        }

        string? name = head.Groups["cname"].Success ? Unquote(head.Groups["cname"].Value) : null;
        string kind = Whitespace().Replace(head.Groups["kind"].Value, " ").ToUpperInvariant();
        int cursor = head.Index + head.Length;

        int open = IndexOfTopLevelOpen(item, cursor);
        if (open < 0)
        {
            return null;
        }

        int close = MatchingParen(item, open);
        if (close < 0)
        {
            return null;
        }

        string inner = item[(open + 1)..close];

        if (kind == "CHECK")
        {
            return new OracleConstraint { Kind = OracleConstraintKind.Check, Name = name, CheckExpression = inner.Trim() };
        }

        IReadOnlyList<string> columns = NameList(inner);

        if (kind == "PRIMARY KEY")
        {
            return new OracleConstraint { Kind = OracleConstraintKind.PrimaryKey, Name = name, Columns = columns };
        }

        if (kind == "UNIQUE")
        {
            return new OracleConstraint { Kind = OracleConstraintKind.Unique, Name = name, Columns = columns };
        }

        Match references = ReferencesClause().Match(item, close + 1);
        if (!references.Success)
        {
            return new OracleConstraint { Kind = OracleConstraintKind.ForeignKey, Name = name, Columns = columns };
        }

        return new OracleConstraint
        {
            Kind = OracleConstraintKind.ForeignKey,
            Name = name,
            Columns = columns,
            ReferencedTable = LocalName(references.Groups["name"].Value),
            ReferencedColumns = ReferencedColumns(item, references.Index + references.Length),
        };
    }

    private static OracleColumn? TryParseColumn(string item, List<OracleConstraint> constraints)
    {
        Match name = ColumnName().Match(item);
        if (!name.Success)
        {
            return null;
        }

        string rest = item[(name.Index + name.Length)..];
        if (ReadType(rest) is not TypeInfo type)
        {
            return null;
        }

        string columnName = Unquote(name.Groups["name"].Value);
        string tail = rest[type.Length..];
        string mask = Mask(tail);

        ReadInlineConstraints(tail, mask, columnName, constraints);

        return new OracleColumn(
            columnName,
            type.Raw,
            type.BaseType,
            type.Precision,
            type.Scale,
            NotNullClause().IsMatch(mask),
            ReadDefault(tail, mask));
    }

    private static string? ReadDefault(string tail, string mask)
    {
        Match keyword = DefaultClause().Match(mask);
        if (!keyword.Success)
        {
            return null;
        }

        int start = keyword.Index + keyword.Length;
        int end = tail.Length;

        foreach (Match boundary in ColumnClauseBoundary().Matches(mask))
        {
            if (boundary.Index >= start && boundary.Index < end)
            {
                end = boundary.Index;
            }
        }

        string expression = tail[start..end].Trim();
        return expression.Length > 0 ? expression : "NULL";
    }

    private static void ReadInlineConstraints(string tail, string mask, string column, List<OracleConstraint> constraints)
    {
        if (InlinePrimaryKey().Match(mask) is { Success: true } primaryKey)
        {
            constraints.Add(new OracleConstraint
            {
                Kind = OracleConstraintKind.PrimaryKey,
                Name = InlineName(primaryKey),
                Columns = [column],
            });
        }
        else if (InlineUnique().Match(mask) is { Success: true } unique)
        {
            constraints.Add(new OracleConstraint
            {
                Kind = OracleConstraintKind.Unique,
                Name = InlineName(unique),
                Columns = [column],
            });
        }

        if (InlineReferences().Match(mask) is { Success: true } references)
        {
            constraints.Add(new OracleConstraint
            {
                Kind = OracleConstraintKind.ForeignKey,
                Name = InlineName(references),
                Columns = [column],
                ReferencedTable = LocalName(references.Groups["name"].Value),
                ReferencedColumns = ReferencedColumns(tail, references.Index + references.Length),
            });
        }

        if (InlineCheck().Match(mask) is { Success: true } check)
        {
            int open = IndexOfTopLevelOpen(tail, check.Index + check.Length);
            int close = open >= 0 ? MatchingParen(tail, open) : -1;
            if (close > open)
            {
                constraints.Add(new OracleConstraint
                {
                    Kind = OracleConstraintKind.Check,
                    Name = InlineName(check),
                    CheckExpression = tail[(open + 1)..close].Trim(),
                });
            }
        }
    }

    private static string? InlineName(Match match) =>
        match.Groups["cname"].Success ? Unquote(match.Groups["cname"].Value) : null;

    private static IReadOnlyList<string> ReferencedColumns(string text, int cursor)
    {
        int open = IndexOfTopLevelOpen(text, cursor);
        if (open < 0 || text[cursor..open].Trim().Length > 0)
        {
            return [];
        }

        int close = MatchingParen(text, open);
        return close > open ? NameList(text[(open + 1)..close]) : [];
    }

    // ---------- type reading ----------

    private sealed record TypeInfo(string Raw, string BaseType, int? Precision, int? Scale, int Length);

    private static TypeInfo? ReadType(string text)
    {
        Match word = TypeWord().Match(text);
        if (!word.Success)
        {
            return null;
        }

        string baseType = word.Groups["word"].Value.ToUpperInvariant();
        int cursor = word.Index + word.Length;
        int? precision = null;
        int? scale = null;
        bool readArguments = true;

        if (baseType == "LONG" && TryPhrase(RawSuffix(), text, ref cursor))
        {
            baseType = "LONG RAW";
            readArguments = false;
        }
        else if (baseType == "DOUBLE" && TryPhrase(PrecisionSuffix(), text, ref cursor))
        {
            baseType = "DOUBLE PRECISION";
            readArguments = false;
        }
        else if (baseType == "INTERVAL")
        {
            Match interval = IntervalSuffix().Match(text, cursor);
            if (interval.Success && interval.Index == cursor)
            {
                baseType = interval.Groups["unit"].Value.Equals("YEAR", StringComparison.OrdinalIgnoreCase)
                    ? "INTERVAL YEAR TO MONTH"
                    : "INTERVAL DAY TO SECOND";
                cursor = interval.Index + interval.Length;
                readArguments = false;
            }
        }

        if (readArguments)
        {
            int probe = SkipWhitespace(text, cursor);
            if (probe < text.Length && text[probe] == '(')
            {
                int close = MatchingParen(text, probe);
                if (close > probe)
                {
                    (precision, scale) = ReadPrecision(text[(probe + 1)..close]);
                    cursor = close + 1;
                }
            }

            if (baseType == "TIMESTAMP")
            {
                if (TryPhrase(LocalTimeZoneSuffix(), text, ref cursor))
                {
                    baseType = "TIMESTAMP WITH LOCAL TIME ZONE";
                }
                else if (TryPhrase(TimeZoneSuffix(), text, ref cursor))
                {
                    baseType = "TIMESTAMP WITH TIME ZONE";
                }
            }
        }

        string raw = Whitespace().Replace(text[word.Index..cursor].Trim(), " ");
        return new TypeInfo(raw, baseType, precision, scale, cursor);
    }

    private static bool TryPhrase(Regex phrase, string text, ref int cursor)
    {
        Match match = phrase.Match(text, cursor);
        if (!match.Success || match.Index != cursor)
        {
            return false;
        }

        cursor = match.Index + match.Length;
        return true;
    }

    private static (int? Precision, int? Scale) ReadPrecision(string arguments)
    {
        string[] parts = arguments.Split(',');
        int? precision = ReadNumber(parts[0]);
        int? scale = parts.Length > 1 ? ReadNumber(parts[1]) : null;
        return (precision, scale);
    }

    private static int? ReadNumber(string text)
    {
        Match digits = SignedInteger().Match(text);
        return digits.Success && int.TryParse(digits.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
            ? value
            : null;
    }

    // ---------- CREATE SEQUENCE / CREATE INDEX / ALTER TABLE ----------

    private static OracleSequence? TryParseSequence(string statement)
    {
        Match head = CreateSequenceHead().Match(statement);
        if (!head.Success)
        {
            return null;
        }

        return new OracleSequence(
            LocalName(head.Groups["name"].Value),
            ReadLong(StartWithClause().Match(statement)),
            ReadLong(IncrementByClause().Match(statement)));
    }

    private static long? ReadLong(Match match) =>
        match.Success && long.TryParse(match.Groups["value"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long value)
            ? value
            : null;

    private static OracleIndex? TryParseIndex(string statement)
    {
        Match head = CreateIndexHead().Match(statement);
        if (!head.Success)
        {
            return null;
        }

        int open = IndexOfTopLevelOpen(statement, head.Index + head.Length);
        int close = open >= 0 ? MatchingParen(statement, open) : -1;
        if (close <= open)
        {
            return null;
        }

        return new OracleIndex(
            LocalName(head.Groups["name"].Value),
            LocalName(head.Groups["table"].Value),
            [.. SplitTopLevel(statement[(open + 1)..close]).Select(Unquote)],
            head.Groups["unique"].Success);
    }

    private static (string Table, OracleConstraint Constraint)? TryParseAlterTableConstraint(string statement)
    {
        Match head = AlterTableAdd().Match(statement);
        if (!head.Success)
        {
            return null;
        }

        string rest = statement[(head.Index + head.Length)..].Trim();
        if (rest.StartsWith('('))
        {
            int close = MatchingParen(rest, 0);
            if (close < 0)
            {
                return null;
            }

            rest = rest[1..close];
        }

        return TryParseConstraintItem(rest) is OracleConstraint constraint
            ? (LocalName(head.Groups["table"].Value), constraint)
            : null;
    }

    // ---------- text helpers ----------

    /// <summary>
    /// Returns a same-length copy with string literal and parenthesised content blanked out, so keyword
    /// searches only match at the top level while match indices still line up with the original text.
    /// </summary>
    private static string Mask(string text)
    {
        char[] mask = text.ToCharArray();
        bool inString = false;
        int depth = 0;

        for (int i = 0; i < mask.Length; i++)
        {
            char current = mask[i];

            if (inString)
            {
                if (current == '\'')
                {
                    inString = false;
                }
                else
                {
                    mask[i] = ' ';
                }

                continue;
            }

            switch (current)
            {
                case '\'':
                    inString = true;
                    break;
                case '(':
                    depth++;
                    break;
                case ')':
                    if (depth > 0)
                    {
                        depth--;
                    }

                    break;
                default:
                    if (depth > 0)
                    {
                        mask[i] = ' ';
                    }

                    break;
            }
        }

        return new string(mask);
    }

    private static List<string> SplitTopLevel(string text)
    {
        List<string> parts = [];
        StringBuilder buffer = new();
        bool inString = false;
        int depth = 0;

        foreach (char current in text)
        {
            if (inString)
            {
                buffer.Append(current);
                if (current == '\'')
                {
                    inString = false;
                }

                continue;
            }

            switch (current)
            {
                case '\'':
                    inString = true;
                    buffer.Append(current);
                    break;
                case '(':
                    depth++;
                    buffer.Append(current);
                    break;
                case ')':
                    if (depth > 0)
                    {
                        depth--;
                    }

                    buffer.Append(current);
                    break;
                case ',' when depth == 0:
                    parts.Add(buffer.ToString());
                    buffer.Clear();
                    break;
                default:
                    buffer.Append(current);
                    break;
            }
        }

        parts.Add(buffer.ToString());
        return [.. parts.Select(part => part.Trim()).Where(part => part.Length > 0)];
    }

    private static IReadOnlyList<string> NameList(string text) => [.. SplitTopLevel(text).Select(Unquote)];

    private static int IndexOfTopLevelOpen(string text, int start)
    {
        bool inString = false;
        for (int i = Math.Max(start, 0); i < text.Length; i++)
        {
            char current = text[i];
            if (inString)
            {
                if (current == '\'')
                {
                    inString = false;
                }

                continue;
            }

            if (current == '\'')
            {
                inString = true;
            }
            else if (current == '(')
            {
                return i;
            }
        }

        return -1;
    }

    private static int MatchingParen(string text, int openIndex)
    {
        bool inString = false;
        int depth = 0;

        for (int i = openIndex; i < text.Length; i++)
        {
            char current = text[i];
            if (inString)
            {
                if (current == '\'')
                {
                    inString = false;
                }

                continue;
            }

            switch (current)
            {
                case '\'':
                    inString = true;
                    break;
                case '(':
                    depth++;
                    break;
                case ')':
                    depth--;
                    if (depth == 0)
                    {
                        return i;
                    }

                    break;
            }
        }

        return -1;
    }

    private static int SkipWhitespace(string text, int start)
    {
        int cursor = Math.Max(start, 0);
        while (cursor < text.Length && char.IsWhiteSpace(text[cursor]))
        {
            cursor++;
        }

        return cursor;
    }

    private static string Unquote(string name)
    {
        string trimmed = name.Trim();
        return trimmed.Length >= 2 && trimmed[0] == '"' && trimmed[^1] == '"' ? trimmed[1..^1] : trimmed;
    }

    /// <summary>Drops any schema qualifier so 'BANKING.ORDERS' and 'ORDERS' resolve to the same object.</summary>
    private static string LocalName(string name)
    {
        string trimmed = name.Trim();
        int separator = trimmed.LastIndexOf('.');
        return separator >= 0 && !trimmed.EndsWith('"') ? Unquote(trimmed[(separator + 1)..]) : Unquote(trimmed);
    }

    // ---------- patterns ----------

    [GeneratedRegex(@"^(SET|SHOW|WHENEVER|SPOOL|PROMPT|REM|DEFINE|UNDEFINE|COLUMN|TTITLE|BTITLE|ACCEPT|PAUSE|VARIABLE|CONNECT|DISCONNECT|EXIT|QUIT|HOST|START|@)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SqlPlusDirective();

    [GeneratedRegex(@"^\s*(?:CREATE\s+(?:OR\s+REPLACE\s+)?(?:EDITIONABLE\s+|NONEDITIONABLE\s+)?(?:PACKAGE|PROCEDURE|FUNCTION|TRIGGER|TYPE)\b|DECLARE\b|BEGIN\b)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PlSqlBlockStart();

    [GeneratedRegex($@"^\s*CREATE\s+(?:GLOBAL\s+TEMPORARY\s+|PRIVATE\s+TEMPORARY\s+)?TABLE\s+{QualifiedName}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CreateTableHead();

    [GeneratedRegex($@"^\s*CREATE\s+SEQUENCE\s+{QualifiedName}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CreateSequenceHead();

    private const string ProgramUnitPrefix = @"^\s*CREATE\s+(?:OR\s+REPLACE\s+)?(?:EDITIONABLE\s+|NONEDITIONABLE\s+)?";

    [GeneratedRegex($@"{ProgramUnitPrefix}PACKAGE\s+BODY\s+{QualifiedName}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CreatePackageBodyHead();

    [GeneratedRegex($@"{ProgramUnitPrefix}PACKAGE\s+{QualifiedName}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CreatePackageHead();

    [GeneratedRegex($@"{ProgramUnitPrefix}TRIGGER\s+{QualifiedName}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CreateTriggerHead();

    [GeneratedRegex($@"{ProgramUnitPrefix}PROCEDURE\s+{QualifiedName}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CreateProcedureHead();

    [GeneratedRegex($@"{ProgramUnitPrefix}FUNCTION\s+{QualifiedName}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CreateFunctionHead();

    [GeneratedRegex($@"^\s*CREATE\s+(?<unique>UNIQUE\s+)?(?:BITMAP\s+)?INDEX\s+{QualifiedName}\s+ON\s+(?<table>{Identifier}(?:\.{Identifier})?)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CreateIndexHead();

    [GeneratedRegex($@"^\s*ALTER\s+TABLE\s+(?<table>{Identifier}(?:\.{Identifier})?)\s+ADD\s+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AlterTableAdd();

    [GeneratedRegex(@"\bSTART\s+WITH\s+(?<value>-?\d+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex StartWithClause();

    [GeneratedRegex(@"\bINCREMENT\s+BY\s+(?<value>-?\d+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex IncrementByClause();

    [GeneratedRegex($@"^\s*(?:CONSTRAINT\s+(?<cname>{Identifier})\s+)?(?<kind>PRIMARY\s+KEY|UNIQUE|FOREIGN\s+KEY|CHECK)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ConstraintHead();

    [GeneratedRegex($@"\s*REFERENCES\s+{QualifiedName}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ReferencesClause();

    [GeneratedRegex($@"^\s*(?<name>{Identifier})\s+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ColumnName();

    [GeneratedRegex(@"^\s*(?<word>[A-Za-z][A-Za-z0-9_]*)", RegexOptions.CultureInvariant)]
    private static partial Regex TypeWord();

    [GeneratedRegex(@"\s*RAW\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RawSuffix();

    [GeneratedRegex(@"\s*PRECISION\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PrecisionSuffix();

    [GeneratedRegex(@"\s*WITH\s+LOCAL\s+TIME\s+ZONE\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LocalTimeZoneSuffix();

    [GeneratedRegex(@"\s*WITH\s+TIME\s+ZONE\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TimeZoneSuffix();

    [GeneratedRegex(@"\s*(?<unit>YEAR|DAY)\s*(?:\(\s*\d+\s*\))?\s+TO\s+(?:MONTH|SECOND)\s*(?:\(\s*\d+\s*\))?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex IntervalSuffix();

    [GeneratedRegex(@"\bNOT\s+NULL\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NotNullClause();

    [GeneratedRegex(@"\bDEFAULT\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DefaultClause();

    [GeneratedRegex(@"\b(?:NOT\s+NULL|NULL|PRIMARY\s+KEY|UNIQUE|REFERENCES|CHECK|CONSTRAINT|ENABLE|DISABLE|GENERATED)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ColumnClauseBoundary();

    [GeneratedRegex($@"(?:CONSTRAINT\s+(?<cname>{Identifier})\s+)?PRIMARY\s+KEY\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex InlinePrimaryKey();

    [GeneratedRegex($@"(?:CONSTRAINT\s+(?<cname>{Identifier})\s+)?UNIQUE\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex InlineUnique();

    [GeneratedRegex($@"(?:CONSTRAINT\s+(?<cname>{Identifier})\s+)?REFERENCES\s+{QualifiedName}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex InlineReferences();

    [GeneratedRegex($@"(?:CONSTRAINT\s+(?<cname>{Identifier})\s+)?CHECK\s*(?=\()", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex InlineCheck();

    [GeneratedRegex(@"-?\d+", RegexOptions.CultureInvariant)]
    private static partial Regex SignedInteger();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex Whitespace();
}
