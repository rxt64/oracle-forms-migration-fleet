using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace OracleFormsMigrationFleet.Fleet.Execution;

/// <summary>
/// How a source fact came to be known. One value exists on purpose: everything retained here was written
/// down by the export, so there is no inferred or defaulted kind to distinguish it from.
/// </summary>
public enum FormsSourceFactKind
{
    Declared,
}

/// <summary>
/// One attribute exactly as the export declared it. <paramref name="Namespace"/> is the empty string for
/// the unqualified attributes an Oracle export writes, so a qualified attribute can never be confused with
/// the unqualified one the interpreting readers look up by local name. <paramref name="Value"/> is the
/// XML-normalized value and is not decoded again.
/// </summary>
public sealed record FormsSourceAttribute(string Name, string Namespace, string Value);

/// <summary>One element of a Forms export, retained without interpretation.</summary>
/// <param name="Id">
/// Path from the module element. Each step is <c>{namespace}localName[index]</c>, where index counts from
/// one among the preceding siblings sharing that qualified name. The module element is always step
/// <c>[1]</c>, so one module's paths never depend on its position in a multi-module wrapper.
/// </param>
/// <param name="Order">The element's position in the module's document-order inventory.</param>
/// <param name="ParentId">The containing element's id; null only for the module element itself.</param>
/// <param name="ChildIndex">The element's position among its parent's direct children, from zero.</param>
/// <param name="DeclaredName">The unqualified Name attribute the export declared, or null where it declared none.</param>
/// <param name="Text">
/// Direct text exactly as the loader supplied it, or null only where the element declared no text node at
/// all. The loader drops insignificant inter-element whitespace, so whitespace reaching here is significant
/// — mixed content, or an element under <c>xml:space="preserve"</c> — and is retained rather than blanked.
/// </param>
public sealed record FormsSourceFact(
    string Id,
    int Order,
    string? ParentId,
    int ChildIndex,
    string LocalName,
    string Namespace,
    string? DeclaredName,
    IReadOnlyList<FormsSourceAttribute> Attributes,
    string? Text,
    FormsSourceFactKind Kind);

/// <summary>The retained facts of one module, with the provenance needed to tell one export's facts from another's.</summary>
/// <param name="TextDigest">
/// SHA-256 of the UTF-8 export text these facts were read from. It is a digest of the text this fleet
/// parsed, not of the bytes on disk, so it says nothing about how the file was transported.
/// </param>
/// <param name="WrapperDeclaredVersion">
/// The version attribute of the Module wrapper, verbatim, or null where the export had no wrapper or
/// declared no version on it. It is a string the file carried, not a release this fleet adjudicated.
/// </param>
public sealed record FormsSourceFactSet(
    string TextDigest,
    string? WrapperDeclaredVersion,
    IReadOnlyList<FormsSourceFact> Facts);

/// <summary>
/// Reads a Forms export element into an ordered fact inventory.
///
/// The interpreting parser recovers blocks, items, and triggers and drops everything else, and those
/// losses were invisible because the normalized representation only ever held what the parser had kept.
/// This retains every element and attribute as declared, in document order, so an omission stays
/// detectable. It interprets nothing: an element outside the Oracle Forms namespace is retained carrying
/// its namespace, which is not a statement that it has any Forms meaning.
///
/// Bounds reject the whole document rather than retaining part of it, because a truncated inventory reads
/// back as an export that genuinely declared less.
/// </summary>
public static class FormsSourceFactReader
{
    /// <summary>Maximum elements retained for one module.</summary>
    public const int MaxFacts = 100_000;

    /// <summary>Maximum attributes retained on one element.</summary>
    public const int MaxAttributes = 512;

    /// <summary>Maximum characters retained for one attribute value or one element's direct text.</summary>
    public const int MaxValueCharacters = FormsXmlDocument.MaxTriggerBodyCharacters;

    /// <summary>Maximum characters in one source-object id.</summary>
    public const int MaxIdCharacters = 4_000;

    /// <summary>Maximum element nesting retained, counting the module element as depth one.</summary>
    public const int MaxDepth = 512;

    /// <summary>Length of the hexadecimal text digest.</summary>
    public const int DigestCharacters = 64;

    private static readonly XNamespace s_forms = FormsXmlDocument.Namespace;

