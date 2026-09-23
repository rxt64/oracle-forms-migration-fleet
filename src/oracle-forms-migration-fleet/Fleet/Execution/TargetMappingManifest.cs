using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace OracleFormsMigrationFleet.Fleet.Execution;

/// <summary>
/// The structural part an operator assigns to a mapped table. Roles are the vocabulary the generator
/// reasons in, so a rule is written once against a role and never against a table name.
/// </summary>
public enum MappedObjectRole
{
    /// <summary>The master record of a master/detail pair.</summary>
    MasterHeader,

    /// <summary>The dependent record of a master/detail pair.</summary>
    DetailLine,

    /// <summary>The entity the master record is raised for and looked up by the operator.</summary>
    PartyLookup,

    /// <summary>The entity a detail line draws price and available stock from.</summary>
    ItemLookup,
}

/// <summary>
/// The part one mapped column plays. A role carries a declared obligation — a type family, a nullability,
/// sometimes a key or a foreign key — that is checked against the parsed schema before anything is
/// generated, so the generator never infers a business meaning from an identifier.
/// </summary>
public enum MappedFieldRole
{
    Identifier,
    ParentReference,
    PartyReference,
    ItemReference,
    Quantity,
    UnitPrice,
    LineAmount,
    TotalAmount,
    StockOnHand,
    ConcurrencyVersion,
    DisplayName,
    ActiveFlag,
    CreatedAt,
    Status,
}

/// <summary>What a declared source reference points at.</summary>
public enum MappedSourceKind
{
    /// <summary>An object of the parsed Oracle schema, addressed as <c>TABLE:NAME</c>.</summary>
    OracleSchemaObject,

    /// <summary>A retained Forms source-object path inside one normalized module.</summary>
    FormsSourceObject,
}

/// <summary>One evidence anchor a mapping decision cites. Nothing is generated from an unresolved anchor.</summary>
public sealed record MappedSourceReference(
    string Id,
    MappedSourceKind Kind,
    string Path,
    string? Module,
    string? TextDigest);

/// <summary>An operator's declaration that one column plays one role.</summary>
public sealed record MappedFieldDeclaration(MappedFieldRole Role, string Column);

/// <summary>An operator's declaration that one table plays one role, with the anchors that justify it.</summary>
/// <param name="Constants">
/// Literals the generated code writes or compares for roles that have no computable value: which flag
/// value means available, which status a new record starts in. They are declared because the generator
/// has no way to derive them, and inventing one would put a value into a production table on a guess.
/// </param>
public sealed record MappedObjectDeclaration(
    MappedObjectRole Role,
    string Table,
    IReadOnlyList<string> SourceRefs,
    IReadOnlyList<MappedFieldDeclaration> Fields,
    IReadOnlyDictionary<MappedFieldRole, string> Constants);

/// <summary>
/// The manifest as supplied, before any of it has been checked against a schema. Reading this type is
/// never an approval: it is the untrusted document, and <see cref="TargetMappingReader"/> is the only
/// path from it to something the generator will act on.
/// </summary>
public sealed record TargetMappingDeclaration(
    string SchemaVersion,
    string Generator,
    string Application,
    string FixtureLabel,
    IReadOnlyList<MappedSourceReference> Sources,
    IReadOnlyList<MappedObjectDeclaration> Objects);

/// <summary>A role bound to a real parsed column, with the target types the transform decided on.</summary>
public sealed record ResolvedField(
    MappedFieldRole Role,
    OracleColumn Column,
    string PostgreSqlType,
    string ClrType)
{
    public string SourceName => Column.Name;
}

/// <summary>A role bound to a real parsed table.</summary>
public sealed record ResolvedObject(
    MappedObjectRole Role,
    OracleTable Table,
    IReadOnlyList<ResolvedField> Fields,
    IReadOnlyList<MappedSourceReference> Sources,
    IReadOnlyDictionary<MappedFieldRole, string> Constants)
{
    public string Constant(MappedFieldRole role) =>
        Constants.TryGetValue(role, out string? value) ? value : throw new InvalidOperationException(
            $"{Role} declares no constant for {role}; the reader should have refused this mapping.");

    public ResolvedField? Optional(MappedFieldRole role) =>
        Fields.FirstOrDefault(field => field.Role == role);

    public ResolvedField Require(MappedFieldRole role) =>
        Optional(role) ?? throw new InvalidOperationException(
            $"{Role} has no column bound to {role}; the reader should have refused this mapping.");
}

/// <summary>
/// One declared Forms property this generator demonstrably re-expresses, and the target element it
/// becomes.
///
/// <paramref name="TargetReference"/> names a role bound to a real table or column of the resolved
/// mapping, so a decision to carry the property forward can be read back against something that was
/// actually emitted rather than against the word "preserve".
/// </summary>
public sealed record CarriedSourceProperty(
    string ModulePath,
    string ObjectPath,
    string PropertyName,
    string TargetReference);

