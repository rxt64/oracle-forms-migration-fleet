// Copyright (c) Microsoft. All rights reserved.

using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace OracleFormsMigrationFleet.Fleet.Execution;

public sealed record FormsItem(
    string Name,
    string ItemType,
    string? DataType,
    string? ColumnName,
    string? Prompt,
    bool Required,
    bool Visible,
    int? MaxLength);

public enum FormsTriggerBodyEncoding
{
    Attribute,
    Element,
}

public sealed record FormsTrigger(
    string Name,
    string Scope,
    string? Body = null,
    FormsTriggerBodyEncoding? BodyEncoding = null);

public sealed record FormsBlock(
    string Name,
    string? BaseTable,
    int RecordsDisplayed,
    IReadOnlyList<FormsItem> Items,
    IReadOnlyList<FormsTrigger> Triggers);

public sealed record FormsModule(
    string Name,
    string? Title,
    IReadOnlyList<FormsBlock> Blocks,
    IReadOnlyList<FormsTrigger> Triggers,
    IReadOnlyList<string> ProgramUnits,
    IReadOnlyList<string> Lovs,
    string? SourcePath = null)
{
    /// <summary>
    /// The module's name qualified by the directory its export was supplied from, which is the identity
    /// this fleet treats as unique. Two directories carrying <c>ORDERS</c> carry two different modules,
    /// because which module a bare name resolves to is a FORMS_PATH question nothing here can answer.
    ///
    /// <see cref="Name"/> keeps meaning what the export declared and is what every operator-facing string
    /// shows; this is for deciding identity, not for display in place of it.
    /// </summary>
    public string QualifiedName =>
        SourcePath is { Length: > 0 } path && WorkspacePath.Folder(path) is { Length: > 0 } folder
            ? $"{folder}/{Name}"
            : Name;
}

public sealed record FormsModuleParse(
    IReadOnlyList<FormsModule> Modules,
    IReadOnlyList<ConversionFinding> Findings);

internal static class FormsModuleIdentity
{
    private static readonly Regex s_loadOrderPrefix = new(
        @"^\d+[-_. ]+", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250));

    public static string Normalize(string name)
    {
        string trimmed = s_loadOrderPrefix.Replace(name.Trim(), string.Empty);
        StringBuilder identity = new(trimmed.Length);

        foreach (char character in trimmed.ToUpperInvariant())
        {
            char mapped = character is '-' or ' ' or '.' ? '_' : character;

            if (mapped == '_' && (identity.Length == 0 || identity[^1] == '_'))
            {
                continue;
            }

            identity.Append(mapped);
        }

        return identity.ToString().TrimEnd('_');
    }

    public static string Qualified(string sourcePath, string name) =>
        $"{WorkspacePath.Folder(sourcePath).ToUpperInvariant()}|{Normalize(name)}";

    public static bool MatchesFile(string sourcePath, string name) =>
        string.Equals(
            Normalize(Path.GetFileNameWithoutExtension(sourcePath)),
            Normalize(name),
            StringComparison.Ordinal);
}

/// <summary>
/// Reads an Oracle Forms XML export, the text interchange format produced by the frmf2xml converter.
///
/// A .fmb is a proprietary binary this build cannot open, so the XML export is the only Forms source it
/// can honestly claim to have read. What it recovers is structure: blocks, their base tables, items with
/// their prompts, order, and required flags. Trigger bodies are retained as untrusted source text so
/// changed behaviour remains distinguishable, but this parser does not translate or execute that PL/SQL.
///
/// Which documents count as an export is decided by <see cref="FormsXmlDocument"/>, so this parser and
/// <see cref="FormsXmlVersionReader"/> cannot reach different conclusions about the same file.
/// </summary>
public static class FormsModuleParser
{
    private static readonly XNamespace s_forms = FormsXmlDocument.Namespace;

