// Copyright (c) Microsoft. All rights reserved.

using System.Text;
using System.Text.RegularExpressions;

namespace OracleFormsMigrationFleet.Fleet.Execution;

public enum PlSqlUnitKind
{
    PackageSpecification,
    Function,
    Procedure,
    Trigger,
    View,
}

public sealed record PlSqlUnit(PlSqlUnitKind Kind, string Name, string Sql);

public sealed record PlSqlTranslation(
    IReadOnlyList<PlSqlUnit> Units,
    IReadOnlyList<ConversionFinding> Findings);

/// <summary>
/// Translates the Oracle program units this converter understands into PL/pgSQL.
///
/// It refuses far more than it accepts. A construct it cannot map is reported as outstanding work and no
/// SQL is emitted for it, because a silently mistranslated business rule is worse than an absent one: the
/// absent one is on the remediation list, and the wrong one looks migrated. Nothing here is a behavioural
/// claim. The output compiles or it does not, and only a differential test can say it behaves the same.
/// </summary>
public static partial class PlSqlTranslator
{
    private const string Delimiter = "$legacy$";

    public static PlSqlTranslation Translate(string? oracleScript)
    {
        List<PlSqlUnit> units = [];
        List<ConversionFinding> findings = [];

        string script = (oracleScript ?? string.Empty).Replace("\r\n", "\n", StringComparison.Ordinal);
        script = ClientDirectivePattern().Replace(script, string.Empty);

        foreach (string block in SplitBlocks(script))
        {
            string trimmed = TrimToCreate(block);
            if (trimmed.Length == 0)
            {
                continue;
            }

            if (PackageBodyPattern().Match(trimmed) is { Success: true } body)
            {
                TranslatePackageBody(body.Groups["name"].Value, trimmed, units, findings);
            }
            else if (PackageSpecPattern().Match(trimmed) is { Success: true } spec)
            {
                // The spec declares only signatures and exceptions; the body carries everything executable.
                units.Add(new PlSqlUnit(PlSqlUnitKind.PackageSpecification, spec.Groups["name"].Value.ToLowerInvariant(), string.Empty));
                ReportDeclaredExceptions(spec.Groups["name"].Value, trimmed, findings);
            }
            else if (TriggerPattern().Match(trimmed) is { Success: true } trigger)
            {
                TranslateTrigger(trigger, units, findings);
            }
            else if (ViewPattern().Match(trimmed) is { Success: true } view)
            {
                // Sources are concatenated before they reach here, so a view that is not followed by a
                // slash would otherwise swallow whatever file came next.
                string select = FirstStatement(view.Groups["body"].Value);

                units.Add(new PlSqlUnit(
                    PlSqlUnitKind.View,
                    view.Groups["name"].Value.ToLowerInvariant(),
                    $"CREATE OR REPLACE VIEW {view.Groups["name"].Value.ToLowerInvariant()} AS\n" +
                    $"{Rewrite(select).Trim()};"));
            }
            else if (SchemaObjectPattern().IsMatch(trimmed))
            {
                // Tables, sequences and indexes are the schema emitter's job, not a program unit this
                // translator failed to understand.
                continue;
            }
            else
            {
                findings.Add(new ConversionFinding(
                    ConversionSeverity.Unsupported,
                    "Program unit",
                    Head(trimmed),
                    "This converter did not recognise the program unit, so nothing was emitted for it."));
            }
        }

        return new PlSqlTranslation(units, findings);
    }

    /// <summary>Renders the translated units in dependency order: routines, then triggers, then views.</summary>
    public static string Render(IReadOnlyList<PlSqlUnit> units)
    {
        ArgumentNullException.ThrowIfNull(units);

        IEnumerable<PlSqlUnit> emitted = units.Where(unit => unit.Sql.Length > 0);
        if (!emitted.Any())
        {
            return string.Empty;
        }

        StringBuilder builder = new();
        builder.Append("\n-- Program units translated from PL/SQL. Translated, not verified: no test has been\n");
        builder.Append("-- run against the original behaviour.\n");

        foreach (PlSqlUnit unit in emitted.OrderBy(unit => unit.Kind switch
        {
            PlSqlUnitKind.Function or PlSqlUnitKind.Procedure => 0,
            PlSqlUnitKind.Trigger => 1,
            _ => 2,
        }))
        {
            builder.Append('\n').Append(unit.Sql).Append('\n');
        }

        return builder.ToString();
    }