    /// <summary>SHA-256 of the export text, lowercase hexadecimal.</summary>
    public static string TextDigest(string? text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text ?? string.Empty))).ToLowerInvariant();

    /// <summary>
    /// The version attribute the Module wrapper declared, verbatim. Null when the export's root is the
    /// FormModule itself, or when the wrapper declared no version.
    /// </summary>
    public static string? WrapperDeclaredVersion(XElement? root) =>
        root is not null && root.Name == s_forms + "Module"
            ? root.Attributes().FirstOrDefault(attribute =>
                !attribute.IsNamespaceDeclaration
                && attribute.Name.Namespace == XNamespace.None
                && string.Equals(attribute.Name.LocalName, "version", StringComparison.OrdinalIgnoreCase))?.Value
            : null;

    /// <summary>
    /// Reads <paramref name="module"/> and everything under it. Exactly one side of the result is set: the
    /// full inventory, or the reason the document was refused.
    /// </summary>
    public static (FormsSourceFactSet? Facts, string? Rejection) Read(
        XElement module,
        string textDigest,
        string? wrapperDeclaredVersion)
    {
        ArgumentNullException.ThrowIfNull(module);

        List<FormsSourceFact> facts = [];
        string? rejection = Walk(module, facts);

        return rejection is not null
            ? (null, rejection)
            : (new FormsSourceFactSet(textDigest, wrapperDeclaredVersion, facts), null);
    }

    /// <summary>
    /// Rebuilds the element tree a retained inventory was read from.
    ///
    /// Nothing is parsed: the tree is assembled from values already in memory, so no DTD, entity, or
    /// external resolver is reachable from it and the retained text is never decoded a second time. It
    /// exists so the interpreted structure of a module can be checked against the facts beside it by
    /// putting those facts through the same interpretation the export went through, rather than by
    /// comparing two independently written projections.
    ///
    /// Exactly one side of the result is set. The rejections here are the ones a fact set that reached this
    /// build without being validated could still cause — a name no XML document could carry, a parent that
    /// is not present, a second root — and none of them is a statement about Oracle Forms.
    /// </summary>
    internal static (XElement? Module, string? Rejection) Rebuild(FormsSourceFactSet facts)
    {
        ArgumentNullException.ThrowIfNull(facts);

        Dictionary<string, XElement> built = new(StringComparer.Ordinal);
        XElement? module = null;

        foreach (FormsSourceFact fact in facts.Facts)
        {
            XElement element;

            try
            {
                element = new XElement(Qualified(fact.LocalName, fact.Namespace));

                foreach (FormsSourceAttribute attribute in fact.Attributes)
                {
                    element.Add(new XAttribute(Qualified(attribute.Name, attribute.Namespace), attribute.Value));
                }
            }
            catch (Exception exception) when (exception is XmlException or ArgumentException or InvalidOperationException)
            {
                return (null, $"'{fact.Id}' names an element or attribute no XML document could declare: {exception.Message}");
            }

            if (fact.Text is { } text)
            {
                element.Add(new XText(text));
            }

            if (fact.ParentId is null)
            {
                if (module is not null)
                {
                    return (null, $"'{fact.Id}' declares no parent while '{module.Name.LocalName}' already roots this inventory.");
                }

                module = element;
            }
            else if (!built.TryGetValue(fact.ParentId, out XElement? parent))
            {
                return (null, $"'{fact.Id}' sits under '{fact.ParentId}', which this inventory does not retain.");
            }
            else
            {
                parent.Add(element);
            }

            if (!built.TryAdd(fact.Id, element))
            {
                return (null, $"'{fact.Id}' is retained more than once.");
            }
        }

        return module is null
            ? (null, "the inventory retains no element without a parent, so it roots no module.")
            : (module, null);
    }

    private static XName Qualified(string localName, string @namespace) =>
        @namespace.Length == 0 ? XName.Get(localName) : XName.Get(localName, @namespace);

    /// <summary>One element whose children are being read, and the counters its children's ids need.</summary>
    private sealed class Frame(string id, IEnumerator<XElement> children)
    {
        public string Id { get; } = id;

        public IEnumerator<XElement> Children { get; } = children;

        /// <summary>How many preceding siblings carried each qualified name, so no step rescans siblings.</summary>
        public Dictionary<XName, int> Seen { get; } = [];

        public int NextChildIndex { get; set; }
    }

    /// <summary>
    /// Depth-first preorder over the module. The traversal is iterative because an export is untrusted
    /// input, and recursion would let a deeply nested document decide this build's stack depth.
    /// </summary>
    private static string? Walk(XElement module, List<FormsSourceFact> facts)
    {
        // The module element is always [1]: it roots its own id space whatever its position among the
        // FormModule siblings of a multi-module wrapper.
        string moduleId = Segment(module.Name, 1);

        if (Retain(module, parentId: null, childIndex: 0, moduleId, facts) is { } moduleRejection)
        {
            return moduleRejection;
        }

        Stack<Frame> open = new();
        open.Push(new Frame(moduleId, module.Elements().GetEnumerator()));

        try
        {
            while (open.Count > 0)
            {
                Frame frame = open.Peek();

                if (!frame.Children.MoveNext())
                {
                    open.Pop().Children.Dispose();
                    continue;
                }

                XElement child = frame.Children.Current;

                frame.Seen.TryGetValue(child.Name, out int preceding);
                frame.Seen[child.Name] = preceding + 1;

                string id = $"{frame.Id}/{Segment(child.Name, preceding + 1)}";

                if (open.Count >= MaxDepth)
                {
                    return $"This export nests elements more than {Count(MaxDepth)} deep at '{id}' and this build retains at most that " +
                        "many levels. The document was refused rather than retaining the estate in part.";
                }

                if (Retain(child, frame.Id, frame.NextChildIndex++, id, facts) is { } rejection)
                {
                    return rejection;
                }

                open.Push(new Frame(id, child.Elements().GetEnumerator()));
            }
        }
        finally
        {
            foreach (Frame frame in open)
            {
                frame.Children.Dispose();
            }
        }

        return null;
    }

    private static string? Retain(
        XElement element,
        string? parentId,
        int childIndex,
        string id,
        List<FormsSourceFact> facts)
    {
        if (facts.Count >= MaxFacts)
        {
            return Overflow("elements", facts.Count + 1, MaxFacts);
        }

        if (id.Length > MaxIdCharacters)
        {
            return $"A source-object path in this export is {Count(id.Length)} characters and this build retains at most " +
                $"{Count(MaxIdCharacters)}. The document was refused rather than retaining an element whose identity could not be written down.";
        }

        List<FormsSourceAttribute> attributes = [];
        foreach (XAttribute attribute in element.Attributes())
        {
            if (attribute.IsNamespaceDeclaration)
            {
                continue;
            }

            if (attributes.Count >= MaxAttributes)
            {
                return Overflow($"attributes on '{id}'", attributes.Count + 1, MaxAttributes);
            }

            if (attribute.Value.Length > MaxValueCharacters)
            {
                return $"The '{attribute.Name.LocalName}' attribute of '{id}' declares {Count(attribute.Value.Length)} characters and this " +
                    $"build retains at most {Count(MaxValueCharacters)}. The document was refused rather than retaining a value in part.";
            }

            attributes.Add(new FormsSourceAttribute(
                attribute.Name.LocalName,
                attribute.Name.Namespace == XNamespace.None ? string.Empty : attribute.Name.NamespaceName,
                attribute.Value));
        }

        string text = string.Concat(element.Nodes().OfType<XText>().Select(node => node.Value));

        if (text.Length > MaxValueCharacters)
        {
            return $"The direct text of '{id}' is {Count(text.Length)} characters and this build retains at most " +
                $"{Count(MaxValueCharacters)}. The document was refused rather than retaining it in part.";
        }

        facts.Add(new FormsSourceFact(
            id,
            facts.Count,
            parentId,
            childIndex,
            element.Name.LocalName,
            element.Name.Namespace == XNamespace.None ? string.Empty : element.Name.NamespaceName,
            DeclaredName(element),
            attributes,
            // Only an element that declared no text node at all records none. Whitespace the loader kept is
            // significant, so blanking it would drop source the export chose to preserve.
            text.Length == 0 ? null : text,
            FormsSourceFactKind.Declared));

        return null;
    }

    /// <summary>
    /// One step of a source-object path: the qualified name with the element's index among siblings that
    /// share it. Unqualified names carry an empty brace pair so an element in no namespace can never write
    /// the same step as one in the Forms namespace.
    /// </summary>
    private static string Segment(XName name, int index) =>
        $"{{{(name.Namespace == XNamespace.None ? string.Empty : name.NamespaceName)}}}{name.LocalName}[{Count(index)}]";

    private static string? DeclaredName(XElement element) =>
        element.Attributes().FirstOrDefault(attribute =>
            !attribute.IsNamespaceDeclaration
            && attribute.Name.Namespace == XNamespace.None
            && string.Equals(attribute.Name.LocalName, "Name", StringComparison.OrdinalIgnoreCase))?.Value;

    private static string Overflow(string kind, int declared, int limit) =>
        $"This export declares more than {Count(limit)} {kind} in one module and this build retains at most that many " +
        $"({Count(declared)} reached). The document was refused rather than retaining the estate in part, because a truncated " +
        "inventory reads back as an export that declared less.";

    private static string Count(int value) => value.ToString(CultureInfo.InvariantCulture);
}
