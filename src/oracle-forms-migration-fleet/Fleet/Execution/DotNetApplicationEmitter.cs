using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OracleFormsMigrationFleet.Fleet.Execution;

/// <summary>
/// Everything the .NET generators need, derived once from a validated mapping so no emitter re-decides a
/// name, a type, or a transform. Nothing here is read from an application name or a table name pattern.
/// </summary>
internal sealed record DotNetGenerationPlan(
    TargetMapping Mapping,
    string ApplicationName,
    IReadOnlyDictionary<string, string> TargetTypes,
    IReadOnlyList<DotNetTransformDecision> Decisions)
{
    public ResolvedObject Header => Mapping.Header;

    public ResolvedObject Detail => Mapping.Detail;

    public ResolvedObject Party => Mapping.Party;

    public ResolvedObject Item => Mapping.Item;

    public bool Optimistic => Mapping.ItemIsVersioned;

    /// <summary>The PostgreSQL type chosen for one parsed column. Every mapped table's column has one.</summary>
    public string TargetType(OracleTable table, OracleColumn column) => TargetTypes[Key(table, column)];

    public static string Key(OracleTable table, OracleColumn column) =>
        $"{table.Name.ToUpperInvariant()}.{column.Name.ToUpperInvariant()}";

    /// <summary>Quoted, lower-cased PostgreSQL identifier. Always quoted so no name needs a reserved-word list.</summary>
    public static string Sql(string name) => $"\"{name.ToLowerInvariant().Replace("\"", "\"\"", StringComparison.Ordinal)}\"";

    public string SqlTable(ResolvedObject entry) => Sql(entry.Table.Name);

    public string SqlColumn(ResolvedObject entry, MappedFieldRole role) => Sql(entry.Require(role).Column.Name);

    public string? SqlColumnIfBound(ResolvedObject entry, MappedFieldRole role) =>
        entry.Optional(role) is { } field ? Sql(field.Column.Name) : null;
}

/// <summary>One decision the generator made that the source did not state. Recorded so a ledger can bind it.</summary>
public sealed record DotNetTransformDecision(string Id, string Decision, string Rationale);

/// <summary>
/// Generates a React/TypeScript front end, an ASP.NET Core back end, and the PostgreSQL schema and routine
/// they run against, from a validated target-mapping manifest and the parsed Oracle schema.
///
/// The generator is role-driven. It has no knowledge of any application, table, or column name: every rule
/// it applies is written against a <see cref="MappedObjectRole"/> or a <see cref="MappedFieldRole"/> that
/// an operator bound, and <see cref="TargetMappingReader"/> refused the mapping unless the parsed schema
/// confirmed the type, nullability, key, and foreign key each role requires. Two estates that bind the same
/// roles to entirely different identifiers therefore take the same path through this code.
///
/// Pure: no file, process, network, clock, or random source. The same inputs produce byte-identical output.
/// Nothing generated here has been compiled or executed by this type, and it makes no claim that it works.
/// </summary>
public static class DotNetApplicationEmitter
{
    /// <summary>Version of the generated contract. Bumped when generated wire or SQL shapes change.</summary>
    public const string GeneratorVersion = "dotnet-master-detail/1";

    /// <summary>Schema version of the resolved manifest this generator writes beside the code.</summary>
    public const string ResolvedManifestSchemaVersion = "fleet.target-mapping-resolved/1";

    /// <summary>Name of the generated PostgreSQL routine. Role-derived and identical across estates.</summary>
    public const string RoutineName = "ofm_master_detail_place";

    /// <summary>SQLSTATE values the generated routine raises, and what the generated API turns each into.</summary>
    public static IReadOnlyList<(string SqlState, string Meaning, int HttpStatus)> RoutineErrors { get; } =
    [
        ("OFM01", "The party reference does not resolve to an available row.", 422),
        ("OFM02", "An item reference does not resolve to an available row.", 422),
        ("OFM03", "A submitted quantity is absent, non-integral, or not positive.", 422),
        ("OFM04", "An item has less stock on hand than the submitted quantity.", 409),
        ("OFM05", "An item row changed between read and write; the write was refused.", 409),
    ];

