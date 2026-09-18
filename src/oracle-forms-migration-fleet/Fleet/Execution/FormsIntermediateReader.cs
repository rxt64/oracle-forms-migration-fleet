// Copyright (c) Microsoft. All rights reserved.

using System.Globalization;
using System.Text.Json;

namespace OracleFormsMigrationFleet.Fleet.Execution;

/// <summary>
/// Outcome of reading the normalized Forms intermediate representation. Exactly one side is set: a
/// document either yields every module it declares or a reason it was refused. There is no partial read.
/// </summary>
public sealed record FormsIntermediateRead(IReadOnlyList<FormsModule>? Modules, string? Error);

/// <summary>
/// Reads the normalized Forms intermediate representation that <c>SourceNormalizationAdapter</c> wrote.
///
/// The document is read field by field with <see cref="JsonDocument"/> rather than deserialized into a
/// type graph. The IR lives in an operator's session workspace beside source that came from a customer
/// repository, so it is untrusted input: there is no converter, no type discriminator, and no constructor
/// this file can reach, and an unexpected shape produces nothing instead of an object.
///
/// Every tolerance the first version had has been removed, because each one turned a damaged IR into a
/// smaller estate that still looked whole: a block with no name became "UNNAMED", a malformed child was
/// skipped, and anything past the element cap was dropped without a word. A document that is not exactly
/// what this fleet wrote is now refused with a reason, and the caller generates nothing.
///
/// It retains trigger bodies as untrusted source text so changed source behaviour remains distinguishable.
/// It does not translate or execute them, and no program-unit body or LOV query is present to recover.
///
/// The document is also bound to the run reading it: the caller supplies the source root it is executing
/// against, and a representation that records a different root, or a module read from outside that root,
/// is refused rather than read against an estate it does not describe.
/// </summary>
public static class FormsIntermediateReader
{
    /// <summary>The only producer whose output this reader accepts.</summary>
    public const string Generator = "oracle-forms-migration-fleet/source-normalization";

    /// <summary>The only IR schema version this build understands.</summary>
    public const string SchemaVersion = "2";

    public const int MaxModules = 5_000;
    public const int MaxChildren = 20_000;
    public const long MaxDocumentBytes = 64L * 1024 * 1024;

    private static readonly string[] s_requiredRootFields =
        ["generator", "schemaVersion", "normalized", "sourceRoot", "formsFamily", "versionAuthority", "modules"];