/// <summary>A manifest every declaration of which resolved against the parsed schema and the normalized IR.</summary>
/// <param name="Carried">
/// Every declared source property this generator carries into the target, derived from the resolved role
/// bindings and the retained export facts. A property absent from it is one nothing in the emitted tier
/// is derived from, which is a different statement from it being unimportant.
/// </param>
public sealed record TargetMapping(
    TargetMappingDeclaration Declaration,
    IReadOnlyList<ResolvedObject> Objects,
    IReadOnlyList<CarriedSourceProperty> Carried)
{
    public ResolvedObject Object(MappedObjectRole role) =>
        Objects.FirstOrDefault(entry => entry.Role == role) ?? throw new InvalidOperationException(
            $"No table is bound to {role}; the reader should have refused this mapping.");

    public ResolvedObject Header => Object(MappedObjectRole.MasterHeader);

    public ResolvedObject Detail => Object(MappedObjectRole.DetailLine);

    public ResolvedObject Party => Object(MappedObjectRole.PartyLookup);

    public ResolvedObject Item => Object(MappedObjectRole.ItemLookup);

    /// <summary>
    /// True when the item table declares a version column, which is what decides whether the generated
    /// routine takes the optimistic path. The choice is the mapping's, not the generator's guess.
    /// </summary>
    public bool ItemIsVersioned => Item.Optional(MappedFieldRole.ConcurrencyVersion) is not null;
}

/// <summary>Exactly one side is set: a mapping the generator may act on, or every reason it was refused.</summary>
public sealed record TargetMappingRead(TargetMapping? Mapping, IReadOnlyList<string> Rejections);

/// <summary>
/// Reads a supplied target-mapping manifest and refuses everything it cannot prove.
///
/// The manifest is operator-supplied and untrusted. Every table, column, key, foreign key, type, scale,
/// and nullability it claims is re-derived from the parsed Oracle schema, and every source anchor it
/// cites is re-derived from the normalized Forms representation, including the text digest of the export
/// those facts were read from. A claim this reader cannot confirm becomes a rejection, never a default:
/// an absent role is not an optional feature, an unrecognized role is not ignored, and an unreadable
/// anchor is not a weaker anchor.
/// </summary>
public static partial class TargetMappingReader
{
    /// <summary>The only manifest version this reader accepts. A manifest is data, so the version is frozen text.</summary>
    public const string SchemaVersion = "fleet.target-mapping/1";

    /// <summary>The generator contract this reader knows how to satisfy.</summary>
    public const string GeneratorId = "dotnet-master-detail/1";

    /// <summary>Conventional manifest location inside the run's source root.</summary>
    public const string ConventionalPath = "mapping/target-mapping.json";

    /// <summary>Maximum manifest size read from a workspace.</summary>
    public const long MaxManifestBytes = 512 * 1024;

    /// <summary>The export element a mapped table's base-table binding is read from.</summary>
    public const string BlockElement = "Block";

    /// <summary>The export element a mapped column's binding is read from.</summary>
    public const string ItemElement = "Item";

    /// <summary>The block attribute naming the table the block queries.</summary>
    public const string DataSourceAttribute = "QueryDataSourceName";

    /// <summary>The item attribute naming the column the item is bound to.</summary>
    public const string ColumnAttribute = "ColumnName";

    private static readonly MappedFieldRole[] s_declaredConstants =
        [MappedFieldRole.ActiveFlag, MappedFieldRole.Status];

    private static readonly MappedObjectRole[] s_requiredObjects =
    [
        MappedObjectRole.MasterHeader,
        MappedObjectRole.DetailLine,
        MappedObjectRole.PartyLookup,
        MappedObjectRole.ItemLookup,
    ];

    private static readonly Dictionary<MappedObjectRole, MappedFieldRole[]> s_requiredFields = new()
    {
        [MappedObjectRole.MasterHeader] =
            [MappedFieldRole.Identifier, MappedFieldRole.PartyReference, MappedFieldRole.TotalAmount, MappedFieldRole.CreatedAt],
        [MappedObjectRole.DetailLine] =
        [
            MappedFieldRole.Identifier, MappedFieldRole.ParentReference, MappedFieldRole.ItemReference,
            MappedFieldRole.Quantity, MappedFieldRole.UnitPrice, MappedFieldRole.LineAmount,
        ],
        [MappedObjectRole.PartyLookup] = [MappedFieldRole.Identifier, MappedFieldRole.DisplayName],
        [MappedObjectRole.ItemLookup] =
            [MappedFieldRole.Identifier, MappedFieldRole.DisplayName, MappedFieldRole.UnitPrice, MappedFieldRole.StockOnHand],
    };

    private static readonly Dictionary<MappedObjectRole, MappedFieldRole[]> s_permittedFields = new()
    {
        [MappedObjectRole.MasterHeader] =
        [
            MappedFieldRole.Identifier, MappedFieldRole.PartyReference, MappedFieldRole.TotalAmount,
            MappedFieldRole.CreatedAt, MappedFieldRole.ConcurrencyVersion, MappedFieldRole.Status,
        ],
        [MappedObjectRole.DetailLine] =
        [
            MappedFieldRole.Identifier, MappedFieldRole.ParentReference, MappedFieldRole.ItemReference,
            MappedFieldRole.Quantity, MappedFieldRole.UnitPrice, MappedFieldRole.LineAmount,
        ],
        [MappedObjectRole.PartyLookup] =
            [MappedFieldRole.Identifier, MappedFieldRole.DisplayName, MappedFieldRole.ActiveFlag],
        [MappedObjectRole.ItemLookup] =
        [
            MappedFieldRole.Identifier, MappedFieldRole.DisplayName, MappedFieldRole.UnitPrice,
            MappedFieldRole.StockOnHand, MappedFieldRole.ActiveFlag, MappedFieldRole.ConcurrencyVersion,
        ],
    };