    public static ApplicationConversion Convert(
        OracleSchema schema,
        TargetMapping mapping,
        string applicationName,
        DatabaseTarget target)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(mapping);

        List<ConversionFinding> findings = [];

        if (target != DatabaseTarget.PostgreSql)
        {
            findings.Add(new ConversionFinding(
                ConversionSeverity.Unsupported,
                "Target",
                target.ToString(),
                "This generator emits PostgreSQL DDL, a PL/pgSQL routine, and Npgsql data access. Nothing was emitted " +
                "for another engine, because the generated SQL would not run on one."));

            return new ApplicationConversion([], findings);
        }

        Dictionary<string, string> types = [];
        foreach (ResolvedObject entry in mapping.Objects)
        {
            foreach (OracleColumn column in entry.Table.Columns)
            {
                if (PostgreSqlType(column) is not { } type)
                {
                    findings.Add(new ConversionFinding(
                        ConversionSeverity.Unsupported,
                        "Column type",
                        $"{entry.Table.Name}.{column.Name}",
                        $"'{column.RawType}' has no PostgreSQL type this generator will choose on its own. The mapped " +
                        "table cannot be created without it, so nothing was emitted rather than emitting a table that " +
                        "silently drops the column."));
                    continue;
                }

                types[DotNetGenerationPlan.Key(entry.Table, column)] = type;
            }
        }

        foreach (ResolvedObject entry in mapping.Objects)
        {
            OracleColumn[] unmapped =
            [
                .. entry.Table.Columns.Where(column => entry.Fields.All(field =>
                    !string.Equals(field.Column.Name, column.Name, StringComparison.OrdinalIgnoreCase))),
            ];

            foreach (OracleColumn column in unmapped.Where(column => column.NotNull))
            {
                // A NOT NULL column nothing is bound to is satisfiable only by its own default, because
                // every statement this generator emits omits it.
                if (!types.TryGetValue(DotNetGenerationPlan.Key(entry.Table, column), out string? type))
                {
                    continue;
                }

                if (column.Default is { Length: > 0 } declared && TranslatableDefault(declared, type) is not null)
                {
                    continue;
                }

                findings.Add(new ConversionFinding(
                    ConversionSeverity.Unsupported,
                    "Unsatisfiable column",
                    $"{entry.Table.Name}.{column.Name}",
                    column.Default is { Length: > 0 } unusable
                        ? $"The column is NOT NULL and no role is bound to it, so every generated write omits it and " +
                          $"depends on its default reaching the target. The source declares DEFAULT {unusable}, which " +
                          $"is not a literal this generator can prove carries the same meaning onto a '{type}' column, " +
                          "so nothing was emitted rather than a table whose first insert fails. Bind the column to a " +
                          "role, give it a literal default, or leave the table out of the mapping."
                        : "The column is NOT NULL, declares no default, and no role is bound to it, so nothing the " +
                          "generated code writes could supply a value and every insert into the table would fail. " +
                          "Bind it to a role, give it a default, or leave the table out of the mapping."));
            }

            if (unmapped.Length > 0)
            {
                findings.Add(new ConversionFinding(
                    ConversionSeverity.ManualReview,
                    "Unmapped column",
                    $"{entry.Table.Name}: {string.Join(", ", unmapped.Select(column => column.Name))}",
                    "These columns were created in the target table so no data has nowhere to land, but no role was bound " +
                    "to them, so the generated screens, API, and routine neither read nor write them."));
            }
        }

        if (findings.Any(finding => finding.Severity == ConversionSeverity.Unsupported))
        {
            return new ApplicationConversion([], findings);
        }

        DotNetGenerationPlan plan = new(mapping, applicationName, types, TransformDecisions(mapping));

        List<GeneratedFile> files =
        [
            .. DotNetTargetDatabaseEmitter.Emit(plan, findings),
            .. DotNetBackendEmitter.Emit(plan),
            .. DotNetFrontendEmitter.Emit(plan),
            new GeneratedFile(
                "mapping-manifest.json",
                ResolvedManifest(plan),
                "Resolved mapping: every role, the source object and column it bound to, the target type chosen, the " +
                "evidence anchor cited, and every transform decision. Machine-readable and content-digested."),
        ];

