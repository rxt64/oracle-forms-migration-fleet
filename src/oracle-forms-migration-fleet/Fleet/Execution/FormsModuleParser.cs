// Copyright (c) Microsoft. All rights reserved.

using System.Xml;
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

public sealed record FormsTrigger(string Name, string Scope);

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
    IReadOnlyList<string> Lovs);

public sealed record FormsModuleParse(
    IReadOnlyList<FormsModule> Modules,
    IReadOnlyList<ConversionFinding> Findings);

/// <summary>
/// Reads an Oracle Forms XML export, the text interchange format produced by the frmf2xml converter.
///
/// A .fmb is a proprietary binary this build cannot open, so the XML export is the only Forms source it
/// can honestly claim to have read. What it recovers is structure: blocks, their base tables, items with
/// their prompts, order, and required flags. It recovers no behaviour. Trigger bodies are PL/SQL that
/// this converter does not translate, and a trigger is reported by name so the work stays visible rather
/// than looking absent.
/// </summary>
public static class FormsModuleParser
{
    public static FormsModuleParse Parse(string? xml)
    {
        List<FormsModule> modules = [];
        List<ConversionFinding> findings = [];

        if (string.IsNullOrWhiteSpace(xml) || !xml.Contains("<", StringComparison.Ordinal))
        {
            return new FormsModuleParse(modules, findings);
        }

        XDocument document;
        try
        {
            // No DTD processing and no resolver: an export is untrusted input, and an external entity
            // reference in one would otherwise read files from the host.
            using StringReader text = new(xml);
            using XmlReader reader = XmlReader.Create(text, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                IgnoreComments = true,
                IgnoreWhitespace = true,
            });

            document = XDocument.Load(reader);
        }
        catch (XmlException exception)
        {
            findings.Add(new ConversionFinding(
                ConversionSeverity.Unsupported,
                "Forms module",
                "Forms XML export",
                $"The export could not be parsed as XML, so no module was read: {exception.Message}"));

            return new FormsModuleParse(modules, findings);
        }

        foreach (XElement module in Elements(document.Root, "FormModule"))
        {
            modules.Add(ReadModule(module, findings));
        }

        if (modules.Count == 0 && document.Root is not null)
        {
            findings.Add(new ConversionFinding(
                ConversionSeverity.ManualReview,
                "Forms module",
                document.Root.Name.LocalName,
                "The file parsed as XML but contained no FormModule element, so it was not treated as a Forms export."));
        }

        return new FormsModuleParse(modules, findings);
    }

    private static FormsModule ReadModule(XElement module, List<ConversionFinding> findings)
    {
        string name = Attribute(module, "Name") ?? "UNNAMED";
        List<FormsBlock> blocks = [];

        foreach (XElement block in Descendants(module, "Block"))
        {
            string blockName = Attribute(block, "Name") ?? "UNNAMED";

            // Exports qualify the table with its owner; the converted schema is not owner-qualified.
            string? baseTable = Attribute(block, "QueryDataSourceName") ?? Attribute(block, "DMLDataTargetName");
            if (baseTable is { Length: > 0 } qualified && qualified.Contains('.', StringComparison.Ordinal))
            {
                baseTable = qualified[(qualified.LastIndexOf('.') + 1)..];
            }

            List<FormsItem> items = [];
            foreach (XElement item in Descendants(block, "Item"))
            {
                string itemName = Attribute(item, "Name") ?? "UNNAMED";
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
                [.. Descendants(block, "Trigger").Select(trigger => new FormsTrigger(
                    Attribute(trigger, "Name") ?? "UNNAMED", blockName))];

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
            [.. module.Elements().Where(child => child.Name.LocalName == "Trigger")
                .Select(trigger => new FormsTrigger(Attribute(trigger, "Name") ?? "UNNAMED", name))];

        List<string> programUnits =
            [.. Descendants(module, "ProgramUnit").Select(unit => Attribute(unit, "Name") ?? "UNNAMED")];

        List<string> lovs =
            [.. Descendants(module, "LOV").Select(lov => Attribute(lov, "Name") ?? "UNNAMED")];

        ReportBehaviour(name, blocks, moduleTriggers, programUnits, lovs, findings);
        ReportAttachments(name, module, findings);

        return new FormsModule(name, Attribute(module, "Title"), blocks, moduleTriggers, programUnits, lovs);
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

    private static IEnumerable<XElement> Elements(XElement? root, string localName)
    {
        if (root is null)
        {
            yield break;
        }

        // An export often has FormModule as its root, which Descendants alone would skip.
        if (root.Name.LocalName == localName)
        {
            yield return root;
        }

        foreach (XElement element in root.Descendants().Where(element => element.Name.LocalName == localName))
        {
            yield return element;
        }
    }

    private static IEnumerable<XElement> Descendants(XElement? root, string localName) =>
        root is null ? [] : root.Descendants().Where(element => element.Name.LocalName == localName);

    /// <summary>Prompts carry their punctuation; a column heading should not.</summary>
    private static string? Label(string? prompt) =>
        prompt?.TrimEnd(':', ' ') is { Length: > 0 } trimmed ? trimmed : null;

    private static string? Attribute(XElement element, string name) =>
        element.Attributes().FirstOrDefault(attribute =>
            string.Equals(attribute.Name.LocalName, name, StringComparison.OrdinalIgnoreCase))?.Value is { Length: > 0 } value
            ? value
            : null;

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