    public static FormsIntermediateRead Read(string? json, string expectedSourceRoot)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return Refuse("The normalized Forms representation is empty.");
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip });
        }
        catch (JsonException exception)
        {
            return Refuse($"The normalized Forms representation is not valid JSON: {exception.Message}");
        }

        using (document)
        {
            JsonElement root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
            {
                return Refuse("The normalized Forms representation is not a JSON object.");
            }

            foreach (string field in s_requiredRootFields)
            {
                if (!root.TryGetProperty(field, out _))
                {
                    return Refuse($"The normalized Forms representation has no '{field}' field, so it is not a document this fleet wrote.");
                }
            }

            if (!string.Equals(Text(root, "generator"), Generator, StringComparison.Ordinal))
            {
                return Refuse(
                    $"The normalized Forms representation declares generator '{Text(root, "generator") ?? "(not a string)"}'. " +
                    $"Only '{Generator}' output is read, because nothing else is known to carry these field meanings.");
            }

            if (!string.Equals(Text(root, "schemaVersion"), SchemaVersion, StringComparison.Ordinal))
            {
                return Refuse(
                    $"The normalized Forms representation declares schema version '{Text(root, "schemaVersion") ?? "(not a string)"}', " +
                    $"and this build reads version '{SchemaVersion}' only. Re-import source to regenerate this format.");
            }

            if (root.GetProperty("normalized").ValueKind != JsonValueKind.True)
            {
                return Refuse(
                    "The normalized Forms representation does not declare normalized=true, so the phase that wrote it did not adjudicate " +
                    "this estate as normalized and nothing may generate from it.");
            }

            if (Trimmed(root, "sourceRoot") is not { } declaredRoot || Trimmed(root, "versionAuthority") is null)
            {
                return Refuse("The normalized Forms representation is missing the source root or version authority it must record.");
            }

            if (SourceRootBinding(declaredRoot, expectedSourceRoot) is { } bindingError)
            {
                return Refuse(bindingError);
            }

            string sourceRoot = WorkspacePath.Normalize(declaredRoot);

            if (Trimmed(root, "formsFamily") is not { } formsFamily)
            {
                return Refuse("The normalized Forms representation records no non-empty 'formsFamily', so the release every module below is attributed to is unstated.");
            }

            if (FamilyRejection(formsFamily, "The normalized Forms representation") is { } familyError)
            {
                return Refuse(familyError);
            }

            JsonElement modules = root.GetProperty("modules");
            if (modules.ValueKind != JsonValueKind.Array)
            {
                return Refuse("The 'modules' field of the normalized Forms representation is not an array.");
            }

            if (modules.GetArrayLength() > MaxModules)
            {
                return Refuse(Overflow("module", modules.GetArrayLength(), MaxModules));
            }

            List<FormsModule> read = [];
            HashSet<string> identities = new(StringComparer.Ordinal);

            foreach (JsonElement module in modules.EnumerateArray())
            {
                if (module.ValueKind != JsonValueKind.Object)
                {
                    return Refuse("A module entry in the normalized Forms representation is not a JSON object.");
                }

                if (Identity(module) is not { } name)
                {
                    return Refuse("A module in the normalized Forms representation has no non-empty 'name'.");
                }

                (string? sourcePath, string? provenanceError) = Provenance(module, name, formsFamily, sourceRoot);
                if (sourcePath is null)
                {
                    return Refuse(provenanceError!);
                }

                // Two directories carrying one module name carry two modules, so identity is the name
                // qualified by the directory it was supplied from. Refusing on the bare name rejected an
                // estate that had supplied a readable export for both.
                if (!identities.Add(QualifiedIdentity(sourcePath, name)))
                {
                    return Refuse(Duplicate("module", $"{WorkspacePath.Folder(sourcePath)}/{name}"));
                }

                (string? title, string? titleError) = OptionalText(module, "title", $"module '{name}'");
                if (titleError is not null)
                {
                    return Refuse(titleError);
                }

                (IReadOnlyList<FormsBlock>? blocks, string? blockError) = ReadBlocks(module, name);
                if (blocks is null)
                {
                    return Refuse(blockError!);
                }

                (IReadOnlyList<FormsTrigger>? triggers, string? triggerError) =
                    ReadTriggers(module, $"module '{name}'", new HashSet<string>(StringComparer.Ordinal) { name });
                (IReadOnlyList<string>? units, string? unitError) = Strings(module, "programUnits", $"module '{name}'");
                (IReadOnlyList<string>? lovs, string? lovError) = Strings(module, "lovs", $"module '{name}'");

                if (triggers is null || units is null || lovs is null)
                {
                    return Refuse(triggerError ?? unitError ?? lovError!);
                }

                read.Add(new FormsModule(
                    name,
                    title,
                    blocks,
                    triggers,
                    units,
                    lovs,
                    sourcePath));
            }

            return new FormsIntermediateRead(read, null);
        }
    }

    /// <summary>
    /// Checks that the representation belongs to the run that is reading it.
    ///
    /// The source root was previously written by the producer and never compared to anything, so an IR
    /// left in a session workspace by an earlier run — or edited by hand — could be read against a
    /// different estate entirely, and every screen generated from it would be attributed to modules this
    /// run never normalized. It has to be the active root exactly, not a related one.
    /// </summary>
    private static string? SourceRootBinding(string declaredRoot, string expectedSourceRoot)
    {
        if (WorkspacePath.Validate(declaredRoot, "The 'sourceRoot' of the normalized Forms representation") is { } declaredError)
        {
            return $"{declaredError} A representation this fleet wrote records a source root inside the session workspace.";
        }

        if (WorkspacePath.Validate(expectedSourceRoot, "The source root of the run reading the normalized Forms representation") is { } expectedError)
        {
            return $"{expectedError} Nothing was read, because the root the representation would have to match is not itself a workspace path.";
        }

        string declared = WorkspacePath.Normalize(declaredRoot);
        string expected = WorkspacePath.Normalize(expectedSourceRoot);

        return string.Equals(declared, expected, StringComparison.Ordinal)
            ? null
            : $"The normalized Forms representation records source root '{declared}' while this run is executing against '{expected}'. " +
              "A representation belongs to the run that wrote it, so reading it here would attribute generated screens to modules " +
              "this run never normalized. Re-run source normalization against this source root.";
    }

    /// <summary>
    /// A module identity scoped to the directory its export was supplied from, matched to the same rule the
    /// normalization phase applies when it decides coverage. Case-folded because the file systems these
    /// estates arrive on treat both the directory and the module name that way.
    /// </summary>
    private static string QualifiedIdentity(string sourcePath, string name) =>
        FormsModuleIdentity.Qualified(sourcePath, name);

    private static (IReadOnlyList<FormsBlock>? Blocks, string? Error) ReadBlocks(JsonElement module, string moduleName)
    {
        (JsonElement declared, string? error) = ArrayField(module, "blocks", $"module '{moduleName}'", MaxChildren);
        if (error is not null)
        {
            return (null, error);
        }

        List<FormsBlock> blocks = [];
        HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);

        foreach (JsonElement block in declared.EnumerateArray())
        {
            if (block.ValueKind != JsonValueKind.Object)
            {
                return (null, $"A block entry in module '{moduleName}' is not a JSON object.");
            }

            if (Identity(block) is not { } name)
            {
                return (null, $"A block in module '{moduleName}' has no non-empty 'name'.");
            }

            if (!names.Add(name))
            {
                return (null, Duplicate($"block in module '{moduleName}'", name));
            }

            string scope = $"{moduleName}.{name}";

            (int? records, string? countError) = Count(block, "recordsDisplayed", $"block '{scope}'");
            if (records is null)
            {
                return (null, countError!);
            }

            (IReadOnlyList<FormsItem>? items, string? itemError) = ReadItems(block, scope);
            if (items is null)
            {
                return (null, itemError!);
            }

            HashSet<string> triggerScopes = new(StringComparer.Ordinal)
            {
                name,
            };
            triggerScopes.UnionWith(items.Select(item => $"{name}.{item.Name}"));

            (IReadOnlyList<FormsTrigger>? triggers, string? triggerError) =
                ReadTriggers(block, $"block '{scope}'", triggerScopes);
            if (triggers is null)
            {
                return (null, triggerError!);
            }

            (string? baseTable, string? baseTableError) = OptionalText(block, "baseTable", $"block '{scope}'");
            if (baseTableError is not null)
            {
                return (null, baseTableError);
            }

            blocks.Add(new FormsBlock(
                name,
                baseTable,
                records.Value,
                items,
                triggers));
        }

        return (blocks, null);
    }

    private static (IReadOnlyList<FormsItem>? Items, string? Error) ReadItems(JsonElement block, string scope)
    {
        (JsonElement declared, string? error) = ArrayField(block, "items", $"block '{scope}'", MaxChildren);
        if (error is not null)
        {
            return (null, error);
        }

        List<FormsItem> items = [];
        HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);

        foreach (JsonElement item in declared.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                return (null, $"An item entry in block '{scope}' is not a JSON object.");
            }

            if (Identity(item) is not { } name)
            {
                return (null, $"An item in block '{scope}' has no non-empty 'name'.");
            }

            if (!names.Add(name))
            {
                return (null, Duplicate($"item in block '{scope}'", name));
            }

            if (Text(item, "itemType") is not { Length: > 0 } itemType)
            {
                return (null, $"Item '{scope}.{name}' has no 'itemType'.");
            }

            (bool? required, string? requiredError) = Flag(item, "required", $"item '{scope}.{name}'");
            (bool? visible, string? visibleError) = Flag(item, "visible", $"item '{scope}.{name}'");

            if (required is null || visible is null)
            {
                return (null, requiredError ?? visibleError!);
            }

            (string? dataType, string? dataTypeError) = OptionalText(item, "dataType", $"item '{scope}.{name}'");
            (string? columnName, string? columnError) = OptionalText(item, "columnName", $"item '{scope}.{name}'");
            (string? prompt, string? promptError) = OptionalText(item, "prompt", $"item '{scope}.{name}'");
            (int? maxLength, string? maxLengthError) = OptionalCount(item, "maxLength", $"item '{scope}.{name}'");

            if ((dataTypeError ?? columnError ?? promptError ?? maxLengthError) is { } optionalError)
            {
                return (null, optionalError);
            }

            items.Add(new FormsItem(
                name,
                itemType,
                dataType,
                columnName,
                prompt,
                required.Value,
                visible.Value,
                maxLength));
        }

        return (items, null);
    }

    /// <summary>
    /// Checks the provenance a module has to carry: where it was read from, and which Forms release the
    /// export it came from declared.
    ///
    /// These fields were previously written by the producer and never read back, so a document could name
    /// a module with no source file, a source file outside the workspace, or a release that contradicted
    /// the one the document as a whole records, and the converter generated from it regardless. Every
    /// generated artifact is attributed to the module named here, so an unattributable module is refused
    /// rather than read.
    ///
    /// Being inside the workspace is not enough: the path has to sit under the source root this run
    /// normalized, matched on whole segments so a sibling directory whose name merely begins with the
    /// root's cannot pass as one beneath it.
    /// </summary>
    private static (string? SourcePath, string? Error) Provenance(JsonElement module, string name, string formsFamily, string sourceRoot)
    {
        if (Trimmed(module, "sourcePath") is not { } sourcePath)
        {
            return (null, $"Module '{name}' in the normalized Forms representation records no non-empty 'sourcePath', so the export it was read " +
                "from cannot be named and nothing generated from it could be attributed to a source file.");
        }

        if (WorkspacePath.Validate(sourcePath, $"The 'sourcePath' of module '{name}'") is { } pathError)
        {
            return (null, $"{pathError} A normalized representation this fleet wrote records every module against a path inside the session workspace.");
        }

        string normalizedPath = WorkspacePath.Normalize(sourcePath);

        if (!WorkspacePath.IsWithin(sourceRoot, normalizedPath))
        {
            return (null, $"Module '{name}' in the normalized Forms representation records source path '{normalizedPath}', which is not under the " +
                $"source root '{sourceRoot}' the same representation declares. A module read from outside the root this run normalized cannot be " +
                "attributed to this estate, so nothing was read from the document.");
        }

            if (!FormsModuleIdentity.MatchesFile(normalizedPath, name))
            {
                return (null,
                $"Module '{name}' in the normalized Forms representation records source path '{normalizedPath}', whose file name does not " +
                "identify that module under the normalization rule. A representation this fleet wrote requires the export file and embedded " +
                "module identity to agree, so nothing was read from the document.");
            }

        if (Trimmed(module, "declaredFamily") is not { } declaredFamily)
        {
            return (null, $"Module '{name}' in the normalized Forms representation records no non-empty 'declaredFamily', so the release its export " +
                "declared is unstated.");
        }

        if (FamilyRejection(declaredFamily, $"Module '{name}'") is { } familyError)
        {
            return (null, familyError);
        }

        // 'unknown' is what the producer writes when the export declared no version at all, so it agrees
        // with any release the document as a whole settled on. A named family has to be that same release.
        bool unknown = string.Equals(declaredFamily, OracleLegacyVersionCatalog.UnknownFamily, StringComparison.Ordinal);

        if (!unknown && !string.Equals(declaredFamily, formsFamily, StringComparison.Ordinal))
        {
            return (null, $"Module '{name}' declares Oracle Forms family '{declaredFamily}' while the normalized Forms representation records " +
                $"'{formsFamily}' for the estate. One run cannot have normalized two releases, so the document was refused rather than " +
                "generating a module against a release it contradicts.");
        }

        (string? declaredVersion, string? versionError) = OptionalText(module, "declaredVersion", $"module '{name}'");
        if (versionError is not null)
        {
            return (null, versionError);
        }

        if (declaredVersion is null)
        {
            return (normalizedPath, null);
        }

        if (declaredVersion.Trim() is not { Length: > 0 } version)
        {
            return (null, $"Module '{name}' declares an empty 'declaredVersion'. A version field present and blank is not the same as an export " +
                "that declared no version, and reading it as one would record a release nothing stated.");
        }

        OracleVersionAssessment assessment = OracleLegacyVersionCatalog.Forms(version);

        if (!assessment.IsRecognized)
        {
            return (null, $"Module '{name}' declares Oracle Forms version '{version}', which matches no release this catalog knows. " +
                "The version a module is attributed to has to be interpretable, so the document was refused rather than read.");
        }

        return string.Equals(assessment.Family, declaredFamily, StringComparison.Ordinal)
            ? (normalizedPath, null)
            : (null, $"Module '{name}' declares Oracle Forms version '{version}', which this catalog reads as family '{assessment.Family}', " +
              $"while the same module records family '{declaredFamily}'. The two disagree about one module, so nothing was read from it.");
    }

    /// <summary>
    /// Why a Forms family string is not one this fleet writes, or nothing. The producer writes either a
    /// catalog family name or the literal unknown token, never a release and never free text.
    /// </summary>
    private static string? FamilyRejection(string family, string scope)
    {
        if (string.Equals(family, OracleLegacyVersionCatalog.UnknownFamily, StringComparison.Ordinal))
        {
            return null;
        }

        OracleVersionAssessment assessment = OracleLegacyVersionCatalog.Forms(family);

        return assessment.IsRecognized && string.Equals(assessment.Family, family, StringComparison.Ordinal)
            ? null
            : $"{scope} declares Oracle Forms family '{family}', which is not a family name this catalog recognizes. " +
              $"The producer writes a catalog family or '{OracleLegacyVersionCatalog.UnknownFamily}', so this document is not one this fleet wrote.";
    }

    private static FormsIntermediateRead Refuse(string reason) => new(null, reason);

    /// <summary>A trimmed, non-empty name, or nothing. There is no coercion to a placeholder.</summary>
    private static string? Identity(JsonElement element) => Trimmed(element, "name");

    /// <summary>A trimmed, non-empty string field. Absent, null, blank, and any non-string are all nothing.</summary>
    private static string? Trimmed(JsonElement element, string name) =>
        Text(element, name)?.Trim() is { Length: > 0 } trimmed ? trimmed : null;

    private static string Duplicate(string kind, string name) =>
        $"The normalized Forms representation declares more than one {kind} named '{name}'. Identities have to be unique " +
        "for a generated artifact to be attributable to one source element.";

    private static string Overflow(string kind, int declared, int limit) =>
        $"The normalized Forms representation declares {declared.ToString(CultureInfo.InvariantCulture)} {kind} entries and this build " +
        $"reads at most {limit.ToString(CultureInfo.InvariantCulture)}. It was refused rather than read in part, because a truncated " +
        "estate generated in full would look complete.";

    private static (JsonElement Declared, string? Error) ArrayField(JsonElement element, string name, string scope, int limit)
    {
        if (!element.TryGetProperty(name, out JsonElement declared))
        {
            return (default, $"{scope} has no '{name}' field.");
        }

        if (declared.ValueKind != JsonValueKind.Array)
        {
            return (default, $"The '{name}' field of {scope} is not an array.");
        }

        return declared.GetArrayLength() > limit
            ? (default, Overflow($"{name} in {scope}", declared.GetArrayLength(), limit))
            : (declared, null);
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static (int? Value, string? Error) Count(JsonElement element, string name, string scope)
    {
        if (!element.TryGetProperty(name, out JsonElement value))
        {
            return (null, $"{scope} has no '{name}' field.");
        }

        return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int number) && number >= 0
            ? (number, null)
            : (null, $"The '{name}' field of {scope} is not a non-negative whole number.");
    }

    /// <summary>
    /// An optional string field. Absent and JSON null both read as nothing; any other non-string is a
    /// refusal rather than a silent null, because a prompt supplied as a number used to disappear from the
    /// generated screen without a word.
    /// </summary>
    private static (string? Value, string? Error) OptionalText(JsonElement element, string name, string scope)
    {
        if (!element.TryGetProperty(name, out JsonElement value) || value.ValueKind == JsonValueKind.Null)
        {
            return (null, null);
        }

        return value.ValueKind == JsonValueKind.String
            ? (value.GetString(), null)
            : (null, $"The '{name}' field of {scope} is present and is not a string.");
    }

    /// <summary>
    /// An optional count. Absent and JSON null read as nothing; a string, a fraction, a negative, or a
    /// value outside the 32-bit range is a refusal, because each of those used to read back as no limit
    /// at all and the generated field silently accepted input the source module bounded.
    /// </summary>
    private static (int? Value, string? Error) OptionalCount(JsonElement element, string name, string scope)
    {
        if (!element.TryGetProperty(name, out JsonElement value) || value.ValueKind == JsonValueKind.Null)
        {
            return (null, null);
        }

        return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int number) && number >= 0
            ? (number, null)
            : (null, $"The '{name}' field of {scope} is present and is not a non-negative whole number within the 32-bit range.");
    }

    private static (bool? Value, string? Error) Flag(JsonElement element, string name, string scope)
    {
        if (!element.TryGetProperty(name, out JsonElement value))
        {
            return (null, $"{scope} has no '{name}' field.");
        }

        return value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? (value.GetBoolean(), null)
            : (null, $"The '{name}' field of {scope} is not a boolean.");
    }

    private static (IReadOnlyList<string>? Value, string? Error) Strings(JsonElement element, string name, string scope)
    {
        (JsonElement declared, string? error) = ArrayField(element, name, scope, MaxChildren);
        if (error is not null)
        {
            return (null, error);
        }

        List<string> read = [];
        foreach (JsonElement entry in declared.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.String || entry.GetString()?.Trim() is not { Length: > 0 } text)
            {
                return (null, $"The '{name}' field of {scope} contains an entry that is not a non-empty string.");
            }

            read.Add(text);
        }

        return (read, null);
    }

    private static (IReadOnlyList<FormsTrigger>? Value, string? Error) ReadTriggers(
        JsonElement element,
        string scope,
        IReadOnlySet<string> allowedScopes)
    {
        (JsonElement declared, string? error) = ArrayField(element, "triggers", scope, MaxChildren);
        if (error is not null)
        {
            return (null, error);
        }

        List<FormsTrigger> read = [];
        HashSet<string> identities = new(StringComparer.OrdinalIgnoreCase);
        foreach (JsonElement entry in declared.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object || Identity(entry) is not { } name)
            {
                return (null, $"The 'triggers' field of {scope} contains an entry that is not an object with a non-empty 'name'.");
            }

            (string? body, string? bodyError) = OptionalText(entry, "body", $"trigger '{name}' in {scope}");
            if (bodyError is not null)
            {
                return (null, bodyError);
            }

            if (body is not null && string.IsNullOrWhiteSpace(body))
            {
                return (null, $"The 'body' field of trigger '{name}' in {scope} is blank. A trigger without retained text is recorded as null.");
            }

            if (body?.Length > FormsXmlDocument.MaxTriggerBodyCharacters)
            {
                return (null,
                    $"The 'body' field of trigger '{name}' in {scope} contains {body.Length.ToString(CultureInfo.InvariantCulture)} characters " +
                    $"and this build reads at most {FormsXmlDocument.MaxTriggerBodyCharacters.ToString(CultureInfo.InvariantCulture)}. It was refused rather than truncated.");
            }

            if (Trimmed(entry, "scope") is not { } scopeValue)
            {
                return (null, $"Trigger '{name}' in {scope} has no non-empty 'scope'.");
            }

            if (!allowedScopes.Contains(scopeValue))
            {
                return (null, $"Trigger '{name}' in {scope} declares scope '{scopeValue}', which is outside the containing module or block.");
            }

            (string? encodingText, string? encodingError) = OptionalText(entry, "bodyEncoding", $"trigger '{scopeValue}.{name}'");
            if (encodingError is not null)
            {
                return (null, encodingError);
            }

            FormsTriggerBodyEncoding? encoding = null;
            if (body is null && encodingText is not null)
            {
                return (null, $"Trigger '{scopeValue}.{name}' records a body encoding but no body.");
            }

            if (body is not null)
            {
                encoding = encodingText switch
                {
                    nameof(FormsTriggerBodyEncoding.Attribute) => FormsTriggerBodyEncoding.Attribute,
                    nameof(FormsTriggerBodyEncoding.Element) => FormsTriggerBodyEncoding.Element,
                    _ => null,
                };

                if (encoding is null)
                {
                    return (null, $"Trigger '{scopeValue}.{name}' has a body but no recognized 'bodyEncoding'.");
                }
            }

            string identity = $"{scopeValue}\0{name}";
            if (!identities.Add(identity))
            {
                return (null, Duplicate($"trigger in {scope}", $"{scopeValue}.{name}"));
            }

            read.Add(new FormsTrigger(name, scopeValue, body, encoding));
        }

        return (read, null);
    }
}
