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

    /// <summary>Marks where translated program units begin, so they can be applied apart from the tables.</summary>
    public const string ProgramUnitsMarker = "-- Program units translated from PL/SQL.";

    /// <summary>
    /// Constructs with no PostgreSQL equivalent. A routine containing one is not emitted at all.
    ///
    /// Emitting it anyway does not degrade gracefully: the CREATE fails, and because the schema is applied
    /// as a unit that failure blocks the data load behind it. Refusing one routine costs a line on the
    /// remediation list; emitting it costs the whole migration.
    /// </summary>
    private static readonly (string Token, string Reason)[] s_untranslatable =
    [
        ("RAISE_APPLICATION_ERROR", "Only the exact Oracle built-in call with a literal error number is translated; other forms require manual review."),
        ("PRAGMA", "A PRAGMA such as AUTONOMOUS_TRANSACTION has no PostgreSQL equivalent; autonomous work needs a separate connection or dblink."),
        ("REF CURSOR", "A REF CURSOR declaration has no direct equivalent in PL/pgSQL."),
        ("DBMS_", "An Oracle DBMS_ built-in package was called and has no PostgreSQL equivalent."),
        ("UTL_FILE", "UTL_FILE reads and writes server files; PostgreSQL has no equivalent available to a plain function."),
        ("UTL_MAIL", "UTL_MAIL sends mail from the database; PostgreSQL has no equivalent."),
        ("UTL_HTTP", "UTL_HTTP makes outbound calls from the database; PostgreSQL has no equivalent."),
        ("UTL_SMTP", "UTL_SMTP sends mail from the database; PostgreSQL has no equivalent available to a plain function."),
        ("UTL_TCP", "UTL_TCP opens network connections from the database; PostgreSQL has no equivalent available to a plain function."),
        ("UTL_RAW", "UTL_RAW operates on Oracle RAW values and must be mapped with the surrounding cryptographic or network API."),
        ("EXECUTE IMMEDIATE", "Dynamic SQL differs in binding and privilege handling; it is not translated automatically."),
        ("CONNECT BY", "Oracle hierarchical queries require a recursive PostgreSQL CTE and are not translated automatically."),
        ("VALUE_ERROR", "VALUE_ERROR is an Oracle predefined exception with no PostgreSQL condition of that name."),
        ("BULK COLLECT", "BULK COLLECT has no PL/pgSQL equivalent; the set has to be handled differently."),
        ("FORALL", "FORALL has no PL/pgSQL equivalent."),
        ("SQL%ROWCOUNT", "SQL%ROWCOUNT is only translated in a direct assignment to a variable; other uses require GET DIAGNOSTICS and control-flow changes."),
        ("%NOTFOUND", "Explicit cursor attributes differ in PL/pgSQL and are not translated automatically."),
        ("%ISOPEN", "Explicit cursor attributes differ in PL/pgSQL and are not translated automatically."),
    ];

    /// <summary>The first construct in <paramref name="body"/> that has no PostgreSQL equivalent.</summary>
    private static (string Token, string Reason)? Untranslatable(string body)
    {
        string executable = MaskNonCode(body);

        foreach (Match cursor in OpenCursorPattern().Matches(executable))
        {
            string query = cursor.Groups["query"].Value;
            if (!query.Equals("SELECT", StringComparison.OrdinalIgnoreCase)
                && !query.Equals("WITH", StringComparison.OrdinalIgnoreCase))
            {
                return ("OPEN FOR dynamic SQL", "A cursor opened from a string expression must be parameterized and reviewed; emitting it would carry SQL injection into the target.");
            }
        }

        if (!AllDualOccurrencesAreRemovable(body, executable))
        {
            return ("FROM DUAL", "Only an unaliased terminal FROM DUAL can be removed mechanically; this form must be rewritten explicitly.");
        }

        if (QualifiedOracleBuiltinPattern().IsMatch(executable)
            || QualifiedSequenceValuePattern().IsMatch(executable))
        {
            return ("qualified Oracle built-in", "A schema, package, or record qualifier changes how this token must be resolved, so it was not rewritten automatically.");
        }

        if (!AllCallsAreRewritable(body, executable, RaiseApplicationErrorStartPattern(), expectedArguments: 2))
        {
            return ("RAISE_APPLICATION_ERROR", "The call shape could not be parsed safely, so it was not translated automatically.");
        }

        if (!AllRaiseApplicationErrorCodesAreLiteral(body, executable))
        {
            return ("RAISE_APPLICATION_ERROR", "Only a literal Oracle error number can be retained faithfully in PostgreSQL DETAIL; a computed error code requires a manual rewrite.");
        }

        if (!AllCallsAreRewritable(body, executable, DbmsOutputStartPattern(), expectedArguments: 1))
        {
            return ("DBMS_OUTPUT", "The call shape could not be parsed safely, so it was not translated automatically.");
        }

        executable = RaiseApplicationErrorStartPattern().Replace(executable, string.Empty);
        executable = DbmsOutputStartPattern().Replace(executable, string.Empty);
        executable = SqlRowCountPattern().Replace(executable, string.Empty);

        foreach ((string token, string reason) in s_untranslatable)
        {
            if (executable.Contains(token, StringComparison.OrdinalIgnoreCase))
            {
                return (token, reason);
            }
        }

        return null;
    }

    public static PlSqlTranslation Translate(string? oracleScript)
    {
        List<PlSqlUnit> units = [];
        List<ConversionFinding> findings = [];

        string script = (oracleScript ?? string.Empty).Replace("\r\n", "\n", StringComparison.Ordinal);
        script = ClientDirectivePattern().Replace(script, string.Empty);
        List<string> blocks = [.. SplitBlocks(script)];
        IReadOnlyDictionary<string, IReadOnlySet<string>> refCursorTypes = CollectRefCursorTypes(blocks);

        foreach (string block in blocks)
        {
            string trimmed = TrimToCreate(block);
            if (trimmed.Length == 0)
            {
                continue;
            }

            if (PackageBodyPattern().Match(trimmed) is { Success: true } body)
            {
                TranslatePackageBody(
                    body.Groups["name"].Value,
                    trimmed,
                    refCursorTypes,
                    units,
                    findings);
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

        return new PlSqlTranslation(Distinct(units, findings), findings);
    }

    private static IReadOnlyDictionary<string, IReadOnlySet<string>> CollectRefCursorTypes(IEnumerable<string> blocks)
    {
        Dictionary<string, HashSet<string>> types = new(StringComparer.OrdinalIgnoreCase);

        foreach (string block in blocks)
        {
            string trimmed = TrimToCreate(block);
            Match package = PackageBodyPattern().Match(trimmed);
            bool isBody = package.Success;
            if (!package.Success)
            {
                package = PackageSpecPattern().Match(trimmed);
            }

            if (!package.Success)
            {
                continue;
            }

            string owner = package.Groups["name"].Value;
            if (!types.TryGetValue(owner, out HashSet<string>? aliases))
            {
                aliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                types.Add(owner, aliases);
            }

            string declarationRegion = MaskNonCode(trimmed);
            if (isBody && RoutinePattern().Match(declarationRegion) is { Success: true } firstRoutine)
            {
                declarationRegion = declarationRegion[..firstRoutine.Index];
            }

            foreach (Match type in RefCursorTypePattern().Matches(declarationRegion))
            {
                aliases.Add(type.Groups["name"].Value);
            }
        }

        return types.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlySet<string>)pair.Value,
            StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Drops a unit whose name is already taken, and says which.
    ///
    /// Everything is emitted as CREATE OR REPLACE, so two units sharing a name would not error: the second
    /// would silently replace the first and a routine would vanish from the target with nothing to show for it.
    /// </summary>
    private static IReadOnlyList<PlSqlUnit> Distinct(List<PlSqlUnit> units, List<ConversionFinding> findings)
    {
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        List<PlSqlUnit> kept = [];

        foreach (PlSqlUnit unit in units)
        {
            if (unit.Sql.Length > 0 && !seen.Add($"{unit.Kind}:{unit.Name}"))
            {
                findings.Add(new ConversionFinding(
                    ConversionSeverity.Unsupported,
                    "Program unit",
                    unit.Name,
                    "A second program unit translated to this same name. It was not emitted, because CREATE OR " +
                    "REPLACE would have silently replaced the first one."));
                continue;
            }

            kept.Add(unit);
        }

        return kept;
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
        builder.Append('\n').Append(ProgramUnitsMarker).Append(" Translated, not verified: no test has been\n");
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
        string package,
        string block,
        IReadOnlyDictionary<string, IReadOnlySet<string>> refCursorTypes,
        List<PlSqlUnit> units,
        List<ConversionFinding> findings)
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

            if (Untranslatable(inner) is { } blocker)
            {
                findings.Add(new ConversionFinding(
                    ConversionSeverity.Unsupported,
                    "Program unit",
                    $"{package}.{name}",
                    $"Not translated because of {blocker.Token}. {blocker.Reason}"));
                continue;
            }

            string executable = MaskNonCode(inner);
            if (RaiseApplicationErrorStartPattern().IsMatch(executable))
            {
                findings.Add(new ConversionFinding(
                    ConversionSeverity.ManualReview,
                    "Program unit",
                    $"{package}.{name}",
                    "RAISE_APPLICATION_ERROR became RAISE EXCEPTION with SQLSTATE P0001. The Oracle error number is retained in DETAIL, but callers that branch on the numeric Oracle code must change."));
            }

            if (DbmsOutputStartPattern().IsMatch(executable))
            {
                findings.Add(new ConversionFinding(
                    ConversionSeverity.ManualReview,
                    "Program unit",
                    $"{package}.{name}",
                    "DBMS_OUTPUT.PUT_LINE became RAISE NOTICE. PostgreSQL clients receive notices separately from result rows, so callers that consume DBMS_OUTPUT must change."));
            }

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

            if (!TryRewriteDeclarations(
                    inner[..begin],
                    $"{package}.{name}",
                    package,
                    refCursorTypes,
                    findings,
                    out string declarations))
            {
                continue;
            }

            string body = Rewrite(inner[(begin + 5)..]).Trim();

            if (!TryTranslateParameters(
                    routine.Groups["params"].Value,
                    $"{package}.{name}",
                    package,
                    refCursorTypes,
                    findings,
                    out string parameters))
            {
                continue;
            }

            string signature = $"{prefix}{name.ToLowerInvariant()}({parameters})";

            StringBuilder sql = new();
            sql.Append(isFunction ? "CREATE OR REPLACE FUNCTION " : "CREATE OR REPLACE PROCEDURE ").Append(signature).Append('\n');

            if (isFunction)
            {
                string returnType = routine.Groups["ret"].Value;
                if (!TryMapType(returnType, package, refCursorTypes, out string mappedReturnType))
                {
                    findings.Add(new ConversionFinding(
                        ConversionSeverity.Unsupported,
                        "Program unit",
                        $"{package}.{name}",
                        $"The return type '{returnType}' is an unresolved cursor type, so the routine was not emitted."));
                    continue;
                }

                sql.Append("RETURNS ").Append(mappedReturnType).Append('\n');
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

        if (Untranslatable(raw) is { } blocker)
        {
            findings.Add(new ConversionFinding(
                ConversionSeverity.Unsupported, "Program unit", $"TRIGGER {name}",
                $"Not translated because of {blocker.Token}. {blocker.Reason}"));
            return;
        }

        int begin = TopLevelBeginIndex(raw);
        if (begin < 0)
        {
            findings.Add(new ConversionFinding(
                ConversionSeverity.Unsupported, "Program unit", $"TRIGGER {name}",
                "No BEGIN was found in the trigger body, so nothing was emitted."));
            return;
        }

        if (!TryRewriteDeclarations(
                raw[..begin],
                $"TRIGGER {name}",
                null,
                new Dictionary<string, IReadOnlySet<string>>(),
                findings,
                out string declarations))
        {
            return;
        }

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

    private static bool TryTranslateParameters(
        string parameters,
        string owner,
        string package,
        IReadOnlyDictionary<string, IReadOnlySet<string>> refCursorTypes,
        List<ConversionFinding> findings,
        out string result)
    {
        if (string.IsNullOrWhiteSpace(parameters))
        {
            result = string.Empty;
            return true;
        }

        parameters = RewriteAlternativeQuotedLiterals(parameters);
        List<string> translated = [];
    bool optionalInputSeen = false;
        foreach (string parameter in SplitTopLevel(parameters))
        {
            Match match = ParameterPattern().Match(parameter.Trim());
            if (!match.Success)
            {
                findings.Add(new ConversionFinding(
                    ConversionSeverity.Unsupported, "Program unit", owner,
                    $"The parameter '{parameter.Trim()}' was not understood, so the routine was not emitted."));
                result = string.Empty;
                return false;
            }

            string direction = Regex.Replace(match.Groups["dir"].Value.Trim(), @"\s+", " ", RegexOptions.None, TimeSpan.FromSeconds(1)).ToUpperInvariant();
            string mode = direction switch
            {
                "OUT" => "OUT ",
                "IN OUT" or "INOUT" => "INOUT ",
                _ => string.Empty,
            };

            bool hasDefault = match.Groups["default"].Success;
            bool isInput = direction is "" or "IN";
            if (hasDefault && !isInput)
            {
                findings.Add(new ConversionFinding(
                    ConversionSeverity.Unsupported, "Program unit", owner,
                    $"The {direction} parameter '{match.Groups["name"].Value}' has a default that PostgreSQL cannot represent, so the routine was not emitted."));
                result = string.Empty;
                return false;
            }

            if (isInput && !hasDefault && optionalInputSeen)
            {
                findings.Add(new ConversionFinding(
                    ConversionSeverity.Unsupported, "Program unit", owner,
                    $"The required parameter '{match.Groups["name"].Value}' follows an optional parameter. PostgreSQL cannot preserve that positional signature, so the routine was not emitted."));
                result = string.Empty;
                return false;
            }

            optionalInputSeen |= isInput && hasDefault;

            string sourceType = match.Groups["type"].Value;
            if (!TryMapType(sourceType, package, refCursorTypes, out string mappedType))
            {
                findings.Add(new ConversionFinding(
                    ConversionSeverity.Unsupported, "Program unit", owner,
                    $"The parameter type '{sourceType}' is an unresolved cursor type, so the routine was not emitted."));
                result = string.Empty;
                return false;
            }

            string defaultValue = hasDefault
                ? $" DEFAULT {Rewrite(match.Groups["default"].Value.Trim())}"
                : string.Empty;

            translated.Add(
                $"{mode}{match.Groups["name"].Value.ToLowerInvariant()} " +
                $"{mappedType}{defaultValue}");
        }

        result = string.Join(", ", translated);
        return true;
    }

    private static bool TryRewriteDeclarations(
        string declarations,
        string owner,
        string? package,
        IReadOnlyDictionary<string, IReadOnlySet<string>> refCursorTypes,
        List<ConversionFinding> findings,
        out string result)
    {
        StringBuilder builder = new();

        foreach (string statement in SplitDeclarationStatements(declarations))
        {
            string trimmed = RemoveCommentOnlyLines(statement).Trim();
            if (trimmed.Length == 0)
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
                findings.Add(new ConversionFinding(
                    ConversionSeverity.Unsupported, "Program unit", owner,
                    $"The declaration '{trimmed.ReplaceLineEndings(" ")}' was not understood, so the routine was not emitted."));
                result = string.Empty;
                return false;
            }

            string type = declaration.Groups["type"].Value.Trim();
            if (type.Contains('%', StringComparison.Ordinal))
            {
                findings.Add(new ConversionFinding(
                    ConversionSeverity.ManualReview, "Program unit", owner,
                    $"'{trimmed}' was carried over as a PostgreSQL row type. PostgreSQL anchors the type the same way " +
                    "but differs on NULL and field assignment, so any logic that depends on that must be re-verified."));
            }

            if (!TryMapType(type, package, refCursorTypes, out string mappedType))
            {
                findings.Add(new ConversionFinding(
                    ConversionSeverity.Unsupported, "Program unit", owner,
                    $"The declaration type '{type}' is an unresolved cursor type, so the routine was not emitted."));
                result = string.Empty;
                return false;
            }

            builder.Append("    ")
                   .Append(declaration.Groups["name"].Value.ToLowerInvariant())
                   .Append(' ')
                   .Append(type.Contains('%', StringComparison.Ordinal) ? type.ToLowerInvariant() : mappedType);

            if (declaration.Groups["default"].Success)
            {
                builder.Append(" := ").Append(Rewrite(declaration.Groups["default"].Value.Trim()));
            }

            builder
                   .Append(";\n");
        }

        result = builder.ToString();
        return true;
    }

    private static string Rewrite(string sql)
    {
        string result = RewriteAlternativeQuotedLiterals(sql);
        result = RewriteCalls(result, RaiseApplicationErrorStartPattern(), arguments =>
            $"RAISE EXCEPTION USING MESSAGE = {arguments[1].Trim()}, " +
            $"ERRCODE = 'P0001', DETAIL = 'Oracle error {arguments[0].Trim()}' ;");
        result = RewriteCalls(result, DbmsOutputStartPattern(), arguments =>
            $"RAISE NOTICE '%', {arguments[0].Trim()};");
        result = ReplaceCodeMatches(result, StandardHashPattern(), match =>
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

        result = ReplaceCodeMatches(result, BindPattern(), match => match.Groups["ref"].Value.ToUpperInvariant() + ".");
        result = ReplaceCodeMatches(result, NvlPattern(), _ => "COALESCE(");
        result = ReplaceCodeMatches(result, SystimestampPattern(), _ => "now()");
        result = ReplaceCodeMatches(result, SysdatePattern(), _ => "CURRENT_DATE");
        result = ReplaceCodeMatches(result, UserPattern(), _ => "CURRENT_USER");
        result = ReplaceCodeMatches(result, SequenceNextvalPattern(),
            match => $"nextval('{match.Groups["seq"].Value.ToLowerInvariant()}')");
        result = ReplaceCodeMatches(result, SequenceCurrvalPattern(),
            match => $"currval('{match.Groups["seq"].Value.ToLowerInvariant()}')");
        result = ReplaceCodeMatches(result, SqlRowCountPattern(), match =>
            $"GET DIAGNOSTICS {match.Groups["name"].Value.ToLowerInvariant()} = ROW_COUNT;");
        result = ReplaceCodeMatches(result, FromDualPattern(), _ => string.Empty);

        // PostgreSQL has no user-declared exception names, so the name is carried as the message instead.
        result = ReplaceCodeMatches(
            result,
            RaisePattern(),
            match => $"RAISE EXCEPTION '{match.Groups["name"].Value.ToLowerInvariant()}'");

        return result;
    }

    private static bool AllDualOccurrencesAreRemovable(string source, string mask)
    {
        foreach (Match dual in FromDualTokenPattern().Matches(mask))
        {
            int index = dual.Index + dual.Length;
            while (index < source.Length && char.IsWhiteSpace(source[index]))
            {
                index++;
            }

            if (index >= source.Length || source[index] != ';')
            {
                return false;
            }
        }

        return true;
    }

    private static bool AllCallsAreRewritable(
        string source,
        string mask,
        Regex startPattern,
        int expectedArguments)
    {
        foreach (Match start in startPattern.Matches(mask))
        {
            if (!TryReadCall(source, mask, start, out _, out IReadOnlyList<string> arguments)
                || arguments.Count != expectedArguments)
            {
                return false;
            }
        }

        return true;
    }

    private static bool AllRaiseApplicationErrorCodesAreLiteral(string source, string mask)
    {
        foreach (Match start in RaiseApplicationErrorStartPattern().Matches(mask))
        {
            if (!TryReadCall(source, mask, start, out _, out IReadOnlyList<string> arguments)
                || arguments.Count != 2
                || !OracleErrorCodePattern().IsMatch(arguments[0].Trim()))
            {
                return false;
            }
        }

        return true;
    }

    private static string RewriteCalls(
        string source,
        Regex startPattern,
        Func<IReadOnlyList<string>, string> replacement)
    {
        string mask = MaskNonCode(source);
        StringBuilder builder = new(source);

        foreach (Match start in startPattern.Matches(mask).Cast<Match>().Reverse())
        {
            if (TryReadCall(source, mask, start, out int end, out IReadOnlyList<string> arguments))
            {
                builder.Remove(start.Index, end - start.Index)
                       .Insert(start.Index, replacement(arguments));
            }
        }

        return builder.ToString();
    }

    private static bool TryReadCall(
        string source,
        string mask,
        Match start,
        out int end,
        out IReadOnlyList<string> arguments)
    {
        int open = mask.IndexOf('(', start.Index, start.Length);
        int depth = 0;

        for (int index = open; index < mask.Length; index++)
        {
            if (mask[index] == '(')
            {
                depth++;
            }
            else if (mask[index] == ')' && --depth == 0)
            {
                int terminator = index + 1;
                while (terminator < source.Length && char.IsWhiteSpace(source[terminator]))
                {
                    terminator++;
                }

                if (terminator >= source.Length || source[terminator] != ';')
                {
                    break;
                }

                string argumentText = RewriteAlternativeQuotedLiterals(source[(open + 1)..index]);
                arguments = [.. SplitTopLevel(argumentText)];
                end = terminator + 1;
                return true;
            }
        }

        end = start.Index;
        arguments = [];
        return false;
    }

    private static string RewriteAlternativeQuotedLiterals(string source)
    {
        string mask = MaskNonCode(source);
        StringBuilder builder = new(source);

        for (int index = source.Length - 3; index >= 0; index--)
        {
            if (mask[index] == ' ' || (source[index] is not 'q' and not 'Q') || source[index + 1] != '\'')
            {
                continue;
            }

            char opener = source[index + 2];
            char closer = opener switch { '[' => ']', '(' => ')', '{' => '}', '<' => '>', _ => opener };
            int close = source.IndexOf($"{closer}'", index + 3, StringComparison.Ordinal);
            if (close < 0)
            {
                continue;
            }

            string content = source[(index + 3)..close].Replace("'", "''", StringComparison.Ordinal);
            builder.Remove(index, close + 2 - index).Insert(index, $"'{content}'");
        }

        return builder.ToString();
    }

    private static string ReplaceCodeMatches(string text, Regex pattern, MatchEvaluator evaluator)
    {
        string mask = MaskNonCode(text);
        MatchCollection matches = pattern.Matches(text);
        StringBuilder builder = new(text);

        foreach (Match match in matches.Cast<Match>().Reverse())
        {
            int token = match.Index;
            while (token < match.Index + match.Length && char.IsWhiteSpace(text[token]))
            {
                token++;
            }

            if (token < mask.Length && mask[token] != ' ')
            {
                builder.Remove(match.Index, match.Length).Insert(match.Index, evaluator(match));
            }
        }

        return builder.ToString();
    }

    private static bool TryMapType(
        string oracleType,
        string? package,
        IReadOnlyDictionary<string, IReadOnlySet<string>> refCursorTypes,
        out string mappedType)
    {
        string type = oracleType.Trim();
        if (type.Length == 0)
        {
            mappedType = "void";
            return true;
        }

        string[] qualified = type.Split('.', 2);
        bool isCursorAlias = qualified.Length == 2
            ? refCursorTypes.TryGetValue(qualified[0], out IReadOnlySet<string>? qualifiedTypes)
              && qualifiedTypes.Contains(qualified[1])
            : package is not null
              && refCursorTypes.TryGetValue(package, out IReadOnlySet<string>? localTypes)
              && localTypes.Contains(type);

        Match sized = SizedTypePattern().Match(type);
        string bare = (sized.Success ? sized.Groups["name"].Value : type).ToUpperInvariant();
        string size = sized.Success ? sized.Groups["size"].Value : string.Empty;

        bool knownType = true;
        mappedType = bare switch
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
            "SYS_REFCURSOR" or "REFCURSOR" => "refcursor",
            _ when isCursorAlias => "refcursor",
            _ when bare.StartsWith("TIMESTAMP", StringComparison.Ordinal) && bare.Contains("TIME ZONE", StringComparison.Ordinal) => "timestamptz",
            _ when bare.StartsWith("TIMESTAMP", StringComparison.Ordinal) => "timestamp",
            _ when type.Contains('%', StringComparison.Ordinal) => type.ToLowerInvariant(),
            _ => UnknownType(),
        };

        return knownType;

        string UnknownType()
        {
            knownType = false;
            return type.ToLowerInvariant();
        }
    }

    private static string MaskNonCode(string text)
    {
        char[] masked = text.ToCharArray();
        bool inString = false;
        bool inLineComment = false;
        bool inBlockComment = false;
        char alternativeQuoteEnd = '\0';

        for (int index = 0; index < masked.Length; index++)
        {
            char current = masked[index];
            char next = index + 1 < masked.Length ? masked[index + 1] : '\0';

            if (alternativeQuoteEnd != '\0')
            {
                masked[index] = ' ';
                if (current == alternativeQuoteEnd && next == '\'')
                {
                    masked[++index] = ' ';
                    alternativeQuoteEnd = '\0';
                }
                continue;
            }

            if (inLineComment)
            {
                if (current == '\n')
                {
                    inLineComment = false;
                }
                else
                {
                    masked[index] = ' ';
                }
                continue;
            }

            if (inBlockComment)
            {
                masked[index] = ' ';
                if (current == '*' && next == '/')
                {
                    masked[++index] = ' ';
                    inBlockComment = false;
                }
                continue;
            }

            if (inString)
            {
                masked[index] = ' ';
                if (current == '\'' && next == '\'')
                {
                    masked[++index] = ' ';
                }
                else if (current == '\'')
                {
                    inString = false;
                }
                continue;
            }

            if ((current == 'q' || current == 'Q') && next == '\'' && index + 2 < masked.Length)
            {
                char opener = masked[index + 2];
                alternativeQuoteEnd = opener switch
                {
                    '[' => ']',
                    '(' => ')',
                    '{' => '}',
                    '<' => '>',
                    _ => opener,
                };
                masked[++index] = masked[++index] = ' ';
            }
            else if (current == '\'')
            {
                masked[index] = ' ';
                inString = true;
            }
            else if (current == '-' && next == '-')
            {
                masked[index] = masked[++index] = ' ';
                inLineComment = true;
            }
            else if (current == '/' && next == '*')
            {
                masked[index] = masked[++index] = ' ';
                inBlockComment = true;
            }
        }

        return new string(masked);
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
        bool inString = false;
        StringBuilder current = new();

        for (int index = 0; index < text.Length; index++)
        {
            char character = text[index];
            char next = index + 1 < text.Length ? text[index + 1] : '\0';

            if (character == '\'')
            {
                current.Append(character);
                if (inString && next == '\'')
                {
                    current.Append(next);
                    index++;
                    continue;
                }

                inString = !inString;
                continue;
            }

            if (inString)
            {
                current.Append(character);
                continue;
            }

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

    private static IEnumerable<string> SplitDeclarationStatements(string declarations)
    {
        string mask = MaskNonCode(declarations);
        int start = 0;

        for (int index = 0; index < mask.Length; index++)
        {
            if (mask[index] != ';')
            {
                continue;
            }

            yield return declarations[start..(index + 1)];
            start = index + 1;
        }

        if (declarations[start..].Trim().Length > 0)
        {
            yield return declarations[start..];
        }
    }

    private static string RemoveCommentOnlyLines(string text) =>
        string.Join('\n', text.Split('\n').Where(line => !line.TrimStart().StartsWith("--", StringComparison.Ordinal)));

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

    [GeneratedRegex(@"^CREATE\s+(?:OR\s+REPLACE\s+)?PACKAGE\s+BODY\s+(?:[\w$#]+\s*\.\s*)?(?<name>[\w$#]+)", RegexOptions.IgnoreCase, 2000)]
    private static partial Regex PackageBodyPattern();

    [GeneratedRegex(@"^CREATE\s+(?:OR\s+REPLACE\s+)?PACKAGE\s+(?:[\w$#]+\s*\.\s*)?(?<name>[\w$#]+)", RegexOptions.IgnoreCase, 2000)]
    private static partial Regex PackageSpecPattern();

    [GeneratedRegex(
        @"^CREATE\s+(?:OR\s+REPLACE\s+)?TRIGGER\s+(?:[\w$#]+\s*\.\s*)?(?<name>[\w$#]+)\s+(?<timing>BEFORE|AFTER|INSTEAD\s+OF)\s+(?<events>[A-Za-z\s]+?)\s+ON\s+(?<table>[\w.]+)\s*(?<row>FOR\s+EACH\s+ROW)?\s*(?<body>(?:DECLARE|BEGIN)\b.*)$",
        RegexOptions.IgnoreCase | RegexOptions.Singleline,
        4000)]
    private static partial Regex TriggerPattern();

    [GeneratedRegex(@"^CREATE\s+(?:OR\s+REPLACE\s+)?(?:FORCE\s+)?VIEW\s+(?:[\w$#]+\s*\.\s*)?(?<name>[\w$#]+)\s+AS\s+(?<body>.*)$", RegexOptions.IgnoreCase | RegexOptions.Singleline, 4000)]
    private static partial Regex ViewPattern();

    [GeneratedRegex(
        @"\b(?<kind>FUNCTION|PROCEDURE)\s+(?<name>\w+)\s*(?:\((?<params>[^()]*(?:\([^()]*\)[^()]*)*)\))?\s*(?:RETURN\s+(?<ret>[\w%.]+))?\s+(?:IS|AS)\b",
        RegexOptions.IgnoreCase,
        4000)]
    private static partial Regex RoutinePattern();

    [GeneratedRegex(@"^(?<name>\w+)\s+(?<dir>IN\s+OUT|INOUT|IN|OUT)?\s*(?<type>[\w%.]+(?:\s*\([^)]*\))?(?:\s+WITH\s+TIME\s+ZONE)?)(?:\s+(?:DEFAULT|:=)\s+(?<default>.+?))?\s*$", RegexOptions.IgnoreCase, 2000)]
    private static partial Regex ParameterPattern();

    [GeneratedRegex(@"^(?<name>\w+)\s+(?<type>[\w%.]+(?:\s*\([^)]*\))?)(?:\s*(?::=|DEFAULT)\s*(?<default>.*?))?\s*;$", RegexOptions.IgnoreCase | RegexOptions.Singleline, 2000)]
    private static partial Regex DeclarationPattern();

    [GeneratedRegex(@"^\s*(?<name>\w+)\s+EXCEPTION\s*;", RegexOptions.IgnoreCase | RegexOptions.Multiline, 2000)]
    private static partial Regex ExceptionPattern();

    [GeneratedRegex(@"\bTYPE\s+(?<name>[\w$#]+)\s+IS\s+REF\s+CURSOR\b", RegexOptions.IgnoreCase, 2000)]
    private static partial Regex RefCursorTypePattern();

    [GeneratedRegex(@"(?<![\w$#.])RAISE_APPLICATION_ERROR\s*\(", RegexOptions.IgnoreCase, 2000)]
    private static partial Regex RaiseApplicationErrorStartPattern();

    [GeneratedRegex(@"^-\d+$", RegexOptions.None, 2000)]
    private static partial Regex OracleErrorCodePattern();

    [GeneratedRegex(@"(?<![\w$#.])DBMS_OUTPUT\.PUT_LINE\s*\(", RegexOptions.IgnoreCase, 2000)]
    private static partial Regex DbmsOutputStartPattern();

    [GeneratedRegex(@"(?<![\w$#.])(?<name>\w+)\s*:=\s*SQL%ROWCOUNT\s*;", RegexOptions.IgnoreCase, 2000)]
    private static partial Regex SqlRowCountPattern();

    [GeneratedRegex(@"\s+FROM\s+(?:SYS\.)?DUAL(?=\s*;)", RegexOptions.IgnoreCase, 2000)]
    private static partial Regex FromDualPattern();

    [GeneratedRegex(@"\bFROM\s+(?:SYS\.)?DUAL\b", RegexOptions.IgnoreCase, 2000)]
    private static partial Regex FromDualTokenPattern();

    [GeneratedRegex(@"\bFROM\s+(?:SYS\.)?DUAL\b(?!\s*;)", RegexOptions.IgnoreCase, 2000)]
    private static partial Regex UnsupportedDualPattern();

    [GeneratedRegex(@"\bOPEN\s+[\w$#]+\s+FOR\s*(?<query>\S*)", RegexOptions.IgnoreCase, 2000)]
    private static partial Regex OpenCursorPattern();

    [GeneratedRegex(@"STANDARD_HASH\s*\(\s*(?<value>'[^']*'|[\w$#.]+)\s*,\s*(?<algorithm>'[^']*')\s*\)", RegexOptions.IgnoreCase, 2000)]
    private static partial Regex StandardHashPattern();

    [GeneratedRegex(@"(?<![\w$#.])NVL\s*\(", RegexOptions.IgnoreCase, 2000)]
    private static partial Regex NvlPattern();

    [GeneratedRegex(@"(?<![\w$#.])SYSTIMESTAMP\b", RegexOptions.IgnoreCase, 2000)]
    private static partial Regex SystimestampPattern();

    [GeneratedRegex(@"(?<![\w$#.])SYSDATE\b", RegexOptions.IgnoreCase, 2000)]
    private static partial Regex SysdatePattern();

    [GeneratedRegex(@"(?<![\w$#.])USER\b", RegexOptions.IgnoreCase, 2000)]
    private static partial Regex UserPattern();

    [GeneratedRegex(@"(?<![\w$#.])(?<seq>\w+)\.NEXTVAL\b", RegexOptions.IgnoreCase, 2000)]
    private static partial Regex SequenceNextvalPattern();

    [GeneratedRegex(@"(?<![\w$#.])(?<seq>\w+)\.CURRVAL\b", RegexOptions.IgnoreCase, 2000)]
    private static partial Regex SequenceCurrvalPattern();

    [GeneratedRegex(@"(?<![\w$#])(?:[\w$#]+\.)+(?:NVL\s*\(|SYSDATE\b|SYSTIMESTAMP\b|USER\b)", RegexOptions.IgnoreCase, 2000)]
    private static partial Regex QualifiedOracleBuiltinPattern();

    [GeneratedRegex(@"(?<![\w$#])(?:[\w$#]+\.){2,}(?:NEXTVAL|CURRVAL)\b", RegexOptions.IgnoreCase, 2000)]
    private static partial Regex QualifiedSequenceValuePattern();

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
