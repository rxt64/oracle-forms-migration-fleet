// Copyright (c) Microsoft. All rights reserved.

namespace OracleFormsMigrationFleet.Fleet.Execution;

/// <summary>
/// Whether a run has any Oracle Forms source to normalize at all.
///
/// This is the one thing that decides whether the source-normalization dependency is applicable. A
/// database-only migration has no Forms module to normalize, and requiring the phase there would refuse a
/// schema conversion that reads no Forms file. Anything else — a declared evidence item, or a module of
/// any Forms type sitting in the tree — makes normalization the phase that adjudicates that source, and a
/// missing or failed normalization must then stop the conversion rather than letting it re-read the tree.
///
/// Modules are recognised by extension. An XML file is recognised by its root shape only, through the
/// same loader the normalization phase uses, so the two cannot disagree about whether a file is an export.
/// Nothing else in a file's contents reaches this decision.
/// </summary>
public static class FormsSourcePresence
{
    private const int MaxFiles = 20_000;
    private const long MaxTextBytes = 8L * 1024 * 1024;

    /// <summary>Every Forms module container this fleet recognises, readable or not.</summary>
    public static IReadOnlyList<string> ModuleExtensions { get; } =
        [".fmb", ".mmb", ".pll", ".olb", ".fmt", ".mmt", ".fmx", ".mmx", ".plx"];

    /// <summary>
    /// True when the request declares Forms source evidence, or when the source root holds a file of a
    /// Forms module type, or an XML file whose root shape is an Oracle Forms export.
    ///
    /// An <c>.xml</c> file is matched on its root shape rather than its extension, because an estate is
    /// full of XML that is not a Forms export and treating all of it as Forms source would block a
    /// database-only migration on a build descriptor.
    /// </summary>
    public static bool Applies(MigrationRunRequest request, WorkspaceWriter workspace, string sourceRoot)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(workspace);

        if (DeclaredByEvidence(request))
        {
            return true;
        }

        if (!workspace.DirectoryExists(sourceRoot))
        {
            return false;
        }

        foreach (WorkspaceFile file in workspace.EnumerateFiles(sourceRoot, MaxFiles))
        {
            string extension = Path.GetExtension(file.RelativePath);

            if (ModuleExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
            {
                return true;
            }

            if (!extension.Equals(".xml", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                FormsXmlLoad load = FormsXmlDocument.Load(workspace.ReadText(file.RelativePath, MaxTextBytes));

                // A file that claims to be a Forms export in a shape this fleet refuses still counts: the
                // run has Forms source, and normalization is the phase that has to say so.
                if (load.IsFormsExport || load.ShapeRejection is not null)
                {
                    return true;
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                // Unreadable source is normalization's problem to report, not a reason to declare there is none.
                return true;
            }
        }

        return false;
    }

    /// <summary>True when the request attests that Forms source exists, whatever the workspace holds.</summary>
    public static bool DeclaredByEvidence(MigrationRunRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        return request.Evidence?.Any(item =>
            item is { Kind: EvidenceKind.FormsModuleSource or EvidenceKind.FormsXmlExport }) == true;
    }
}