    public static FormsModuleParse Parse(string? xml)
    {
        List<FormsModule> modules = [];
        List<ConversionFinding> findings = [];

        FormsXmlLoad load = FormsXmlDocument.Load(xml);

        if (load.ParseError is { } error)
        {
            findings.Add(new ConversionFinding(
                ConversionSeverity.Unsupported,
                "Forms module",
                "Forms XML export",
                $"The export could not be parsed as XML, so no module was read: {error}"));

            return new FormsModuleParse(modules, findings);
        }

        if (load.Root is not { } root)
        {
            return new FormsModuleParse(modules, findings);
        }

        if (load.ShapeRejection is { } rejection)
        {
            findings.Add(new ConversionFinding(
                ConversionSeverity.Unsupported,
                "Forms module",
                root.Name.LocalName,
                rejection));

            return new FormsModuleParse(modules, findings);
        }

        if (!load.IsFormsExport)
        {
            findings.Add(new ConversionFinding(
                ConversionSeverity.ManualReview,
                "Forms module",
                root.Name.LocalName,
                "The file parsed as XML but contained no FormModule element, so it was not treated as a Forms export."));

            return new FormsModuleParse(modules, findings);
        }

        if (load.FormModules.Count > FormsIntermediateReader.MaxModules)
        {
            findings.Add(LimitFinding(
                "Forms export",
                "module",
                load.FormModules.Count,
                FormsIntermediateReader.MaxModules));

            return new FormsModuleParse(modules, findings);
        }

        HashSet<string> moduleNames = new(StringComparer.OrdinalIgnoreCase);
        foreach (XElement module in load.FormModules)
        {
            string moduleName = Attribute(module, "Name") ?? "UNNAMED";
            if (!moduleNames.Add(moduleName))
            {
                findings.Add(DuplicateFinding("Forms export", "module", moduleName));
            }

            modules.Add(ReadModule(module, findings));
        }

        return new FormsModuleParse(modules, findings);
    }

    private static FormsModule ReadModule(XElement module, List<ConversionFinding> findings)
    {
        string name = Attribute(module, "Name") ?? "UNNAMED";
        List<FormsBlock> blocks = [];
        List<XElement> declaredBlocks = [.. Descendants(module, "Block")];

        if (declaredBlocks.Count > FormsIntermediateReader.MaxChildren)
        {
            findings.Add(LimitFinding(name, "block", declaredBlocks.Count, FormsIntermediateReader.MaxChildren));
            return new FormsModule(name, Attribute(module, "Title"), blocks, [], [], []);
        }

        HashSet<string> blockNames = new(StringComparer.OrdinalIgnoreCase);

        foreach (XElement block in declaredBlocks)
        {
            string blockName = Attribute(block, "Name") ?? "UNNAMED";

            if (!blockNames.Add(blockName))
            {
                findings.Add(DuplicateFinding(name, "block", blockName));
            }

            // Exports qualify the table with its owner; the converted schema is not owner-qualified.
            string? baseTable = Attribute(block, "QueryDataSourceName") ?? Attribute(block, "DMLDataTargetName");
            if (baseTable is { Length: > 0 } qualified && qualified.Contains('.', StringComparison.Ordinal))
            {
                baseTable = qualified[(qualified.LastIndexOf('.') + 1)..];
            }

            List<FormsItem> items = [];
            List<XElement> declaredItems = [.. Descendants(block, "Item")];
            if (declaredItems.Count > FormsIntermediateReader.MaxChildren)
            {
                findings.Add(LimitFinding($"{name}.{blockName}", "item", declaredItems.Count, FormsIntermediateReader.MaxChildren));
                declaredItems = [];
            }

            HashSet<string> itemNames = new(StringComparer.OrdinalIgnoreCase);
            foreach (XElement item in declaredItems)
            {
                string itemName = Attribute(item, "Name") ?? "UNNAMED";

                if (!itemNames.Add(itemName))
                {
                    findings.Add(DuplicateFinding($"{name}.{blockName}", "item", itemName));
                }

                string itemType = Attribute(item, "ItemType") ?? "Text Item";

                // Only a database item maps to a column; the rest are populated by Forms logic.
                bool databaseItem = Flag(item, "DatabaseItem", @default: true);
                string? column = databaseItem ? Attribute(item, "ColumnName") ?? itemName : null;

                items.Add(new FormsItem(
                    itemName,
                    itemType,
                    Attribute(item, "DataType"),
                    baseTable is null ? null : column,
                    Label(Attribute(item, "Prompt")),
                    Flag(item, "Required", @default: false),
                    Flag(item, "Visible", @default: true),
                    Number(item, "MaximumLength")));
            }

            List<FormsTrigger> triggers =
                [.. block.Elements(s_forms + "Trigger").Select(trigger => ReadTrigger(trigger, blockName))];

            foreach (XElement item in Descendants(block, "Item"))
            {
                string itemScope = $"{blockName}.{Attribute(item, "Name") ?? "UNNAMED"}";
                triggers.AddRange(item.Elements(s_forms + "Trigger").Select(trigger => ReadTrigger(trigger, itemScope)));
            }

            if (triggers.Count > FormsIntermediateReader.MaxChildren)
            {
                findings.Add(LimitFinding($"{name}.{blockName}", "trigger", triggers.Count, FormsIntermediateReader.MaxChildren));
                triggers.Clear();
            }

            if (baseTable is null)
            {
                findings.Add(new ConversionFinding(
                    ConversionSeverity.ManualReview,
                    "Forms module",
                    $"{name}.{blockName}",
                    "The block has no base table, so it is a control block whose contents came from Forms logic. " +
                    "No screen was generated for it and nothing populates it."));
            }

            blocks.Add(new FormsBlock(
                blockName,
                baseTable,
                Number(block, "RecordsDisplayed") ?? Number(block, "RecordsDisplayCount") ?? 1,
                items,
                triggers));
        }

        // Triggers directly on the module, not inside a block.
        List<FormsTrigger> moduleTriggers =
            [.. module.Elements().Where(child => child.Name == s_forms + "Trigger")
                .Select(trigger => ReadTrigger(trigger, name))];

        if (moduleTriggers.Count > FormsIntermediateReader.MaxChildren)
        {
            findings.Add(LimitFinding(name, "module trigger", moduleTriggers.Count, FormsIntermediateReader.MaxChildren));
            moduleTriggers.Clear();
        }

        List<string> programUnits =
            [.. Descendants(module, "ProgramUnit").Select(unit => Attribute(unit, "Name") ?? "UNNAMED")];

        List<string> lovs =
            [.. Descendants(module, "LOV").Select(lov => Attribute(lov, "Name") ?? "UNNAMED")];

        if (programUnits.Count > FormsIntermediateReader.MaxChildren)
        {
            findings.Add(LimitFinding(name, "program unit", programUnits.Count, FormsIntermediateReader.MaxChildren));
            programUnits.Clear();
        }

        if (lovs.Count > FormsIntermediateReader.MaxChildren)
        {
            findings.Add(LimitFinding(name, "LOV", lovs.Count, FormsIntermediateReader.MaxChildren));
            lovs.Clear();
        }

        ReportDuplicateTriggers(name, moduleTriggers.Concat(blocks.SelectMany(block => block.Triggers)), findings);

        ReportBehaviour(name, blocks, moduleTriggers, programUnits, lovs, findings);
        ReportAttachments(name, module, findings);

        return new FormsModule(name, Attribute(module, "Title"), blocks, moduleTriggers, programUnits, lovs);
    }