    /// <summary>Parses and fully validates <paramref name="json"/>. No exception escapes for malformed input.</summary>
    public static TargetMappingRead Read(
        string? json,
        OracleSchema schema,
        IReadOnlyList<FormsModule>? forms = null)
    {
        ArgumentNullException.ThrowIfNull(schema);

        TargetMappingDeclaration declaration;
        List<string> rejections = [];

        try
        {
            declaration = Parse(json, rejections);
        }
        catch (JsonException exception)
        {
            return new TargetMappingRead(null, [$"The target mapping manifest is not valid JSON: {exception.Message}"]);
        }

        if (rejections.Count > 0)
        {
            return new TargetMappingRead(null, rejections);
        }

        if (!string.Equals(declaration.SchemaVersion, SchemaVersion, StringComparison.Ordinal))
        {
            return new TargetMappingRead(null,
            [
                $"The manifest declares schemaVersion '{declaration.SchemaVersion}'. This reader accepts only " +
                $"'{SchemaVersion}', and reading a version it does not know would be a guess about what the fields mean.",
            ]);
        }

        if (!string.Equals(declaration.Generator, GeneratorId, StringComparison.Ordinal))
        {
            return new TargetMappingRead(null,
            [
                $"The manifest requests generator '{declaration.Generator}'. This build implements '{GeneratorId}' only.",
            ]);
        }

        Dictionary<string, MappedSourceReference> anchors = new(StringComparer.Ordinal);
        foreach (MappedSourceReference source in declaration.Sources)
        {
            if (!anchors.TryAdd(source.Id, source))
            {
                rejections.Add($"Source anchor '{source.Id}' is declared more than once.");
            }
        }

        IReadOnlyList<FormsModule> modules = forms ?? [];

        foreach (MappedSourceReference source in declaration.Sources)
        {
            ValidateAnchor(source, schema, modules, rejections);
        }

        List<ResolvedObject> resolved = [];
        HashSet<MappedObjectRole> seenRoles = [];

        foreach (MappedObjectDeclaration declaredObject in declaration.Objects)
        {
            if (!seenRoles.Add(declaredObject.Role))
            {
                rejections.Add(
                    $"Object role {declaredObject.Role} is declared more than once. A role names one table, so two " +
                    "candidates are an ambiguity this reader will not resolve on the operator's behalf.");
                continue;
            }

            if (ResolveObject(declaredObject, schema, anchors, rejections) is { } entry)
            {
                resolved.Add(entry);
            }
        }

        foreach (MappedObjectRole required in s_requiredObjects)
        {
            if (!seenRoles.Contains(required))
            {
                rejections.Add(
                    $"No table is bound to the required object role {required}. The generator emits a master/detail " +
                    "workflow with party and item lookups; an unbound role is missing behaviour, not a smaller feature set.");
            }
        }

        if (rejections.Count == 0)
        {
            ValidateRelationships(resolved, rejections);
        }

        return rejections.Count > 0
            ? new TargetMappingRead(null, rejections)
            : new TargetMappingRead(new TargetMapping(declaration, resolved, Carry(resolved, modules)), []);
    }