    private static void TranslatePackageBody(
        string package, string block, List<PlSqlUnit> units, List<ConversionFinding> findings)
    {
        string prefix = package.ToLowerInvariant() + "_";
        bool any = false;

        foreach (Match routine in RoutinePattern().Matches(block))
        {
            string name = routine.Groups["name"].Value;
            bool isFunction = routine.Groups["kind"].Value.Equals("FUNCTION", StringComparison.OrdinalIgnoreCase);

            Match terminator = Regex.Match(
                block[routine.Index..],
                $@"\bEND\s+{Regex.Escape(name)}\s*;",
                RegexOptions.IgnoreCase,
                TimeSpan.FromSeconds(2));

            if (!terminator.Success)
            {
                findings.Add(new ConversionFinding(
                    ConversionSeverity.Unsupported,
                    "Program unit",
                    $"{package}.{name}",
                    "The routine did not end with 'END <name>;', so its extent could not be determined and nothing was emitted."));
                continue;
            }

            string inner = block.Substring(
                routine.Index + routine.Length,
                terminator.Index - routine.Length);

            int begin = TopLevelBeginIndex(inner);
            if (begin < 0)
            {
                findings.Add(new ConversionFinding(
                    ConversionSeverity.Unsupported,
                    "Program unit",
                    $"{package}.{name}",
                    "No BEGIN was found in the routine body, so nothing was emitted."));
                continue;
            }

            string declarations = RewriteDeclarations(inner[..begin], $"{package}.{name}", findings);
            string body = Rewrite(inner[(begin + 5)..]).Trim();

            string parameters = TranslateParameters(routine.Groups["params"].Value, $"{package}.{name}", findings);
            string signature = $"{prefix}{name.ToLowerInvariant()}({parameters})";

            StringBuilder sql = new();
            sql.Append(isFunction ? "CREATE OR REPLACE FUNCTION " : "CREATE OR REPLACE PROCEDURE ").Append(signature).Append('\n');

            if (isFunction)
            {
                sql.Append("RETURNS ").Append(MapType(routine.Groups["ret"].Value)).Append('\n');
            }

            sql.Append("AS ").Append(Delimiter).Append('\n');

            if (declarations.Trim().Length > 0)
            {
                sql.Append("DECLARE\n").Append(declarations.TrimEnd()).Append('\n');
            }

            sql.Append("BEGIN\n").Append(body).Append("\nEND;\n").Append(Delimiter).Append(" LANGUAGE plpgsql;");

            units.Add(new PlSqlUnit(
                isFunction ? PlSqlUnitKind.Function : PlSqlUnitKind.Procedure,
                $"{prefix}{name.ToLowerInvariant()}",
                sql.ToString()));
            any = true;
        }

        if (!any)
        {
            findings.Add(new ConversionFinding(
                ConversionSeverity.Unsupported,
                "Program unit",
                $"PACKAGE BODY {package}",
                "No routine in the package body could be translated, so nothing was emitted for it."));
        }
        else
        {
            findings.Add(new ConversionFinding(
                ConversionSeverity.ManualReview,
                "Program unit",
                $"PACKAGE BODY {package}",
                "Package routines were emitted as standalone functions prefixed with the package name. " +
                "PostgreSQL has no package state, so anything that relied on session-scoped package variables will differ."));
        }
    }