    private static ConversionFinding DuplicateFinding(string scope, string kind, string name) => new(
        ConversionSeverity.Unsupported,
        "Forms module",
        $"{scope}.{name}",
        $"The export declares more than one {kind} named '{name}' in the same scope. The normalized IR requires unique identities, " +
        "so normalization was refused rather than writing a representation its reader would later reject.");

    private static ConversionFinding LimitFinding(string scope, string kind, int count, int limit) => new(
        ConversionSeverity.Unsupported,
        "Forms module",
        scope,
        $"The export declares {count} {kind} entries in this scope and this build retains at most {limit}. Normalization was refused " +
        "rather than writing a representation its reader would later reject or reading the estate in part.");

    private static void ReportDuplicateTriggers(
        string module,
        IEnumerable<FormsTrigger> triggers,
        List<ConversionFinding> findings)
    {
        foreach (IGrouping<(string Scope, string Name), FormsTrigger> duplicate in triggers
            .GroupBy(trigger => (trigger.Scope.ToUpperInvariant(), trigger.Name.ToUpperInvariant()))
            .Where(group => group.Skip(1).Any()))
        {
            FormsTrigger trigger = duplicate.First();
            findings.Add(new ConversionFinding(
                ConversionSeverity.Unsupported,
                "Forms module",
                $"{module}.{trigger.Scope}.{trigger.Name}",
                "The export declares more than one trigger with this scope and name. Their bodies cannot be attributed to a unique " +
                "source event, so normalization was refused rather than carrying ambiguous behavior forward."));
        }
    }

