// Copyright (c) Microsoft. All rights reserved.

using System.Xml.Linq;

namespace OracleFormsMigrationFleet.Fleet.Execution;

/// <summary>
/// What a supplied XML file declares about itself. Every field is a claim made inside the file, never a
/// verified fact about the estate it came from.
/// </summary>
public sealed record FormsXmlDeclaration(
    bool IsXml,
    bool HasFormModule,
    string? RootElement,
    string? DeclaredVersion,
    string? DeclaredFormsVersion,
    IReadOnlyList<string> DeclaredVersions,
    string? ParseError)
{
    /// <summary>
    /// Set when the file names a Forms element but not in a root shape or namespace this fleet reads.
    /// Distinct from <see cref="ParseError"/>, because the file is well-formed XML, and distinct from an
    /// unrelated XML file, which produces neither.
    /// </summary>
    public string? ShapeRejection { get; init; }

    /// <summary>The first version attribute the file carried, whichever of the two it used.</summary>
    public string? AnyDeclaredVersion => DeclaredFormsVersion ?? DeclaredVersion;

    public static FormsXmlDeclaration NotXml { get; } = new(false, false, null, null, null, [], null);
}

/// <summary>
/// Reads the version attributes an Oracle Forms XML export declares, and nothing else.
///
/// Every declaration on the root and on every FormModule is collected rather than the first one found,
/// because a file that names two different releases is a contradiction the caller has to resolve. Taking
/// one and continuing would make the release this run records depend on document order.
///
/// Which documents count as an export is decided by <see cref="FormsXmlDocument"/>, so this reader and
/// <see cref="FormsModuleParser"/> cannot disagree about the same file. The reader deliberately reports
/// the declared attribute rather than asserting provenance: nothing here proves the file came from
/// frmf2xml, from a licensed Forms installation, or from the release it names.
/// </summary>
public static class FormsXmlVersionReader
{
    public static FormsXmlDeclaration Read(string? xml)
    {
        FormsXmlLoad load = FormsXmlDocument.Load(xml);

        if (load.ParseError is { } error)
        {
            return new FormsXmlDeclaration(false, false, null, null, null, [], error);
        }

        if (load.Root is not { } root)
        {
            return FormsXmlDeclaration.NotXml;
        }

        if (!load.IsFormsExport)
        {
            return new FormsXmlDeclaration(true, false, root.Name.LocalName, null, null, [], null)
            {
                ShapeRejection = load.ShapeRejection,
            };
        }

        List<string> declared = [];
        foreach (XElement element in (XElement[])[root, .. load.FormModules])
        {
            foreach (string name in (string[])["version", "FormsVersion"])
            {
                if (Attribute(element, name) is { } value && !declared.Contains(value, StringComparer.OrdinalIgnoreCase))
                {
                    declared.Add(value);
                }
            }
        }

        XElement module = load.FormModules[0];

        return new FormsXmlDeclaration(
            IsXml: true,
            HasFormModule: true,
            RootElement: root.Name.LocalName,
            DeclaredVersion: Attribute(module, "version") ?? Attribute(root, "version"),
            DeclaredFormsVersion: Attribute(module, "FormsVersion") ?? Attribute(root, "FormsVersion"),
            DeclaredVersions: declared,
            ParseError: null);
    }

    /// <summary>
    /// A declared version attribute, matched by local name in no namespace. A qualified attribute is never
    /// read, so a document cannot offer a second candidate for the same name; <see cref="FormsXmlDocument"/>
    /// has already refused any document that carries one.
    /// </summary>
    private static string? Attribute(XElement? element, string name) => element?
        .Attributes()
        .FirstOrDefault(attribute =>
            attribute.Name.Namespace == XNamespace.None
            && string.Equals(attribute.Name.LocalName, name, StringComparison.OrdinalIgnoreCase))?
        .Value is { Length: > 0 } value ? value : null;
}