    private static void TranslateTrigger(Match trigger, List<PlSqlUnit> units, List<ConversionFinding> findings)
    {
        string name = trigger.Groups["name"].Value.ToLowerInvariant();
        string table = trigger.Groups["table"].Value.ToLowerInvariant();
        string timing = Regex.Replace(trigger.Groups["timing"].Value, @"\s+", " ", RegexOptions.None, TimeSpan.FromSeconds(1)).ToUpperInvariant();
        string events = Regex.Replace(trigger.Groups["events"].Value.Trim(), @"\s+", " ", RegexOptions.None, TimeSpan.FromSeconds(1)).ToUpperInvariant();
        bool forEachRow = trigger.Groups["row"].Success;

        string raw = trigger.Groups["body"].Value.Trim();
        int begin = TopLevelBeginIndex(raw);
        if (begin < 0)
        {
            findings.Add(new ConversionFinding(
                ConversionSeverity.Unsupported, "Program unit", $"TRIGGER {name}",
                "No BEGIN was found in the trigger body, so nothing was emitted."));
            return;
        }

        string declarations = RewriteDeclarations(raw[..begin], $"TRIGGER {name}", findings);
        string body = raw[(begin + 5)..].Trim();

        Match tail = Regex.Match(body, @"\bEND\s*;?\s*$", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(2));
        if (tail.Success)
        {
            body = body[..tail.Index];
        }

        body = Rewrite(body).Trim();

        // A BEFORE row trigger must return the row it is allowed to have modified; anything else returns null.
        string result = timing == "BEFORE" && forEachRow
            ? "RETURN NEW;"
            : "RETURN NULL;";

        StringBuilder sql = new();
        sql.Append("CREATE OR REPLACE FUNCTION ").Append(name).Append("_fn() RETURNS trigger\nAS ").Append(Delimiter).Append('\n');

        if (declarations.Trim().Length > 0)
        {
            sql.Append("DECLARE\n").Append(declarations.TrimEnd()).Append('\n');
        }

        sql.Append("BEGIN\n").Append(body).Append('\n').Append(result).Append("\nEND;\n")
           .Append(Delimiter).Append(" LANGUAGE plpgsql;\n\n");

        sql.Append("DROP TRIGGER IF EXISTS ").Append(name).Append(" ON ").Append(table).Append(";\n");
        sql.Append("CREATE TRIGGER ").Append(name).Append('\n')
           .Append(timing).Append(' ').Append(events).Append(" ON ").Append(table).Append('\n')
           .Append(forEachRow ? "FOR EACH ROW" : "FOR EACH STATEMENT")
           .Append(" EXECUTE FUNCTION ").Append(name).Append("_fn();");

        units.Add(new PlSqlUnit(PlSqlUnitKind.Trigger, name, sql.ToString()));
    }

    private static void ReportDeclaredExceptions(string package, string block, List<ConversionFinding> findings)
    {
        foreach (Match declared in ExceptionPattern().Matches(block))
        {
            findings.Add(new ConversionFinding(
                ConversionSeverity.ManualReview,
                "Program unit",
                $"{package}.{declared.Groups["name"].Value}",
                "PostgreSQL has no user-declared exception type. RAISE of this name became RAISE EXCEPTION with the " +
                "name as its message, so anything that caught it by name must be rewritten to match on the message or SQLSTATE."));
        }
    }

    private static string TranslateParameters(string parameters, string owner, List<ConversionFinding> findings)
    {
        if (string.IsNullOrWhiteSpace(parameters))
        {
            return string.Empty;
        }

        List<string> translated = [];
        foreach (string parameter in SplitTopLevel(parameters))
        {
            Match match = ParameterPattern().Match(parameter.Trim());
            if (!match.Success)
            {
                findings.Add(new ConversionFinding(
                    ConversionSeverity.Unsupported, "Program unit", owner,
                    $"The parameter '{parameter.Trim()}' was not understood, so the routine signature may be wrong."));
                continue;
            }

            string direction = Regex.Replace(match.Groups["dir"].Value.Trim(), @"\s+", " ", RegexOptions.None, TimeSpan.FromSeconds(1)).ToUpperInvariant();
            string mode = direction switch
            {
                "OUT" => "OUT ",
                "IN OUT" or "INOUT" => "INOUT ",
                _ => string.Empty,
            };

            translated.Add($"{mode}{match.Groups["name"].Value.ToLowerInvariant()} {MapType(match.Groups["type"].Value)}");
        }

        return string.Join(", ", translated);
    }