    private static void ReportBehaviour(
        string module,
        IReadOnlyList<FormsBlock> blocks,
        IReadOnlyList<FormsTrigger> moduleTriggers,
        IReadOnlyList<string> programUnits,
        IReadOnlyList<string> lovs,
        List<ConversionFinding> findings)
    {
        foreach (FormsTrigger trigger in moduleTriggers.Concat(blocks.SelectMany(block => block.Triggers)))
        {
            findings.Add(new ConversionFinding(
                ConversionSeverity.Unsupported,
                "Forms behaviour",
                $"{module}.{trigger.Scope}.{trigger.Name}",
                "Forms trigger logic is PL/SQL bound to a client-side event model with no PostgreSQL or React " +
                "equivalent. It was not translated; the generated screen has the field but not this behaviour."));
        }

        foreach (string unit in programUnits)
        {
            findings.Add(new ConversionFinding(
                ConversionSeverity.Unsupported,
                "Forms behaviour",
                $"{module}.{unit}",
                "A program unit inside the module was not translated. It has to be reimplemented in the back end."));
        }

        foreach (string lov in lovs)
        {
            findings.Add(new ConversionFinding(
                ConversionSeverity.ManualReview,
                "Forms behaviour",
                $"{module}.{lov}",
                "A list of values supplied a lookup. The generated screen renders a plain input; the lookup and its " +
                "record group have to be rebuilt."));
        }
    }

    private static void ReportAttachments(string module, XElement element, List<ConversionFinding> findings)
    {
        foreach (XElement library in Descendants(element, "AttachedLibrary"))
        {
            findings.Add(new ConversionFinding(
                ConversionSeverity.Unsupported,
                "Forms behaviour",
                $"{module}.{Attribute(library, "Name") ?? "UNNAMED"}",
                "An attached PL/SQL library (.pll) is shared code this export does not contain, so its contents were " +
                "never seen. Anything the form relied on from it is absent."));
        }

        foreach (XElement relation in Descendants(element, "Relation"))
        {
            findings.Add(new ConversionFinding(
                ConversionSeverity.ManualReview,
                "Forms behaviour",
                $"{module}.{Attribute(relation, "Name") ?? "UNNAMED"}",
                $"A master-detail relation to {Attribute(relation, "DetailBlock") ?? "a detail block"} coordinated two " +
                "blocks. The generated screen renders one block and does not synchronise them."));
        }
    }

    private static IEnumerable<XElement> Descendants(XElement? root, string localName) =>
        root is null ? [] : root.Descendants().Where(element => element.Name == s_forms + localName);

    /// <summary>Prompts carry their punctuation; a column heading should not.</summary>
    private static string? Label(string? prompt) =>
        prompt?.TrimEnd(':', ' ') is { Length: > 0 } trimmed ? trimmed : null;

    /// <summary>
    /// A Forms attribute, matched by local name in no namespace. A qualified attribute is never read here,
    /// so a document cannot supply two candidates for the same name and let position pick the winner;
    /// <see cref="FormsXmlDocument"/> has already refused any document that carries one.
    /// </summary>
    private static string? Attribute(XElement element, string name) =>
        element.Attributes().FirstOrDefault(attribute =>
            attribute.Name.Namespace == XNamespace.None
            && string.Equals(attribute.Name.LocalName, name, StringComparison.OrdinalIgnoreCase))?.Value is { Length: > 0 } value
            ? value
            : null;

    private static FormsTrigger ReadTrigger(XElement trigger, string scope)
    {
        if (trigger.Attribute("TriggerText")?.Value is { } attributeBody)
        {
            return new FormsTrigger(
                Attribute(trigger, "Name") ?? "UNNAMED",
                scope,
                string.IsNullOrWhiteSpace(attributeBody) ? null : attributeBody,
                string.IsNullOrWhiteSpace(attributeBody) ? null : FormsTriggerBodyEncoding.Attribute);
        }

        XElement? bodyElement = trigger.Element(s_forms + "TriggerText");
        string? body = bodyElement is null
            ? null
            : string.Concat(bodyElement.Nodes().OfType<XText>().Select(text => text.Value));

        return new FormsTrigger(
            Attribute(trigger, "Name") ?? "UNNAMED",
            scope,
            string.IsNullOrWhiteSpace(body) ? null : body,
            string.IsNullOrWhiteSpace(body) ? null : FormsTriggerBodyEncoding.Element);
    }

    /// <summary>Exports write booleans as Yes/No, not true/false.</summary>
    private static bool Flag(XElement element, string name, bool @default) => Attribute(element, name) switch
    {
        null => @default,
        "Yes" or "yes" or "YES" or "Y" or "y" or "true" or "True" or "TRUE" or "1" => true,
        _ => false,
    };

    private static int? Number(XElement element, string name) =>
        int.TryParse(Attribute(element, name), out int value) ? value : null;
}