    /// <summary>
    /// Every declared Forms property the emitted tier is derived from, resolved to what it becomes.
    ///
    /// The generator this build implements reads two things out of a mapped module: the base table a
    /// block queries, and the column an item under that block is bound to. Everything else an export
    /// declares — a trigger body, a prompt, a display count, a window, a program unit — reaches no
    /// emitted file, so nothing here claims it does. A block whose declared base table is not the table
    /// the role resolved to carries nothing at all, because the correspondence it would rest on is the
    /// thing that failed to hold.
    /// </summary>
    private static IReadOnlyList<CarriedSourceProperty> Carry(
        IReadOnlyList<ResolvedObject> objects,
        IReadOnlyList<FormsModule> forms)
    {
        List<CarriedSourceProperty> carried = [];

        foreach (ResolvedObject resolved in objects)
        {
            foreach (MappedSourceReference anchor in resolved.Sources)
            {
                if (anchor.Kind != MappedSourceKind.FormsSourceObject ||
                    anchor.Module is not { Length: > 0 } modulePath ||
                    forms.FirstOrDefault(candidate =>
                        string.Equals(candidate.SourcePath, modulePath, StringComparison.Ordinal))?.SourceFacts
                        is not { } facts)
                {
                    continue;
                }

                FormsSourceFact? block = facts.Facts.FirstOrDefault(fact =>
                    string.Equals(fact.Id, anchor.Path, StringComparison.Ordinal) &&
                    string.Equals(fact.LocalName, BlockElement, StringComparison.Ordinal));

                if (block is null ||
                    Declared(block, DataSourceAttribute) is not { } baseTable ||
                    !string.Equals(baseTable, resolved.Table.Name, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string table =
                    $"{resolved.Role} bound to table '{resolved.Table.Name}', emitted as the PostgreSQL table of that role";
                carried.Add(new(modulePath, block.Id, DispositionLedgerEntries.ObjectPresenceProperty, table));
                carried.Add(new(modulePath, block.Id, DataSourceAttribute, table));

                foreach (FormsSourceFact item in facts.Facts)
                {
                    if (!string.Equals(item.LocalName, ItemElement, StringComparison.Ordinal) ||
                        !GenerationCoverage.Covers(block.Id, item.Id) ||
                        Declared(item, ColumnAttribute) is not { } column ||
                        resolved.Fields.FirstOrDefault(field =>
                            string.Equals(field.SourceName, column, StringComparison.OrdinalIgnoreCase)) is not { } field)
                    {
                        continue;
                    }

                    string target =
                        $"{field.Role} bound to '{resolved.Table.Name}.{field.SourceName}', emitted as {field.PostgreSqlType}";
                    carried.Add(new(modulePath, item.Id, DispositionLedgerEntries.ObjectPresenceProperty, target));
                    carried.Add(new(modulePath, item.Id, ColumnAttribute, target));
                }
            }
        }

        return carried;
    }

    private static string? Declared(FormsSourceFact fact, string attribute) =>
        fact.Attributes.FirstOrDefault(candidate =>
            candidate.Namespace.Length == 0 &&
            string.Equals(candidate.Name, attribute, StringComparison.Ordinal))?.Value;

    /// <summary>
    /// Reads only the evidence anchors a manifest declares, without a parsed schema to confirm them
    /// against.
    ///
    /// This exists for the server deciding which recorded dispositions a mapping would rest on, which it
    /// has to do before any generation phase runs and therefore before a schema has been parsed. It is a
    /// strictly weaker read than <see cref="Read"/> and is never a substitute for it: it confirms no
    /// table, no column, and no source-object path, so nothing may be generated from its result.
    /// </summary>
    public static (IReadOnlyList<MappedSourceReference>? Sources, IReadOnlyList<string> Rejections) ReadAnchors(string? json)
    {
        List<string> rejections = [];
        TargetMappingDeclaration declaration;

        try
        {
            declaration = Parse(json, rejections);
        }
        catch (JsonException exception)
        {
            return (null, [$"The target mapping manifest is not valid JSON: {exception.Message}"]);
        }

        if (!string.Equals(declaration.SchemaVersion, SchemaVersion, StringComparison.Ordinal))
        {
            rejections.Add(
                $"The manifest declares schemaVersion '{declaration.SchemaVersion}'. This reader accepts only '{SchemaVersion}'.");
        }

        if (!string.Equals(declaration.Generator, GeneratorId, StringComparison.Ordinal))
        {
            rejections.Add($"The manifest requests generator '{declaration.Generator}'. This build implements '{GeneratorId}' only.");
        }

        return rejections.Count > 0 ? (null, rejections) : (declaration.Sources, []);
    }

    private static TargetMappingDeclaration Parse(string? json, List<string> rejections)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            rejections.Add("The target mapping manifest is empty.");
            return Blank();
        }

        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;

        if (root.ValueKind != JsonValueKind.Object)
        {
            rejections.Add("The target mapping manifest must be a JSON object.");
            return Blank();
        }

        string schemaVersion = RequiredText(root, "schemaVersion", rejections);
        string generator = RequiredText(root, "generator", rejections);
        string application = RequiredText(root, "application", rejections);
        string fixtureLabel = RequiredText(root, "fixtureLabel", rejections);

        List<MappedSourceReference> sources = [];
        if (root.TryGetProperty("sources", out JsonElement sourceArray) && sourceArray.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement element in sourceArray.EnumerateArray())
            {
                string id = RequiredText(element, "id", rejections);
                string kindText = RequiredText(element, "kind", rejections);
                string path = RequiredText(element, "path", rejections);

                if (!Enum.TryParse(kindText, ignoreCase: false, out MappedSourceKind kind))
                {
                    rejections.Add(
                        $"Source anchor '{id}' declares kind '{kindText}', which this reader does not recognize. " +
                        $"Recognized kinds: {string.Join(", ", Enum.GetNames<MappedSourceKind>())}.");
                    continue;
                }

                sources.Add(new MappedSourceReference(
                    id,
                    kind,
                    path,
                    OptionalText(element, "module"),
                    OptionalText(element, "textDigest")));
            }
        }
        else
        {
            rejections.Add("The manifest declares no 'sources' array. Every mapped object has to cite the evidence it rests on.");
        }

        List<MappedObjectDeclaration> objects = [];
        if (root.TryGetProperty("objects", out JsonElement objectArray) && objectArray.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement element in objectArray.EnumerateArray())
            {
                string roleText = RequiredText(element, "role", rejections);
                string table = RequiredText(element, "table", rejections);

                if (!Enum.TryParse(roleText, ignoreCase: false, out MappedObjectRole role))
                {
                    rejections.Add(
                        $"'{table}' is declared with object role '{roleText}', which this reader does not recognize. " +
                        $"Recognized roles: {string.Join(", ", Enum.GetNames<MappedObjectRole>())}.");
                    continue;
                }

                List<string> refs = [];
                if (element.TryGetProperty("sourceRefs", out JsonElement refArray) && refArray.ValueKind == JsonValueKind.Array)
                {
                    refs.AddRange(refArray.EnumerateArray()
                        .Where(entry => entry.ValueKind == JsonValueKind.String)
                        .Select(entry => entry.GetString()!));
                }

                List<MappedFieldDeclaration> fields = [];
                bool fieldRoleRejected = false;
                if (element.TryGetProperty("fields", out JsonElement fieldArray) && fieldArray.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement field in fieldArray.EnumerateArray())
                    {
                        string fieldRoleText = RequiredText(field, "role", rejections);
                        string column = RequiredText(field, "column", rejections);

                        if (!Enum.TryParse(fieldRoleText, ignoreCase: false, out MappedFieldRole fieldRole))
                        {
                            rejections.Add(
                                $"'{table}.{column}' is declared with field role '{fieldRoleText}', which this reader does " +
                                $"not recognize. Recognized roles: {string.Join(", ", Enum.GetNames<MappedFieldRole>())}.");
                            fieldRoleRejected = true;
                            continue;
                        }

                        fields.Add(new MappedFieldDeclaration(fieldRole, column));
                    }
                }
                else
                {
                    rejections.Add($"'{table}' declares no 'fields' array.");
                }

