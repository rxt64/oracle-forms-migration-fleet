// Copyright (c) Microsoft. All rights reserved.

using System.Xml;
using System.Xml.Linq;

namespace OracleFormsMigrationFleet.Fleet.Execution;

/// <summary>Outcome of loading a supplied file as an Oracle Forms XML export.</summary>
/// <param name="Root">The document root, when the text parsed as XML at all.</param>
/// <param name="ParseError">Why the text is not XML, including a refused DTD.</param>
/// <param name="FormModules">FormModule elements in the accepted shape. Empty unless <paramref name="IsFormsExport"/>.</param>
/// <param name="IsFormsExport">True only for a documented root shape in the Oracle Forms namespace.</param>
/// <param name="ShapeRejection">
/// Set when the document names a Forms element but not in a shape this fleet accepts. It is separate from
/// <paramref name="ParseError"/> because "this is some other XML file" and "this claims to be a Forms
/// export and is not one this fleet will read" are different answers, and only the second is a refusal.
/// </param>
public sealed record FormsXmlLoad(
    XElement? Root,
    string? ParseError,
    IReadOnlyList<XElement> FormModules,
    bool IsFormsExport,
    string? ShapeRejection)
{
    public static FormsXmlLoad NotXml { get; } = new(null, null, [], false, null);
}

/// <summary>
/// The single place a supplied file is loaded as an Oracle Forms XML export.
///
/// The version reader and the module parser used to decide independently what counted as an export: one
/// took any element locally named FormModule anywhere in any namespace, the other did the same over a
/// different element set. A document could therefore declare a version to one and no module to the other,
/// and an arbitrary XML file with a FormModule-shaped element could be read as Forms source.
///
/// Only the two root shapes Oracle's Forms2XML converter documents are accepted, and both are required to
/// be in the Forms namespace:
/// <list type="bullet">
/// <item><description><c>Module</c> root containing one or more <c>FormModule</c> elements.</description></item>
/// <item><description><c>FormModule</c> root, the single-module form of the same export.</description></item>
/// </list>
/// A missing, different, or mixed namespace is refused rather than guessed: an export whose namespace does
/// not match is not a document whose element meanings this fleet knows. The requirement holds for every
/// structural element the readers interpret, not only the root, because an attacker who cannot change the
/// root can still graft <c>evil:Block</c>, <c>evil:Item</c>, or <c>evil:Trigger</c> underneath a valid one:
/// the parser matched on local name alone and would have read those as blocks, columns, and triggers of a
/// module Oracle never produced.
/// </summary>
public static class FormsXmlDocument
{
    /// <summary>The namespace Oracle's Forms XML schema declares.</summary>
    public const string Namespace = "http://xmlns.oracle.com/Forms";

    /// <summary>Maximum XML-normalized source characters retained for one trigger body.</summary>
    public const int MaxTriggerBodyCharacters = 200_000;

    private static readonly XNamespace s_forms = Namespace;

    private static readonly XNamespace s_schemaInstance = "http://www.w3.org/2001/XMLSchema-instance";

    /// <summary>
    /// Every local name a reader in this fleet interprets structurally. An element carrying one of these
    /// names outside the Forms namespace is a refusal, because its meaning is not the meaning the readers
    /// would assign it. Names nothing interprets are deliberately absent: refusing a document over an
    /// element no code reads would reject valid exports for no gain.
    /// </summary>
    private static readonly HashSet<string> s_structural = new(StringComparer.OrdinalIgnoreCase)
    {
        "Module", "FormModule", "Block", "Item", "Trigger", "TriggerText", "ProgramUnit", "LOV", "AttachedLibrary", "Relation",
    };

    public static FormsXmlLoad Load(string? xml)
    {
        if (string.IsNullOrWhiteSpace(xml) || !xml.Contains('<', StringComparison.Ordinal))
        {
            return FormsXmlLoad.NotXml;
        }

        XDocument document;
        try
        {
            // The export is untrusted input: an external entity reference would otherwise read host files.
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
            return new FormsXmlLoad(null, exception.Message, [], false, null);
        }

        XElement? root = document.Root;
        if (root is null)
        {
            return FormsXmlLoad.NotXml;
        }

        List<XElement> modules;

        if (root.Name == s_forms + "FormModule")
        {
            modules = [root];
        }
        else if (root.Name == s_forms + "Module")
        {
            modules = [.. root.Elements().Where(element => element.Name == s_forms + "FormModule")];

            if (modules.Count == 0)
            {
                return Names(root).Contains("FormModule", StringComparer.Ordinal)
                    ? new FormsXmlLoad(root, null, [], false,
                        $"The root element is '{Namespace}:Module' but its FormModule element is not a direct child in that namespace, " +
                        "so the document is not a Forms XML export in a shape this fleet reads.")
                    : new FormsXmlLoad(root, null, [], false, null);
            }
        }
        else
        {
            // A Forms-shaped element outside the Forms namespace is refused loudly rather than read as an
            // export or dismissed as an unrelated file: the meaning of its attributes is not established.
            return root.Name.LocalName is "Module" or "FormModule" || Names(root).Contains("FormModule", StringComparer.Ordinal)
                ? new FormsXmlLoad(root, null, [], false, Describe(root))
                : new FormsXmlLoad(root, null, [], false, null);
        }

        // The root shape is right. Everything under it still has to be, or a valid wrapper would launder
        // foreign structure into the module model.
        return Foreign(root) is { } rejection
            ? new FormsXmlLoad(root, null, [], false, rejection)
            : new FormsXmlLoad(root, null, modules, true, null);
    }