        findings.Add(new ConversionFinding(
            ConversionSeverity.ManualReview,
            "Verification",
            "Generated output",
            "Nothing here was compiled, started, or executed by the generator. Build it and run its tests against a " +
            "PostgreSQL instance before treating any of it as working software."));

        return new ApplicationConversion(files, findings);
    }

    /// <summary>
    /// The choices this generator makes that the source did not state, for one mapping. Public because a
    /// decision nobody can read is not a recorded decision.
    /// </summary>
    public static IReadOnlyList<DotNetTransformDecision> TransformDecisions(TargetMapping mapping)
    {
        ArgumentNullException.ThrowIfNull(mapping);

        List<DotNetTransformDecision> decisions =
        [
            new(
                "identity-allocation",
                "Every column bound to Identifier is created GENERATED BY DEFAULT AS IDENTITY.",
                "The mapping proved each is a single-column primary key but says nothing about how the source allocated " +
                "it. An identity column allocates without a sequence name this generator would have had to invent, and " +
                "BY DEFAULT leaves existing keys insertable when data is loaded."),
            new(
                "server-computed-money",
                "Line amount and header total are computed inside the generated routine, never accepted from the caller.",
                "A submitted total is an assertion by the client. Computing quantity multiplied by the unit price read " +
                "from the item row in the same transaction makes the stored total reproducible from the stored lines."),
            new(
                "unit-price-snapshot",
                "The detail line's unit price is written from the item row read during the transaction.",
                "Without a snapshot, a later price change would silently restate historical line amounts."),
            new(
                "single-transaction",
                "Header insert, detail inserts, and stock decrements happen in one routine invocation and one transaction.",
                "Any refusal raises, which aborts the transaction, so a rejected submission leaves no header, no line, " +
                "and no stock change behind."),
            new(
                "item-lock-order",
                "Lines are processed in ascending item-identifier order.",
                "Two concurrent submissions touching the same two items in opposite orders would otherwise be able to " +
                "deadlock on each other."),
        ];

        decisions.Add(mapping.ItemIsVersioned
            ? new DotNetTransformDecision(
                "concurrency-control",
                "Optimistic: the stock decrement is conditioned on the version value read earlier, which it increments.",
                "The mapping bound ConcurrencyVersion on the item, so a lost update is detectable without holding a row " +
                "lock for the length of the submission. A failed condition is reported as a conflict and writes nothing.")
            : new DotNetTransformDecision(
                "concurrency-control",
                "Pessimistic: item rows are read FOR UPDATE before their stock is checked and decremented.",
                "The mapping bound no ConcurrencyVersion on the item, so there is no value to condition a write on. " +
                "Holding the row lock for the rest of the transaction is what stops two submissions overselling."));

        if (mapping.Header.Optional(MappedFieldRole.ConcurrencyVersion) is not null)
        {
            decisions.Add(new DotNetTransformDecision(
                "header-version-seed",
                "A newly inserted header is written with version 1 and the value is returned to the caller.",
                "The mapping bound ConcurrencyVersion on the header, so a later update path has a value to condition on."));
        }

        return decisions;
    }

    private static string ResolvedManifest(DotNetGenerationPlan plan)
    {
        JsonArray objects = [];

        foreach (ResolvedObject entry in plan.Mapping.Objects)
        {
            JsonArray fields = [];
            foreach (ResolvedField field in entry.Fields)
            {
                fields.Add(new JsonObject
                {
                    ["role"] = field.Role.ToString(),
                    ["sourceColumn"] = field.Column.Name,
                    ["sourceType"] = field.Column.RawType,
                    ["sourceNotNull"] = field.Column.NotNull,
                    ["targetColumn"] = field.Column.Name.ToLowerInvariant(),
                    ["targetType"] = field.PostgreSqlType,
                    ["clrType"] = field.ClrType,
                });
            }

            JsonArray anchors = [];
            foreach (MappedSourceReference source in entry.Sources)
            {
                anchors.Add(new JsonObject
                {
                    ["id"] = source.Id,
                    ["kind"] = source.Kind.ToString(),
                    ["path"] = source.Path,
                    ["module"] = source.Module,
                    ["textDigest"] = source.TextDigest,
                });
            }

            JsonObject constants = [];
            foreach ((MappedFieldRole role, string value) in entry.Constants.OrderBy(pair => pair.Key))
            {
                constants[role.ToString()] = value;
            }

            objects.Add(new JsonObject
            {
                ["role"] = entry.Role.ToString(),
                ["sourceTable"] = entry.Table.Name,
                ["targetTable"] = entry.Table.Name.ToLowerInvariant(),
                ["declaredConstants"] = constants,
                ["sourceRefs"] = anchors,
                ["fields"] = fields,
            });
        }

        JsonArray decisions = [];
        foreach (DotNetTransformDecision decision in plan.Decisions)
        {
            decisions.Add(new JsonObject
            {
                ["id"] = decision.Id,
                ["decision"] = decision.Decision,
                ["rationale"] = decision.Rationale,
            });
        }

        JsonArray errors = [];
        foreach ((string sqlState, string meaning, int status) in RoutineErrors)
        {
            errors.Add(new JsonObject
            {
                ["sqlState"] = sqlState,
                ["meaning"] = meaning,
                ["httpStatus"] = status,
            });
        }

        JsonObject document = new()
        {
            ["schemaVersion"] = ResolvedManifestSchemaVersion,
            ["generator"] = GeneratorVersion,
            ["mappingSchemaVersion"] = plan.Mapping.Declaration.SchemaVersion,
            ["application"] = plan.ApplicationName,
            ["fixtureLabel"] = plan.Mapping.Declaration.FixtureLabel,
            ["target"] = new JsonObject
            {
                ["frontEnd"] = FrontEndStack.React.ToString(),
                ["backEnd"] = BackEndStack.AspNetCore.ToString(),
                ["database"] = DatabaseTarget.PostgreSql.ToString(),
            },
            ["concurrencyControl"] = plan.Optimistic ? "optimistic-version-column" : "pessimistic-row-lock",
            ["objects"] = objects,
            ["decisions"] = decisions,
            ["routines"] = new JsonArray
            {
                new JsonObject
                {
                    ["name"] = RoutineName,
                    ["language"] = "plpgsql",
                    ["purpose"] = "Validates a submission, writes the header and its lines, and decrements item stock atomically.",
                    ["errors"] = errors,
                },
            },
            ["verification"] = new JsonObject
            {
                ["executedByGenerator"] = false,
                ["note"] = "The generator compiled nothing and contacted no database. Build and test results are separate evidence.",
            },
        };

        string body = document.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        document["bindingDigest"] = System.Convert
            .ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(body)))
            .ToLowerInvariant();

        return document.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n";
    }

    /// <summary>
    /// The PostgreSQL type for one parsed column, or null when this generator will not choose one. Null is
    /// a refusal: a created table that quietly omits a column reads back as a table the source never had.
    /// </summary>
    internal static string? PostgreSqlType(OracleColumn column)
    {
        string Number()
        {
            if (column.Scale is > 0)
            {
                return column.Precision is { } scaled
                    ? $"numeric({scaled.ToString(CultureInfo.InvariantCulture)},{column.Scale!.Value.ToString(CultureInfo.InvariantCulture)})"
                    : "numeric";
            }

            return column.Precision switch
            {
                null => "numeric",
                <= 9 => "integer",
                <= 18 => "bigint",
                { } wide => $"numeric({wide.ToString(CultureInfo.InvariantCulture)},0)",
            };
        }

        string Sized(string keyword, string fallback) =>
            column.Precision is { } length && length > 0
                ? $"{keyword}({length.ToString(CultureInfo.InvariantCulture)})"
                : fallback;

        return column.BaseType switch
        {
            "NUMBER" or "DECIMAL" or "NUMERIC" => Number(),
            "INTEGER" or "INT" or "SMALLINT" => "integer",
            "VARCHAR2" or "VARCHAR" or "NVARCHAR2" => Sized("varchar", "text"),
            "CHAR" or "NCHAR" => Sized("char", "char(1)"),
            "DATE" or "TIMESTAMP" => "timestamp",
            "TIMESTAMP WITH TIME ZONE" or "TIMESTAMP WITH LOCAL TIME ZONE" => "timestamptz",
            "CLOB" or "NCLOB" or "LONG" => "text",
            "BLOB" or "RAW" or "LONG RAW" => "bytea",
            "FLOAT" or "BINARY_FLOAT" => "real",
            "BINARY_DOUBLE" or "DOUBLE PRECISION" => "double precision",
            _ => null,
        };
    }

    /// <summary>PascalCase from an Oracle identifier. Deterministic, and never consulted for behaviour.</summary>
    internal static string Pascal(string name)
    {
        StringBuilder builder = new(name.Length);
        bool upper = true;

        foreach (char character in name)
        {
            if (character is '_' or '$' or '#' or ' ' or '-')
            {
                upper = true;
                continue;
            }

            builder.Append(upper ? char.ToUpperInvariant(character) : char.ToLowerInvariant(character));
            upper = false;
        }

        return builder.Length == 0 ? "Value" : builder.ToString();
    }

    /// <summary>A human label for a generated screen, derived from the mapped identifier.</summary>
    internal static string Label(string name)
    {
        string pascal = Pascal(name);
        StringBuilder builder = new(pascal.Length + 8);

        for (int index = 0; index < pascal.Length; index++)
        {
            if (index > 0 && char.IsUpper(pascal[index]))
            {
                builder.Append(' ');
            }

            builder.Append(pascal[index]);
        }

        return builder.ToString();
    }

    /// <summary>A single-quoted SQL string literal. Used only for operator-declared constants.</summary>
    internal static string Literal(string value) => $"'{value.Replace("'", "''", StringComparison.Ordinal)}'";

    /// <summary>
    /// The PostgreSQL rendering of an Oracle column default this generator is willing to carry across, or
    /// null when it is not.
    ///
    /// Only a literal qualifies, and only one whose kind the target column can hold. A literal is the one
    /// class of default expression that provably means the same thing to both engines: no function
    /// resolves differently, no session setting applies, and nothing is rewritten. Everything else —
    /// <c>SYSDATE</c>, a sequence, <c>SYS_GUID()</c>, a concatenation, a call into PL/SQL — would be this
    /// generator deciding what the source meant, so it is refused and reported instead.
    ///
    /// Oracle stores the empty string as NULL, so <c>''</c> is not a value and is refused as well.
    /// </summary>
    internal static string? TranslatableDefault(string oracleDefault, string targetType)
    {
        ArgumentNullException.ThrowIfNull(oracleDefault);
        ArgumentNullException.ThrowIfNull(targetType);

        string expression = oracleDefault.Trim();
        if (expression.Length == 0)
        {
            return null;
        }

        return expression[0] == '\''
            ? IsTextLiteral(expression) && HoldsText(targetType) ? expression : null
            : IsNumericLiteral(expression) && HoldsNumber(targetType) ? expression : null;
    }

    /// <summary>Whether the expression is one single-quoted literal with a non-empty value.</summary>
    private static bool IsTextLiteral(string expression)
    {
        if (expression.Length < 2 || expression[0] != '\'' || expression[^1] != '\'')
        {
            return false;
        }

        int characters = 0;

        for (int index = 1; index < expression.Length - 1; index++)
        {
            if (expression[index] != '\'')
            {
                characters++;
                continue;
            }

            // A lone quote inside would close the literal, making the rest of the expression something
            // other than one literal.
            if (index + 1 >= expression.Length - 1 || expression[index + 1] != '\'')
            {
                return false;
            }

            index++;
            characters++;
        }

        return characters > 0;
    }

    private static bool IsNumericLiteral(string expression)
    {
        bool digits = false;
        bool point = false;

        for (int index = expression[0] is '+' or '-' ? 1 : 0; index < expression.Length; index++)
        {
            if (char.IsAsciiDigit(expression[index]))
            {
                digits = true;
                continue;
            }

            if (expression[index] == '.' && !point)
            {
                point = true;
                continue;
            }

            return false;
        }

        return digits;
    }

    private static bool HoldsText(string targetType) =>
        targetType is "text"
        || targetType.StartsWith("varchar", StringComparison.Ordinal)
        || targetType.StartsWith("char", StringComparison.Ordinal);

    private static bool HoldsNumber(string targetType) =>
        targetType is "integer" or "bigint" or "real" or "double precision"
        || targetType.StartsWith("numeric", StringComparison.Ordinal);
}