                if (!fieldRoleRejected)
                {
                    Dictionary<MappedFieldRole, string> constants = [];
                    if (element.TryGetProperty("constants", out JsonElement constantObject) &&
                        constantObject.ValueKind == JsonValueKind.Object)
                    {
                        foreach (JsonProperty constant in constantObject.EnumerateObject())
                        {
                            if (!Enum.TryParse(constant.Name, ignoreCase: false, out MappedFieldRole constantRole))
                            {
                                rejections.Add(
                                    $"'{table}' declares a constant for '{constant.Name}', which is not a field role this " +
                                    "reader recognizes.");
                                continue;
                            }

                            if (constant.Value.ValueKind != JsonValueKind.String ||
                                constant.Value.GetString() is not { Length: > 0 } constantValue)
                            {
                                rejections.Add($"'{table}' declares a non-string constant for {constantRole}.");
                                continue;
                            }

                            constants[constantRole] = constantValue;
                        }
                    }

                    objects.Add(new MappedObjectDeclaration(role, table, refs, fields, constants));
                }
            }
        }
        else
        {
            rejections.Add("The manifest declares no 'objects' array.");
        }

        return new TargetMappingDeclaration(schemaVersion, generator, application, fixtureLabel, sources, objects);
    }

    private static TargetMappingDeclaration Blank() => new(string.Empty, string.Empty, string.Empty, string.Empty, [], []);

    private static string RequiredText(JsonElement element, string name, List<string> rejections)
    {
        if (element.ValueKind == JsonValueKind.Object &&
            element.TryGetProperty(name, out JsonElement value) &&
            value.ValueKind == JsonValueKind.String &&
            value.GetString() is { Length: > 0 } text)
        {
            return text;
        }

        rejections.Add($"A manifest entry is missing the required string property '{name}'.");
        return string.Empty;
    }

    private static string? OptionalText(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static void ValidateAnchor(
        MappedSourceReference source,
        OracleSchema schema,
        IReadOnlyList<FormsModule> forms,
        List<string> rejections)
    {
        switch (source.Kind)
        {
            case MappedSourceKind.OracleSchemaObject:
                if (!source.Path.StartsWith("TABLE:", StringComparison.Ordinal))
                {
                    rejections.Add(
                        $"Source anchor '{source.Id}' addresses '{source.Path}'. Schema anchors are written 'TABLE:NAME'.");
                    return;
                }

                string table = source.Path["TABLE:".Length..];
                if (!schema.Tables.Any(candidate => string.Equals(candidate.Name, table, StringComparison.OrdinalIgnoreCase)))
                {
                    rejections.Add(
                        $"Source anchor '{source.Id}' cites table '{table}', which the parsed schema does not declare.");
                }

                return;

            case MappedSourceKind.FormsSourceObject:
                if (source.Module is not { Length: > 0 } modulePath)
                {
                    rejections.Add($"Source anchor '{source.Id}' is a Forms anchor and declares no 'module'.");
                    return;
                }

                FormsModule? module = forms.FirstOrDefault(candidate =>
                    string.Equals(candidate.SourcePath, modulePath, StringComparison.Ordinal));

                if (module is null)
                {
                    rejections.Add(
                        $"Source anchor '{source.Id}' cites module '{modulePath}', which is not among the normalized " +
                        "modules this run read. A mapping may not rest on a module nothing in this run opened.");
                    return;
                }

                if (module.SourceFacts is not { } facts)
                {
                    rejections.Add(
                        $"Source anchor '{source.Id}' cites module '{modulePath}', which carries no retained source facts, " +
                        "so the path it names cannot be confirmed to exist.");
                    return;
                }

                if (source.TextDigest is not { Length: > 0 } digest)
                {
                    rejections.Add(
                        $"Source anchor '{source.Id}' declares no 'textDigest'. Without it the anchor names a path in " +
                        "whatever export happens to be present rather than in the one the mapping was written against.");
                    return;
                }

                if (!string.Equals(digest, facts.TextDigest, StringComparison.OrdinalIgnoreCase))
                {
                    rejections.Add(
                        $"Source anchor '{source.Id}' was written against export text digest '{digest}', but module " +
                        $"'{modulePath}' in this run digests to '{facts.TextDigest}'. The mapping describes a different export.");
                    return;
                }

                if (!facts.Facts.Any(fact => string.Equals(fact.Id, source.Path, StringComparison.Ordinal)))
                {
                    rejections.Add(
                        $"Source anchor '{source.Id}' names source-object path '{source.Path}', which module " +
                        $"'{modulePath}' does not retain.");
                }

                return;

            default:
                rejections.Add($"Source anchor '{source.Id}' declares an unhandled kind.");
                return;
        }
    }

    private static ResolvedObject? ResolveObject(
        MappedObjectDeclaration declaration,
        OracleSchema schema,
        IReadOnlyDictionary<string, MappedSourceReference> anchors,
        List<string> rejections)
    {
        OracleTable[] matches =
        [
            .. schema.Tables.Where(table => string.Equals(table.Name, declaration.Table, StringComparison.OrdinalIgnoreCase)),
        ];

        if (matches.Length == 0)
        {
            rejections.Add(
                $"{declaration.Role} is bound to table '{declaration.Table}', which the parsed schema does not declare.");
            return null;
        }

        if (matches.Length > 1)
        {
            rejections.Add(
                $"{declaration.Role} is bound to table '{declaration.Table}', which the parsed schema declares " +
                $"{matches.Length.ToString(CultureInfo.InvariantCulture)} times. Which definition is meant is ambiguous.");
            return null;
        }

        OracleTable table = matches[0];

        if (declaration.SourceRefs.Count == 0)
        {
            rejections.Add($"{declaration.Role} ('{table.Name}') cites no source anchor.");
        }

        List<MappedSourceReference> cited = [];
        foreach (string reference in declaration.SourceRefs)
        {
            if (anchors.TryGetValue(reference, out MappedSourceReference? anchor))
            {
                cited.Add(anchor);
            }
            else
            {
                rejections.Add($"{declaration.Role} ('{table.Name}') cites source anchor '{reference}', which is not declared.");
            }
        }

        MappedFieldRole[] permitted = s_permittedFields[declaration.Role];
        List<ResolvedField> fields = [];
        HashSet<MappedFieldRole> seen = [];
        HashSet<string> boundColumns = new(StringComparer.OrdinalIgnoreCase);

        foreach (MappedFieldDeclaration field in declaration.Fields)
        {
            if (!permitted.Contains(field.Role))
            {
                rejections.Add(
                    $"{declaration.Role} ('{table.Name}') binds '{field.Column}' to {field.Role}, which this object role " +
                    $"does not carry. Roles permitted here: {string.Join(", ", permitted)}.");
                continue;
            }

            if (!seen.Add(field.Role))
            {
                rejections.Add(
                    $"{declaration.Role} ('{table.Name}') binds {field.Role} more than once. A field role names one column.");
                continue;
            }

            if (!boundColumns.Add(field.Column))
            {
                rejections.Add(
                    $"{declaration.Role} ('{table.Name}') binds column '{field.Column}' to more than one role.");
                continue;
            }

            OracleColumn[] columns =
            [
                .. table.Columns.Where(column => string.Equals(column.Name, field.Column, StringComparison.OrdinalIgnoreCase)),
            ];

            if (columns.Length != 1)
            {
                rejections.Add(columns.Length == 0
                    ? $"{declaration.Role} ('{table.Name}') binds {field.Role} to column '{field.Column}', which the table does not declare."
                    : $"{declaration.Role} ('{table.Name}') binds {field.Role} to column '{field.Column}', which the table declares more than once.");
                continue;
            }

            if (ResolveField(declaration.Role, table, field.Role, columns[0], rejections) is { } resolvedField)
            {
                fields.Add(resolvedField);
            }
        }

        foreach (MappedFieldRole required in s_requiredFields[declaration.Role])
        {
            if (!seen.Contains(required))
            {
                rejections.Add(
                    $"{declaration.Role} ('{table.Name}') binds no column to the required field role {required}.");
            }
        }

        foreach (MappedFieldRole declaring in s_declaredConstants)
        {
            if (fields.FirstOrDefault(field => field.Role == declaring) is not { } constantField)
            {
                continue;
            }

            if (!declaration.Constants.TryGetValue(declaring, out string? literal))
            {
                rejections.Add(
                    $"{declaration.Role} ('{table.Name}.{constantField.Column.Name}') is bound to {declaring} but declares " +
                    "no constant for it. The generated code writes or compares a literal here and has no way to derive one.");
                continue;
            }

            if (constantField.Column.Precision is { } length && literal.Length > length)
            {
                rejections.Add(
                    $"{declaration.Role} ('{table.Name}.{constantField.Column.Name}') declares constant '{literal}', which " +
                    $"is longer than the column's declared length {length.ToString(CultureInfo.InvariantCulture)}.");
                continue;
            }

            if (DomainValues(table, constantField.Column.Name) is { Count: > 0 } domain &&
                !domain.Contains(literal, StringComparer.Ordinal))
            {
                rejections.Add(
                    $"{declaration.Role} ('{table.Name}.{constantField.Column.Name}') declares constant '{literal}', which " +
                    $"the column's declared check constraint does not admit. Admitted: {string.Join(", ", domain)}.");
            }
        }

        foreach (MappedFieldRole constantRole in declaration.Constants.Keys)
        {
            if (fields.All(field => field.Role != constantRole))
            {
                rejections.Add(
                    $"{declaration.Role} ('{table.Name}') declares a constant for {constantRole}, which no column is bound to.");
            }
        }

        return new ResolvedObject(declaration.Role, table, fields, cited, declaration.Constants);
    }

    /// <summary>
    /// The literal values a single-column <c>IN</c> check admits for <paramref name="column"/>. An empty
    /// list means the schema declares no such domain, which is not the same as admitting everything: it is
    /// why a declared constant is still required.
    /// </summary>
    internal static IReadOnlyList<string> DomainValues(OracleTable table, string column)
    {
        foreach (OracleConstraint constraint in table.Constraints.Where(entry => entry.Kind == OracleConstraintKind.Check))
        {
            if (constraint.CheckExpression is not { Length: > 0 } expression)
            {
                continue;
            }

            Match match = SingleColumnInList().Match(expression);
            if (!match.Success || !string.Equals(match.Groups["column"].Value, column, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            List<string> values = [];
            foreach (Match literal in QuotedLiteral().Matches(match.Groups["values"].Value))
            {
                values.Add(literal.Groups["value"].Value.Replace("''", "'", StringComparison.Ordinal));
            }

            if (values.Count > 0)
            {
                return values;
            }
        }

        return [];
    }

    private static ResolvedField? ResolveField(
        MappedObjectRole objectRole,
        OracleTable table,
        MappedFieldRole role,
        OracleColumn column,
        List<string> rejections)
    {
        string where = $"{objectRole} ('{table.Name}.{column.Name}') bound to {role}";
        bool notNull = column.NotNull || InPrimaryKey(table, column.Name);

        switch (role)
        {
            case MappedFieldRole.Identifier:
                if (SingleColumnPrimaryKey(table) is not { } key ||
                    !string.Equals(key, column.Name, StringComparison.OrdinalIgnoreCase))
                {
                    rejections.Add(
                        $"{where} is not the table's single-column primary key. The generated routine addresses rows by " +
                        "this column, so a key it cannot prove is unique would let one identifier reach several rows.");
                    return null;
                }

                return Integral(where, column, rejections) is { } identifierType
                    ? new ResolvedField(role, column, identifierType.PostgreSql, identifierType.Clr)
                    : null;

            case MappedFieldRole.ParentReference:
            case MappedFieldRole.PartyReference:
            case MappedFieldRole.ItemReference:
            case MappedFieldRole.Quantity:
            case MappedFieldRole.StockOnHand:
            case MappedFieldRole.ConcurrencyVersion:
                if (!notNull)
                {
                    rejections.Add($"{where} is nullable. This role is read on every request and has no defined meaning when absent.");
                    return null;
                }

                return Integral(where, column, rejections) is { } integralType
                    ? new ResolvedField(role, column, integralType.PostgreSql, integralType.Clr)
                    : null;

            case MappedFieldRole.UnitPrice:
            case MappedFieldRole.LineAmount:
            case MappedFieldRole.TotalAmount:
                if (!notNull)
                {
                    rejections.Add($"{where} is nullable. Money this generator computes is never absent.");
                    return null;
                }

                return Decimal(where, column, rejections) is { } decimalType
                    ? new ResolvedField(role, column, decimalType.PostgreSql, decimalType.Clr)
                    : null;

            case MappedFieldRole.DisplayName:
            case MappedFieldRole.Status:
                if (!notNull)
                {
                    rejections.Add($"{where} is nullable.");
                    return null;
                }

                return Character(where, column, rejections) is { } textType
                    ? new ResolvedField(role, column, textType.PostgreSql, textType.Clr)
                    : null;

            case MappedFieldRole.ActiveFlag:
                if (!notNull)
                {
                    rejections.Add($"{where} is nullable. A three-valued availability flag has no defined filter.");
                    return null;
                }

                if (Character(where, column, rejections) is not { } flagType)
                {
                    return null;
                }

                if (column.Precision is not 1)
                {
                    rejections.Add(
                        $"{where} declares length {column.Precision?.ToString(CultureInfo.InvariantCulture) ?? "none"}. " +
                        "The generated filter compares a single-character flag and cannot decide what a longer domain means.");
                    return null;
                }

                return new ResolvedField(role, column, flagType.PostgreSql, flagType.Clr);

            case MappedFieldRole.CreatedAt:
                if (!notNull)
                {
                    rejections.Add($"{where} is nullable. The generated insert always writes it.");
                    return null;
                }

                if (column.BaseType is not ("DATE" or "TIMESTAMP"))
                {
                    rejections.Add($"{where} is declared '{column.RawType}'. This role requires DATE or TIMESTAMP.");
                    return null;
                }

                return new ResolvedField(role, column, "timestamp", "DateTime");

            default:
                rejections.Add($"{where} is a role this generator has no transform for.");
                return null;
        }
    }

    private static (string PostgreSql, string Clr)? Integral(string where, OracleColumn column, List<string> rejections)
    {
        if (column.BaseType is not ("NUMBER" or "INTEGER"))
        {
            rejections.Add($"{where} is declared '{column.RawType}'. This role requires a whole-number column.");
            return null;
        }

        if (column.Scale is > 0)
        {
            rejections.Add(
                $"{where} is declared '{column.RawType}' with scale " +
                $"{column.Scale!.Value.ToString(CultureInfo.InvariantCulture)}. This role counts or identifies and " +
                "cannot carry a fractional part.");
            return null;
        }

        int precision = column.Precision ?? 38;

        if (precision > 18)
        {
            rejections.Add(
                $"{where} declares precision {precision.ToString(CultureInfo.InvariantCulture)}, which exceeds what a " +
                "64-bit integer holds. Narrowing it here would silently lose values the source accepts.");
            return null;
        }

        return precision <= 9 ? ("integer", "int") : ("bigint", "long");
    }

    private static (string PostgreSql, string Clr)? Decimal(string where, OracleColumn column, List<string> rejections)
    {
        if (column.BaseType is not "NUMBER")
        {
            rejections.Add($"{where} is declared '{column.RawType}'. This role requires NUMBER with a declared scale.");
            return null;
        }

        if (column.Precision is not { } precision || column.Scale is not { } scale)
        {
            rejections.Add(
                $"{where} is declared '{column.RawType}' without both precision and scale. Money the generator computes " +
                "and compares has to round somewhere the mapping states, not wherever the driver happens to.");
            return null;
        }

        if (scale < 1)
        {
            rejections.Add($"{where} declares scale {scale.ToString(CultureInfo.InvariantCulture)}, so it cannot hold a monetary fraction.");
            return null;
        }

        if (precision > 28 || precision <= scale)
        {
            rejections.Add(
                $"{where} declares NUMBER({precision.ToString(CultureInfo.InvariantCulture)}," +
                $"{scale.ToString(CultureInfo.InvariantCulture)}), which this generator will not map: the integral part " +
                "must be positive and the whole value must fit a .NET decimal without truncation.");
            return null;
        }

        return ($"numeric({precision.ToString(CultureInfo.InvariantCulture)},{scale.ToString(CultureInfo.InvariantCulture)})", "decimal");
    }

    private static (string PostgreSql, string Clr)? Character(string where, OracleColumn column, List<string> rejections)
    {
        if (column.BaseType is not ("VARCHAR2" or "VARCHAR" or "CHAR" or "NVARCHAR2"))
        {
            rejections.Add($"{where} is declared '{column.RawType}'. This role requires a character column.");
            return null;
        }

        if (column.Precision is not { } length || length <= 0)
        {
            rejections.Add($"{where} is declared '{column.RawType}' without a length.");
            return null;
        }

        string type = column.BaseType is "CHAR"
            ? $"char({length.ToString(CultureInfo.InvariantCulture)})"
            : $"varchar({length.ToString(CultureInfo.InvariantCulture)})";

        return (type, "string");
    }

    private static void ValidateRelationships(IReadOnlyList<ResolvedObject> objects, List<string> rejections)
    {
        ResolvedObject header = objects.First(entry => entry.Role == MappedObjectRole.MasterHeader);
        ResolvedObject detail = objects.First(entry => entry.Role == MappedObjectRole.DetailLine);
        ResolvedObject party = objects.First(entry => entry.Role == MappedObjectRole.PartyLookup);
        ResolvedObject item = objects.First(entry => entry.Role == MappedObjectRole.ItemLookup);

        if (objects.Select(entry => entry.Table.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != objects.Count)
        {
            rejections.Add("Two object roles are bound to the same table. Each role addresses its own rows.");
        }

        RequireForeignKey(detail, MappedFieldRole.ParentReference, header, rejections);
        RequireForeignKey(header, MappedFieldRole.PartyReference, party, rejections);
        RequireForeignKey(detail, MappedFieldRole.ItemReference, item, rejections);

        ResolvedField unitPrice = detail.Require(MappedFieldRole.UnitPrice);
        ResolvedField lineAmount = detail.Require(MappedFieldRole.LineAmount);
        ResolvedField total = header.Require(MappedFieldRole.TotalAmount);
        ResolvedField itemPrice = item.Require(MappedFieldRole.UnitPrice);

        RequireNoRoundingLoss(itemPrice, unitPrice, "the item's unit price", "the detail line's unit price", rejections);
        RequireNoRoundingLoss(unitPrice, lineAmount, "the detail line's unit price", "its line amount", rejections);
        RequireNoRoundingLoss(lineAmount, total, "the detail line's line amount", "the header total", rejections);
    }

    private static void RequireNoRoundingLoss(
        ResolvedField from,
        ResolvedField to,
        string fromLabel,
        string toLabel,
        List<string> rejections)
    {
        if ((from.Column.Scale ?? 0) > (to.Column.Scale ?? 0))
        {
            rejections.Add(
                $"{fromLabel} ('{from.Column.RawType}') carries more decimal places than {toLabel} " +
                $"('{to.Column.RawType}'). The generated arithmetic would round on every line and the totals it stores " +
                "would not add up to the lines it stored beside them.");
        }
    }

    private static void RequireForeignKey(
        ResolvedObject from,
        MappedFieldRole role,
        ResolvedObject to,
        List<string> rejections)
    {
        if (from.Optional(role) is not { } field)
        {
            return;
        }

        string targetKey = to.Require(MappedFieldRole.Identifier).Column.Name;

        bool declared = from.Table.Constraints.Any(constraint =>
            constraint.Kind == OracleConstraintKind.ForeignKey
            && constraint.Columns.Count == 1
            && string.Equals(constraint.Columns[0], field.Column.Name, StringComparison.OrdinalIgnoreCase)
            && string.Equals(constraint.ReferencedTable, to.Table.Name, StringComparison.OrdinalIgnoreCase)
            && (constraint.ReferencedColumns.Count == 0
                || (constraint.ReferencedColumns.Count == 1
                    && string.Equals(constraint.ReferencedColumns[0], targetKey, StringComparison.OrdinalIgnoreCase))));

        if (!declared)
        {
            rejections.Add(
                $"{from.Role} ('{from.Table.Name}.{field.Column.Name}') is bound to {role}, but the schema declares no " +
                $"foreign key from it to {to.Table.Name}({targetKey}). The generated routine joins and cascades on that " +
                "relationship, and a relationship the database does not enforce is one the application would have to " +
                "assume held.");
        }
    }

    private static bool InPrimaryKey(OracleTable table, string column) =>
        table.Constraints.Any(constraint =>
            constraint.Kind == OracleConstraintKind.PrimaryKey
            && constraint.Columns.Any(name => string.Equals(name, column, StringComparison.OrdinalIgnoreCase)));

    private static string? SingleColumnPrimaryKey(OracleTable table)
    {
        OracleConstraint[] keys =
        [
            .. table.Constraints.Where(constraint => constraint.Kind == OracleConstraintKind.PrimaryKey),
        ];

        return keys.Length == 1 && keys[0].Columns.Count == 1 ? keys[0].Columns[0] : null;
    }

    [GeneratedRegex(
        @"^\s*\(?\s*(?<column>[A-Za-z_][A-Za-z0-9_$#]*)\s*\)?\s+IN\s*\((?<values>[^)]*)\)\s*\)?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SingleColumnInList();

    [GeneratedRegex(@"'(?<value>(?:[^']|'')*)'", RegexOptions.CultureInvariant)]
    private static partial Regex QuotedLiteral();
}