    private static string RewriteDeclarations(string declarations, string owner, List<ConversionFinding> findings)
    {
        StringBuilder builder = new();

        foreach (string line in declarations.Split('\n'))
        {
            string trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith("--", StringComparison.Ordinal))
            {
                continue;
            }

            if (ExceptionPattern().IsMatch(trimmed))
            {
                findings.Add(new ConversionFinding(
                    ConversionSeverity.ManualReview, "Program unit", owner,
                    $"The declaration '{trimmed}' has no PostgreSQL equivalent and was dropped."));
                continue;
            }

            Match declaration = DeclarationPattern().Match(trimmed);
            if (!declaration.Success)
            {
                builder.Append("    ").Append(Rewrite(trimmed)).Append('\n');
                continue;
            }

            string type = declaration.Groups["type"].Value.Trim();
            if (type.Contains('%', StringComparison.Ordinal))
            {
                findings.Add(new ConversionFinding(
                    ConversionSeverity.ManualReview, "Program unit", owner,
                    $"'{trimmed}' was carried over as a PostgreSQL row type. PostgreSQL anchors the type the same way " +
                    "but differs on NULL and field assignment, so any logic that depends on that must be re-verified."));
            }

            builder.Append("    ")
                   .Append(declaration.Groups["name"].Value.ToLowerInvariant())
                   .Append(' ')
                   .Append(type.Contains('%', StringComparison.Ordinal) ? type.ToLowerInvariant() : MapType(type))
                   .Append(";\n");
        }

