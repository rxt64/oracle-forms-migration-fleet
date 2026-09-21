// Copyright (c) Microsoft. All rights reserved.

using System.Globalization;
using System.Text.Json;
using System.Xml.Linq;

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
    public const string SchemaVersion = "3";

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

            // A duplicate key is not a harmless oddity here: the parser keeps the first and a reviewer
            // reading the document sees the last, so a tampered copy can show one value and be read as another.
            if (DuplicateKey(root) is { } duplicateKey)
            {
                return Refuse(
                    $"The normalized Forms representation declares the key '{duplicateKey}' more than once in one object. " +
                    "A document this fleet wrote carries each key once, so it was refused rather than read with whichever " +
                    "occurrence happened to win.");
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

                (FormsSourceFactSet? facts, string? factError) = ReadSourceFacts(module, name);
                if (facts is null)
                {
                    return Refuse(factError!);
                }

                FormsModule interpreted = new(
                    name,
                    title,
                    blocks,
                    triggers,
                    units,
                    lovs,
                    sourcePath,
                    facts);

                if (Reconcile(interpreted, facts) is { } divergence)
                {
                    return Refuse(divergence);
                }

                read.Add(interpreted);
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

    /// <summary>
    /// Reads the retained source facts of one module.
    ///
    /// These are the only record of what the export declared beyond the small structure the parser
    /// interprets, so a damaged inventory is refused rather than read short: a fact set missing entries, or
    /// whose parent and path relationships disagree, would read back as an export that genuinely declared
    /// less and an omission would be indistinguishable from an absence.
    ///
    /// Nothing here is interpreted. A fact's kind has to be Declared, because this fleet writes no other
    /// kind and a document claiming an inferred or defaulted fact is not one it wrote.
    /// </summary>
    private static (FormsSourceFactSet? Facts, string? Error) ReadSourceFacts(JsonElement module, string moduleName)
    {
        string scope = $"module '{moduleName}'";

        if (!module.TryGetProperty("sourceFacts", out JsonElement set) || set.ValueKind != JsonValueKind.Object)
        {
            return (null, $"{scope} has no 'sourceFacts' object. Schema version '{SchemaVersion}' retains the export's declared " +
                "elements and attributes, and a module without them records no evidence of what it dropped. Re-import source to regenerate this format.");
        }

        if (Trimmed(set, "textDigest") is not { } digest
            || digest.Length != FormsSourceFactReader.DigestCharacters
            || !digest.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f'))
        {
            return (null, $"The 'sourceFacts' of {scope} records no 'textDigest' in the lowercase hexadecimal SHA-256 form this fleet writes, " +
                "so the export text these facts were read from cannot be identified.");
        }

        (string? wrapperVersion, string? wrapperError) = OptionalText(set, "wrapperDeclaredVersion", $"the 'sourceFacts' of {scope}");
        if (wrapperError is not null)
        {
            return (null, wrapperError);
        }

        (JsonElement declared, string? arrayError) = ArrayField(set, "facts", $"the 'sourceFacts' of {scope}", FormsSourceFactReader.MaxFacts);
        if (arrayError is not null)
        {
            return (null, arrayError);
        }

        if (declared.GetArrayLength() == 0)
        {
            return (null, $"The 'sourceFacts' of {scope} declares no facts. Every module carries at least its own element, so an empty " +
                "inventory is not one this fleet wrote.");
        }

        List<FormsSourceFact> facts = [];
        HashSet<string> identities = new(StringComparer.Ordinal);

        // The elements still open above the fact being read, innermost last. The producer writes facts
        // depth first, so a parent that has already been closed means these are not the facts it wrote.
        List<string> open = [];

        // How many direct children each parent has already declared, so childIndex is checked against the
        // sequence actually present rather than against itself.
        Dictionary<string, int> children = new(StringComparer.Ordinal);

        // How many preceding siblings of each qualified name each parent has already declared, which is the
        // counter the producer writes into a path step. Without it a step could carry any positive index.
        Dictionary<string, int> siblings = new(StringComparer.Ordinal);

        foreach (JsonElement entry in declared.EnumerateArray())
        {
            int order = facts.Count;
            string where = $"source fact {order.ToString(CultureInfo.InvariantCulture)} of {scope}";

            if (entry.ValueKind != JsonValueKind.Object)
            {
                return (null, $"{where} is not a JSON object.");
            }

            if (Trimmed(entry, "id") is not { } id || id.Length > FormsSourceFactReader.MaxIdCharacters)
            {
                return (null, $"{where} has no non-empty 'id' within {FormsSourceFactReader.MaxIdCharacters.ToString(CultureInfo.InvariantCulture)} characters, " +
                    "so the source object it retains cannot be named.");
            }

            // Checked rather than claimed, so this id is not yet an identity a later parent lookup can
            // resolve against: a fact naming itself as its own parent has to fail the lookup below.
            if (identities.Contains(id))
            {
                return (null, Duplicate($"source fact in {scope}", id));
            }

            (int? declaredOrder, string? orderError) = Count(entry, "order", where);
            if (declaredOrder is null)
            {
                return (null, orderError!);
            }

            if (declaredOrder.Value != order)
            {
                return (null, $"{where} declares order {declaredOrder.Value.ToString(CultureInfo.InvariantCulture)} at position " +
                    $"{order.ToString(CultureInfo.InvariantCulture)}. Document order is what makes an omission detectable, so a " +
                    "representation whose order disagrees with its own sequence was refused.");
            }

            (int? childIndex, string? childError) = Count(entry, "childIndex", where);
            if (childIndex is null)
            {
                return (null, childError!);
            }

            (string? parentId, string? parentError) = OptionalText(entry, "parentId", where);
            if (parentError is not null)
            {
                return (null, parentError);
            }

            string step;

            if (order == 0)
            {
                if (parentId is not null)
                {
                    return (null, $"{where} is the module element and declares a parent. The first fact of a module is the module itself.");
                }

                step = id;
            }
            else if (parentId is not { Length: > 0 })
            {
                return (null, $"{where} declares no 'parentId'. Only the module element has no parent, and it is the first fact.");
            }
            else if (!identities.Contains(parentId))
            {
                return (null, $"{where} declares parent '{parentId}', which is not a fact declared before it. A retained element has to sit " +
                    "under one this representation already recorded.");
            }
            else if (!id.StartsWith(parentId + "/", StringComparison.Ordinal)
                || id[(parentId.Length + 1)..] is not { Length: > 0 } declaredStep
                || declaredStep.Contains("]/", StringComparison.Ordinal))
            {
                return (null, $"{where} declares id '{id}' under parent '{parentId}'. A source-object path is its parent's plus one step, so the two " +
                    "disagree about where this element sits.");
            }
            else
            {
                while (open.Count > 0 && !string.Equals(open[^1], parentId, StringComparison.Ordinal))
                {
                    open.RemoveAt(open.Count - 1);
                }

                if (open.Count == 0)
                {
                    return (null, $"{where} declares parent '{parentId}', which this representation had already closed. Facts are written " +
                        "depth first from the module element, so a set that revisits a finished branch is not one this fleet wrote.");
                }

                // The producer refuses an export nested deeper than this rather than retaining part of it,
                // so a set that declares a deeper element was never written by it, and reading one would
                // accept a tree the retaining side would have thrown out.
                if (open.Count + 1 > FormsSourceFactReader.MaxDepth)
                {
                    return (null, $"{where} sits {(open.Count + 1).ToString(CultureInfo.InvariantCulture)} elements deep and this build reads at " +
                        $"most {FormsSourceFactReader.MaxDepth.ToString(CultureInfo.InvariantCulture)}, which is the depth the retaining side " +
                        "keeps. It was refused rather than read in part.");
                }

                step = declaredStep;
            }

            if (Trimmed(entry, "localName") is not { } localName)
            {
                return (null, $"{where} has no non-empty 'localName'.");
            }

            if (!entry.TryGetProperty("namespace", out JsonElement namespaceValue) || namespaceValue.ValueKind != JsonValueKind.String)
            {
                return (null, $"{where} has no 'namespace' string. It is recorded even when empty, so a Forms element and a foreign one " +
                    "carrying the same name stay distinguishable.");
            }

            string declaredNamespace = namespaceValue.GetString() ?? string.Empty;

            string parentKey = parentId ?? string.Empty;
            siblings.TryGetValue($"{parentKey}\0{declaredNamespace}\0{localName}", out int preceding);

            // The module element roots its own id space and is always [1]; every other element carries its
            // position among the preceding siblings sharing its qualified name. Both are counters the
            // producer derives from the export, so an index that agrees with neither is a forged path even
            // when every parent reference around it resolves.
            int expectedPosition = preceding + 1;

            // The path is what every later citation resolves against, so it is not allowed to name one
            // element while the fact beside it names another.
            if (StepPosition(step, localName, declaredNamespace) is not { } position)
            {
                return (null, $"{where} ends its path with '{step}' while declaring the element '{declaredNamespace}:{localName}'. A step names " +
                    "its own element and its position among siblings of that name, from one, so the path and the fact disagree about what was retained.");
            }

            if (position != expectedPosition)
            {
                return (null, $"{where} ends its path with '{step}' while it is element " +
                    $"{expectedPosition.ToString(CultureInfo.InvariantCulture)} of that qualified name under " +
                    $"{(parentId is null ? "the module root" : $"'{parentId}'")}. Sibling position is derived from the export, so a set that " +
                    "numbers it otherwise cites elements the retained tree does not contain.");
            }

            children.TryGetValue(parentKey, out int expectedChildIndex);

            if (childIndex.Value != expectedChildIndex)
            {
                return (null, $"{where} declares child index {childIndex.Value.ToString(CultureInfo.InvariantCulture)} while it is child " +
                    $"{expectedChildIndex.ToString(CultureInfo.InvariantCulture)} of the parent this representation records. Where an element sat " +
                    "among its siblings is a fact about the export, so a set that renumbers it was refused.");
            }

            if (Text(entry, "kind") is not nameof(FormsSourceFactKind.Declared))
            {
                return (null, $"{where} declares a fact kind other than '{nameof(FormsSourceFactKind.Declared)}'. This fleet retains only what " +
                    "the export wrote down, so a document claiming an inferred or defaulted fact is not one it wrote.");
            }

            (string? declaredName, string? nameError) = OptionalText(entry, "declaredName", where);
            if (nameError is not null)
            {
                return (null, nameError);
            }

            (string? text, string? textError) = OptionalText(entry, "text", where);
            if (textError is not null)
            {
                return (null, textError);
            }

            // Whitespace the loader kept is significant text the export preserved, so it is read back as
            // declared. An element that declared no text at all records null, never the empty string.
            if (text is { Length: 0 })
            {
                return (null, $"{where} declares empty direct text. An element with no retained text records null, so an empty value is not one " +
                    "this fleet wrote.");
            }

            if (text?.Length > FormsSourceFactReader.MaxValueCharacters)
            {
                return (null, $"The 'text' of {where} contains {text.Length.ToString(CultureInfo.InvariantCulture)} characters and this build " +
                    $"reads at most {FormsSourceFactReader.MaxValueCharacters.ToString(CultureInfo.InvariantCulture)}. It was refused rather than truncated.");
            }

            (IReadOnlyList<FormsSourceAttribute>? attributes, string? attributeError) = ReadFactAttributes(entry, where);
            if (attributes is null)
            {
                return (null, attributeError!);
            }

            // declaredName is a projection of the unqualified Name attribute, never a second source of it.
            string? nameAttribute = attributes.FirstOrDefault(attribute =>
                attribute.Namespace.Length == 0
                && string.Equals(attribute.Name, "Name", StringComparison.OrdinalIgnoreCase))?.Value;

            if (!string.Equals(declaredName, nameAttribute, StringComparison.Ordinal))
            {
                return (null, $"{where} records declared name {Quote(declaredName)} while its retained attributes declare {Quote(nameAttribute)}. " +
                    "The declared name restates the element's own Name attribute, so a set where the two differ was refused rather than read.");
            }

            identities.Add(id);
            children[parentKey] = expectedChildIndex + 1;
            siblings[$"{parentKey}\0{declaredNamespace}\0{localName}"] = expectedPosition;
            open.Add(id);

            facts.Add(new FormsSourceFact(
                id,
                order,
                parentId,
                childIndex.Value,
                localName,
                declaredNamespace,
                declaredName,
                attributes,
                text,
                FormsSourceFactKind.Declared));
        }

        FormsSourceFact moduleFact = facts[0];

        if (!string.Equals(moduleFact.LocalName, "FormModule", StringComparison.Ordinal)
            || !string.Equals(moduleFact.Namespace, FormsXmlDocument.Namespace, StringComparison.Ordinal))
        {
            return (null, $"The first source fact of {scope} is '{moduleFact.Namespace}:{moduleFact.LocalName}' rather than the module element " +
                $"'{FormsXmlDocument.Namespace}:FormModule'. The inventory of a module begins with the module.");
        }

        return moduleFact.DeclaredName is { } factName && !string.Equals(factName, moduleName, StringComparison.Ordinal)
            ? (null, $"The module element retained for {scope} declares name '{factName}'. The retained facts and the module they belong to " +
                "disagree about which module was read, so nothing was read from the document.")
            : (new FormsSourceFactSet(digest, wrapperVersion, facts), null);
    }

    /// <summary>
    /// The sibling position one step of a source-object path declares, when the step names exactly the
    /// element recorded beside it: the same namespace, the same local name, and a position counted from
    /// one. A step that agrees with nothing lets a fact be cited under a path describing some other element
    /// entirely, so it yields no position rather than a tolerated one.
    /// </summary>
    private static int? StepPosition(string step, string localName, string declaredNamespace)
    {
        if (step.Length < 4 || step[0] != '{' || step[^1] != ']')
        {
            return null;
        }

        int close = step.IndexOf('}', StringComparison.Ordinal);
        int open = step.LastIndexOf('[');

        if (close < 0 || open <= close)
        {
            return null;
        }

        string index = step[(open + 1)..^1];

        return string.Equals(step[1..close], declaredNamespace, StringComparison.Ordinal)
            && string.Equals(step[(close + 1)..open], localName, StringComparison.Ordinal)
            && int.TryParse(index, NumberStyles.None, CultureInfo.InvariantCulture, out int position)
            && position >= 1
            && string.Equals(position.ToString(CultureInfo.InvariantCulture), index, StringComparison.Ordinal)
                ? position
                : null;
    }

    /// <summary>
    /// Checks that the interpreted structure of a module is the structure its own retained facts describe.
    ///
    /// The two used to be read independently and compared only by module name, so a document could declare
    /// a base table, a prompt, a required flag, or a trigger body that no retained element supports, and
    /// the converter generated from the declaration rather than from the source it claims to project. The
    /// facts are rebuilt into the element tree they were read from and put through the same interpretation
    /// the export itself went through, so what generation consumes is the source this representation
    /// retained. The text digest plays no part: it names which export text the facts came from and is not
    /// evidence that anything beside them agrees with it.
    ///
    /// The source path is deliberately not compared, because it is not something an export declares: it is
    /// where the file sat, and it is checked against the run's source root and the module's own identity.
    /// </summary>
    private static string? Reconcile(FormsModule interpreted, FormsSourceFactSet facts)
    {
        (XElement? module, string? rejection) = FormsSourceFactReader.Rebuild(facts);

        if (module is null)
        {
            return $"The source facts retained for module '{interpreted.Name}' do not describe an element tree: {rejection} " +
                "A representation this fleet wrote retains the export it read, so nothing was read from the document.";
        }

        if (FormsXmlDocument.ShapeRejection(module) is { } shape)
        {
            return $"The source facts retained for module '{interpreted.Name}' describe a document this fleet would not have read: {shape}";
        }

        // The projection reports what it could not carry rather than throwing, and the report used to be
        // thrown away here. A retained tree over an element cap projects an empty list and says so in a
        // finding, so a document declaring an empty structure beside an oversized tree compared equal and
        // was read as a module with nothing in it. Structural findings are the same ones normalization
        // refuses to write on — severity and category both — so a representation carrying one is refused
        // rather than reconciled against the emptiness the projection fell back to. Behaviour findings are
        // not among them: the IR retains untranslated Forms behaviour on purpose.
        List<ConversionFinding> findings = [];
        FormsModule projected = FormsModuleParser.Project(module, facts, findings);

        ConversionFinding? blocking = findings.FirstOrDefault(finding =>
            finding.Severity == ConversionSeverity.Unsupported
            && string.Equals(finding.Category, "Forms module", StringComparison.Ordinal));

        return blocking is not null
            ? $"The source facts retained for module '{interpreted.Name}' describe source this fleet would have refused to normalize: " +
              $"{blocking.Construct} — {blocking.Reason} The structure declared beside them cannot be checked against source that was " +
              "never readable as a module, so nothing was read from the document."
            : Divergence(interpreted, projected);
    }

    /// <summary>
    /// The first field on which the interpreted structure and the structure projected from the retained
    /// facts disagree, or nothing. Order is compared as well as content: blocks, items, and triggers are
    /// positions in a source document, so a reordered list is a different module.
    /// </summary>
    private static string? Divergence(FormsModule interpreted, FormsModule projected)
    {
        string scope = $"module '{interpreted.Name}'";

        if (!string.Equals(interpreted.Name, projected.Name, StringComparison.Ordinal))
        {
            return Disagrees(scope, "name", interpreted.Name, projected.Name);
        }

        if (!string.Equals(interpreted.Title, projected.Title, StringComparison.Ordinal))
        {
            return Disagrees(scope, "title", interpreted.Title, projected.Title);
        }

        if (Names(scope, "program unit", interpreted.ProgramUnits, projected.ProgramUnits) is { } unitDivergence)
        {
            return unitDivergence;
        }

        if (Names(scope, "LOV", interpreted.Lovs, projected.Lovs) is { } lovDivergence)
        {
            return lovDivergence;
        }

        if (Triggers(scope, interpreted.Triggers, projected.Triggers) is { } moduleTriggerDivergence)
        {
            return moduleTriggerDivergence;
        }

        if (interpreted.Blocks.Count != projected.Blocks.Count)
        {
            return Disagrees(scope, "block count", Number(interpreted.Blocks.Count), Number(projected.Blocks.Count));
        }

        for (int index = 0; index < interpreted.Blocks.Count; index++)
        {
            FormsBlock declared = interpreted.Blocks[index];
            FormsBlock retained = projected.Blocks[index];
            string blockScope = $"block '{interpreted.Name}.{declared.Name}'";

            if (!string.Equals(declared.Name, retained.Name, StringComparison.Ordinal))
            {
                return Disagrees($"{scope} at block {Number(index)}", "name", declared.Name, retained.Name);
            }

            if (!string.Equals(declared.BaseTable, retained.BaseTable, StringComparison.Ordinal))
            {
                return Disagrees(blockScope, "base table", declared.BaseTable, retained.BaseTable);
            }

            if (declared.RecordsDisplayed != retained.RecordsDisplayed)
            {
                return Disagrees(blockScope, "displayed record count", Number(declared.RecordsDisplayed), Number(retained.RecordsDisplayed));
            }

            if (Items(blockScope, declared.Items, retained.Items) is { } itemDivergence)
            {
                return itemDivergence;
            }

            if (Triggers(blockScope, declared.Triggers, retained.Triggers) is { } blockTriggerDivergence)
            {
                return blockTriggerDivergence;
            }
        }

        return null;
    }

    private static string? Items(string scope, IReadOnlyList<FormsItem> interpreted, IReadOnlyList<FormsItem> projected)
    {
        if (interpreted.Count != projected.Count)
        {
            return Disagrees(scope, "item count", Number(interpreted.Count), Number(projected.Count));
        }

        for (int index = 0; index < interpreted.Count; index++)
        {
            FormsItem declared = interpreted[index];
            FormsItem retained = projected[index];
            string itemScope = $"item {Number(index)} of {scope}";

            if (declared != retained)
            {
                return !string.Equals(declared.Name, retained.Name, StringComparison.Ordinal)
                    ? Disagrees(itemScope, "name", declared.Name, retained.Name)
                    : Disagrees(
                        $"item '{declared.Name}' of {scope}",
                        "declared properties",
                        Describe(declared),
                        Describe(retained));
            }
        }

        return null;
    }

    private static string? Triggers(string scope, IReadOnlyList<FormsTrigger> interpreted, IReadOnlyList<FormsTrigger> projected)
    {
        if (interpreted.Count != projected.Count)
        {
            return Disagrees(scope, "trigger count", Number(interpreted.Count), Number(projected.Count));
        }

        for (int index = 0; index < interpreted.Count; index++)
        {
            FormsTrigger declared = interpreted[index];
            FormsTrigger retained = projected[index];

            if (declared != retained)
            {
                return Disagrees(
                    $"trigger {Number(index)} of {scope}",
                    "identity, scope, body, or body encoding",
                    Describe(declared),
                    Describe(retained));
            }
        }

        return null;
    }

    private static string? Names(string scope, string kind, IReadOnlyList<string> interpreted, IReadOnlyList<string> projected)
    {
        if (interpreted.Count != projected.Count)
        {
            return Disagrees(scope, $"{kind} count", Number(interpreted.Count), Number(projected.Count));
        }

        for (int index = 0; index < interpreted.Count; index++)
        {
            if (!string.Equals(interpreted[index], projected[index], StringComparison.Ordinal))
            {
                return Disagrees(scope, $"{kind} {Number(index)}", interpreted[index], projected[index]);
            }
        }

        return null;
    }

    private static string Disagrees(string scope, string field, string? interpreted, string? projected) =>
        $"The interpreted {field} of {scope} is {Quote(interpreted)} while the source facts retained beside it describe " +
        $"{Quote(projected)}. A representation this fleet wrote projects the export it retained, so the document was refused rather " +
        "than generating from a structure no retained element supports.";

    private static string Describe(FormsItem item) =>
        $"{item.ItemType}, data type {Quote(item.DataType)}, column {Quote(item.ColumnName)}, prompt {Quote(item.Prompt)}, " +
        $"required {item.Required}, visible {item.Visible}, maximum length {(item.MaxLength is { } length ? Number(length) : "none")}";

    private static string Describe(FormsTrigger trigger) =>
        $"'{trigger.Name}' in scope '{trigger.Scope}' with body {Quote(trigger.Body)} supplied as {trigger.BodyEncoding?.ToString() ?? "no body"}";

    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Quote(string? value) => value is null ? "none" : $"'{value}'";

    private static (IReadOnlyList<FormsSourceAttribute>? Attributes, string? Error) ReadFactAttributes(JsonElement fact, string scope)
    {
        (JsonElement declared, string? error) = ArrayField(fact, "attributes", scope, FormsSourceFactReader.MaxAttributes);
        if (error is not null)
        {
            return (null, error);
        }

        List<FormsSourceAttribute> attributes = [];
        HashSet<string> qualified = new(StringComparer.Ordinal);

        foreach (JsonElement entry in declared.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object || Trimmed(entry, "name") is not { } name)
            {
                return (null, $"The 'attributes' of {scope} contains an entry that is not an object with a non-empty 'name'.");
            }

            if (!entry.TryGetProperty("namespace", out JsonElement namespaceValue) || namespaceValue.ValueKind != JsonValueKind.String)
            {
                return (null, $"Attribute '{name}' of {scope} has no 'namespace' string. It is recorded even when empty, so a qualified " +
                    "attribute cannot pass as the unqualified one the interpreting readers look up.");
            }

            string declaredNamespace = namespaceValue.GetString() ?? string.Empty;

            // The retaining side skips namespace declarations, so a set that carries one describes an
            // element tree no export it read could have produced.
            if (string.Equals(declaredNamespace, "http://www.w3.org/2000/xmlns/", StringComparison.Ordinal)
                || (declaredNamespace.Length == 0 && string.Equals(name, "xmlns", StringComparison.Ordinal)))
            {
                return (null, $"Attribute '{name}' of {scope} is a namespace declaration. Declarations are not retained as attributes, " +
                    "so a set that records one is not one this fleet wrote.");
            }

            if (!entry.TryGetProperty("value", out JsonElement value) || value.ValueKind != JsonValueKind.String)
            {
                return (null, $"Attribute '{name}' of {scope} has no 'value' string. A declared attribute with no value is not something " +
                    "an export writes, and reading it as absent would lose a fact this representation exists to retain.");
            }

            string text = value.GetString() ?? string.Empty;

            if (text.Length > FormsSourceFactReader.MaxValueCharacters)
            {
                return (null, $"Attribute '{name}' of {scope} contains {text.Length.ToString(CultureInfo.InvariantCulture)} characters and this " +
                    $"build reads at most {FormsSourceFactReader.MaxValueCharacters.ToString(CultureInfo.InvariantCulture)}. It was refused rather than truncated.");
            }

            if (!qualified.Add($"{declaredNamespace}\0{name}"))
            {
                return (null, Duplicate($"attribute on {scope}", name));
            }

            attributes.Add(new FormsSourceAttribute(name, declaredNamespace, text));
        }

        return (attributes, null);
    }

    /// <summary>The first object key declared more than once anywhere in the document, or nothing.</summary>
    private static string? DuplicateKey(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                HashSet<string> names = new(StringComparer.Ordinal);

                foreach (JsonProperty property in element.EnumerateObject())
                {
                    if (!names.Add(property.Name))
                    {
                        return property.Name;
                    }

                    if (DuplicateKey(property.Value) is { } nested)
                    {
                        return nested;
                    }
                }

                return null;

            case JsonValueKind.Array:
                foreach (JsonElement entry in element.EnumerateArray())
                {
                    if (DuplicateKey(entry) is { } nested)
                    {
                        return nested;
                    }
                }

                return null;

            default:
                return null;
        }
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