    /// <summary>
    /// The first structural element declared outside the Forms namespace, or the first namespace-qualified
    /// attribute on a Forms element, described as a refusal.
    ///
    /// Attributes are required to be unqualified because that is what the readers look up: they match on
    /// local name, so <c>evil:Name</c> and <c>Name</c> are the same key to them and the document would
    /// decide which one wins by position. The W3C <c>xml</c> and XML Schema instance namespaces are
    /// allowed, because an export routinely carries <c>xsi:schemaLocation</c> or <c>xml:lang</c> and
    /// neither names anything this fleet interprets.
    /// </summary>
    private static string? Foreign(XElement root)
    {
        foreach (XElement element in root.DescendantsAndSelf())
        {
            if (s_structural.Contains(element.Name.LocalName) && element.Name.Namespace != s_forms)
            {
                return
                    $"The document mixes namespaces: a '{element.Name.LocalName}' element is declared in {Where(element.Name.Namespace)} " +
                    $"rather than '{Namespace}'. Oracle Forms structure is read by element name, so a foreign element carrying a Forms " +
                    "name would be read as the block, item, trigger, or program unit it is not. The document was refused and no module " +
                    "was read from it.";
            }

            if (element.Name.Namespace != s_forms)
            {
                continue;
            }

            if (element.Name == s_forms + "TriggerText" && element.HasElements)
            {
                return
                    "A TriggerText element contains nested elements. Retaining only its direct text would silently remove part of the " +
                    "PL/SQL body, so the document was refused and no module was read from it.";
            }

            if (element.Name == s_forms + "Trigger")
            {
                List<XAttribute> bodyAttributes =
                [
                    .. element.Attributes().Where(attribute =>
                        attribute.Name.Namespace == XNamespace.None
                        && string.Equals(attribute.Name.LocalName, "TriggerText", StringComparison.Ordinal)),
                ];
                List<XElement> bodyElements = [.. element.Elements(s_forms + "TriggerText")];

                if (element.Attributes().Any(attribute =>
                    attribute.Name.Namespace == XNamespace.None
                    && string.Equals(attribute.Name.LocalName, "TriggerText", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(attribute.Name.LocalName, "TriggerText", StringComparison.Ordinal)))
                {
                    return
                        "A TriggerText attribute uses casing outside the Oracle Forms schema. Treating it as absent or as the canonical " +
                        "attribute would make malformed source indistinguishable from an Oracle export, so the document was refused.";
                }

                if (bodyAttributes.Count > 1 || bodyElements.Count > 1 || (bodyAttributes.Count == 1 && bodyElements.Count == 1))
                {
                    return
                        "A Trigger declares more than one TriggerText representation. Attribute and child-element encodings cannot compete " +
                        "or be concatenated without changing the PL/SQL body, so the document was refused and no module was read from it.";
                }

                string? body = bodyAttributes.FirstOrDefault()?.Value
                    ?? bodyElements.FirstOrDefault()?.Value;

                if ((bodyAttributes.Count == 1 || bodyElements.Count == 1) && string.IsNullOrWhiteSpace(body))
                {
                    return
                        "A Trigger declares an empty or whitespace-only TriggerText representation. A trigger with no body omits " +
                        "TriggerText entirely, so the document was refused rather than silently discarding declared source text.";
                }

                if (body?.Length > MaxTriggerBodyCharacters)
                {
                    return
                        $"A TriggerText body contains {body.Length} characters and this build retains at most " +
                        $"{MaxTriggerBodyCharacters}. The document was refused rather than writing an IR its reader would later reject.";
                }

                if (element.Elements().Any(child =>
                    string.Equals(child.Name.LocalName, "TriggerText", StringComparison.OrdinalIgnoreCase)
                    && child.Name != s_forms + "TriggerText"))
                {
                    return
                        "A TriggerText child uses casing or a namespace outside the Oracle Forms schema. Treating it as absent would " +
                        "silently discard declared trigger source, so the document was refused and no module was read from it.";
                }
            }

            foreach (XAttribute attribute in element.Attributes())
            {
                XNamespace declared = attribute.Name.Namespace;

                if (attribute.IsNamespaceDeclaration
                    || declared == XNamespace.None
                    || declared == XNamespace.Xml
                    || declared == s_schemaInstance)
                {
                    continue;
                }

                return
                    $"The document carries a namespace-qualified attribute '{attribute.Name.LocalName}' in {Where(declared)} on the " +
                    $"'{element.Name.LocalName}' element. This fleet reads Forms attributes by local name only, so a qualified " +
                    "attribute would compete with the real one for the same meaning. The document was refused and no module was read from it.";
            }
        }

        return null;
    }

    private static string Where(XNamespace declared) =>
        declared == XNamespace.None ? "no namespace" : $"'{declared.NamespaceName}'";

    private static string Describe(XElement root)
    {
        string declared = root.Name.Namespace == XNamespace.None ? "no namespace" : $"'{root.Name.NamespaceName}'";

        return
            $"The document declares a Forms element under {declared} with root '{root.Name.LocalName}'. " +
            $"This fleet reads only an Oracle Forms XML export whose root is '{Namespace}:Module' containing FormModule elements, " +
            $"or '{Namespace}:FormModule' directly. Re-export the module with frmf2xml and supply the result unaltered.";
    }

    private static IEnumerable<string> Names(XElement root) =>
        root.DescendantsAndSelf().Select(element => element.Name.LocalName);
}