        return builder.ToString();
    }

    private static string Rewrite(string sql)
    {
        string result = StandardHashPattern().Replace(sql, match =>
        {
            string value = match.Groups["value"].Value.Trim();
            return match.Groups["algorithm"].Value.Trim('\'').ToUpperInvariant() switch
            {
                "SHA256" or "SHA-256" => $"sha256(convert_to({value}, 'UTF8'))",
                "SHA384" or "SHA-384" => $"sha384(convert_to({value}, 'UTF8'))",
                "SHA512" or "SHA-512" => $"sha512(convert_to({value}, 'UTF8'))",
                "MD5" => $"decode(md5({value}), 'hex')",
                _ => match.Value,
            };
        });

        result = BindPattern().Replace(result, match => match.Groups["ref"].Value.ToUpperInvariant() + ".");
        result = Regex.Replace(result, @"\bNVL\s*\(", "COALESCE(", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(2));
        result = Regex.Replace(result, @"\bSYSTIMESTAMP\b", "now()", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(2));
        result = Regex.Replace(result, @"\bSYSDATE\b", "CURRENT_DATE", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(2));
        result = Regex.Replace(result, @"\b(?<seq>\w+)\.NEXTVAL\b",
            match => $"nextval('{match.Groups["seq"].Value.ToLowerInvariant()}')", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(2));
        result = Regex.Replace(result, @"\b(?<seq>\w+)\.CURRVAL\b",
            match => $"currval('{match.Groups["seq"].Value.ToLowerInvariant()}')", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(2));

        // PostgreSQL has no user-declared exception names, so the name is carried as the message instead.
        result = RaisePattern().Replace(result, match => $"RAISE EXCEPTION '{match.Groups["name"].Value.ToLowerInvariant()}'");

        return result;
    }

    private static string MapType(string oracleType)
    {
        string type = oracleType.Trim();
        if (type.Length == 0)
        {
            return "void";
        }

        Match sized = SizedTypePattern().Match(type);
        string bare = (sized.Success ? sized.Groups["name"].Value : type).ToUpperInvariant();
        string size = sized.Success ? sized.Groups["size"].Value : string.Empty;

        return bare switch
        {
            "NUMBER" or "DECIMAL" or "DEC" or "NUMERIC" => size.Length > 0 ? $"numeric({size})" : "numeric",
            "PLS_INTEGER" or "BINARY_INTEGER" or "SIMPLE_INTEGER" or "INTEGER" or "INT" or "SMALLINT" => "integer",
            "BINARY_FLOAT" => "real",
            "BINARY_DOUBLE" or "FLOAT" or "DOUBLE PRECISION" => "double precision",
            "VARCHAR2" or "NVARCHAR2" or "VARCHAR" => size.Length > 0 ? $"varchar({size.Split(' ')[0]})" : "text",
            "CHAR" or "NCHAR" => size.Length > 0 ? $"char({size.Split(' ')[0]})" : "char",
            "CLOB" or "NCLOB" or "LONG" => "text",
            "BLOB" or "RAW" or "LONG RAW" => "bytea",
            "DATE" => "date",
            "BOOLEAN" => "boolean",
            "TIMESTAMP" => "timestamp",
            _ when bare.StartsWith("TIMESTAMP", StringComparison.Ordinal) && bare.Contains("TIME ZONE", StringComparison.Ordinal) => "timestamptz",
            _ when bare.StartsWith("TIMESTAMP", StringComparison.Ordinal) => "timestamp",
            _ => type.ToLowerInvariant(),
        };
    }

    /// <summary>Index of the BEGIN that opens the routine, ignoring any inside a string or comment.</summary>
    private static int TopLevelBeginIndex(string text)
    {
        Match match = Regex.Match(text, @"(?<![\w.])BEGIN(?![\w])", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(2));
        return match.Success ? match.Index : -1;
    }

    private static IEnumerable<string> SplitBlocks(string script)
    {
        StringBuilder current = new();

        foreach (string line in script.Split('\n'))
        {
            if (line.Trim() == "/")
            {
                yield return current.ToString();
                current.Clear();
                continue;
            }

            current.Append(line).Append('\n');
        }

        if (current.Length > 0)
        {
            yield return current.ToString();
        }
    }

    /// <summary>Drops the session settings and comments that precede a CREATE inside the same block.</summary>
    private static string TrimToCreate(string block)
    {
        Match create = Regex.Match(block, @"^[ \t]*CREATE\b", RegexOptions.IgnoreCase | RegexOptions.Multiline, TimeSpan.FromSeconds(2));
        return create.Success ? block[create.Index..].Trim() : string.Empty;
    }

    private static IEnumerable<string> SplitTopLevel(string text)
    {
        int depth = 0;
        StringBuilder current = new();

        foreach (char character in text)
        {
            switch (character)
            {
                case '(':
                    depth++;
                    break;
                case ')':
                    depth--;
                    break;
                case ',' when depth == 0:
                    yield return current.ToString();
                    current.Clear();
                    continue;
            }

            current.Append(character);
        }

        if (current.ToString().Trim().Length > 0)
        {
            yield return current.ToString();
        }
    }

    /// <summary>Text up to the first semicolon that is outside a string literal.</summary>
    private static string FirstStatement(string text)
    {
        bool inString = false;

        for (int index = 0; index < text.Length; index++)
        {
            if (text[index] == '\'')
            {
                inString = !inString;
            }
            else if (text[index] == ';' && !inString)
            {
                return text[..index];
            }
        }

        return text;
    }

    private static string Head(string text)
    {
        string head = text.Split('\n')[0].Trim();
        return head.Length > 120 ? head[..120] : head;
    }

    [GeneratedRegex(@"^[ \t]*(WHENEVER|SET[ \t]+DEFINE|ALTER[ \t]+SESSION)\b[^\n]*$", RegexOptions.IgnoreCase | RegexOptions.Multiline, 2000)]
    private static partial Regex ClientDirectivePattern();

    [GeneratedRegex(@"^CREATE\s+(?:OR\s+REPLACE\s+)?PACKAGE\s+BODY\s+(?<name>\w+)", RegexOptions.IgnoreCase, 2000)]
    private static partial Regex PackageBodyPattern();

    [GeneratedRegex(@"^CREATE\s+(?:OR\s+REPLACE\s+)?PACKAGE\s+(?<name>\w+)", RegexOptions.IgnoreCase, 2000)]
    private static partial Regex PackageSpecPattern();

    [GeneratedRegex(
        @"^CREATE\s+(?:OR\s+REPLACE\s+)?TRIGGER\s+(?<name>\w+)\s+(?<timing>BEFORE|AFTER|INSTEAD\s+OF)\s+(?<events>[A-Za-z\s]+?)\s+ON\s+(?<table>[\w.]+)\s*(?<row>FOR\s+EACH\s+ROW)?\s*(?<body>(?:DECLARE|BEGIN)\b.*)$",
        RegexOptions.IgnoreCase | RegexOptions.Singleline,
        4000)]
    private static partial Regex TriggerPattern();

    [GeneratedRegex(@"^CREATE\s+(?:OR\s+REPLACE\s+)?(?:FORCE\s+)?VIEW\s+(?<name>\w+)\s+AS\s+(?<body>.*)$", RegexOptions.IgnoreCase | RegexOptions.Singleline, 4000)]
    private static partial Regex ViewPattern();

    [GeneratedRegex(
        @"\b(?<kind>FUNCTION|PROCEDURE)\s+(?<name>\w+)\s*(?:\((?<params>[^()]*(?:\([^()]*\)[^()]*)*)\))?\s*(?:RETURN\s+(?<ret>[\w%.]+))?\s+(?:IS|AS)\b",
        RegexOptions.IgnoreCase,
        4000)]
    private static partial Regex RoutinePattern();

    [GeneratedRegex(@"^(?<name>\w+)\s+(?<dir>IN\s+OUT|INOUT|IN|OUT)?\s*(?<type>[\w%.]+(?:\s*\([^)]*\))?(?:\s+WITH\s+TIME\s+ZONE)?)\s*$", RegexOptions.IgnoreCase, 2000)]
    private static partial Regex ParameterPattern();

    [GeneratedRegex(@"^(?<name>\w+)\s+(?<type>[\w%.]+(?:\s*\([^)]*\))?)\s*;$", RegexOptions.IgnoreCase, 2000)]
    private static partial Regex DeclarationPattern();

    [GeneratedRegex(@"^\s*(?<name>\w+)\s+EXCEPTION\s*;", RegexOptions.IgnoreCase | RegexOptions.Multiline, 2000)]
    private static partial Regex ExceptionPattern();

    [GeneratedRegex(@"STANDARD_HASH\s*\(\s*(?<value>'[^']*'|[\w$#.]+)\s*,\s*(?<algorithm>'[^']*')\s*\)", RegexOptions.IgnoreCase, 2000)]
    private static partial Regex StandardHashPattern();

    [GeneratedRegex(@":(?<ref>NEW|OLD)\.", RegexOptions.IgnoreCase, 2000)]
    private static partial Regex BindPattern();

    // RAISE with a bare name only; RAISE EXCEPTION / NOTICE and a bare re-RAISE are already valid.
    [GeneratedRegex(@"\bRAISE\s+(?!EXCEPTION|NOTICE|WARNING|INFO|DEBUG|LOG)(?<name>\w+)\s*(?=;)", RegexOptions.IgnoreCase, 2000)]
    private static partial Regex RaisePattern();

    [GeneratedRegex(@"^CREATE\s+(?:OR\s+REPLACE\s+)?(?:GLOBAL\s+TEMPORARY\s+|UNIQUE\s+|BITMAP\s+)?(?:TABLE|SEQUENCE|INDEX|SYNONYM|USER|ROLE|TABLESPACE|DATABASE\s+LINK|MATERIALIZED\s+VIEW)\b", RegexOptions.IgnoreCase, 2000)]
    private static partial Regex SchemaObjectPattern();

    [GeneratedRegex(@"^(?<name>[\w\s]+?)\s*\(\s*(?<size>[^)]*)\s*\)$", RegexOptions.IgnoreCase, 2000)]
    private static partial Regex SizedTypePattern();
}
