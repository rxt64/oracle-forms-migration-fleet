// Copyright (c) Microsoft. All rights reserved.

using OracleFormsMigrationFleet.Fleet;

namespace OracleFormsMigrationFleet.Hosting;

/// <summary>
/// Classifies an acquired source tree by file name. File contents are never opened, so an untrusted
/// archive cannot influence the result beyond the names it declares.
/// </summary>
public sealed record SourceInventory(
    int FileCount,
    long ByteCount,
    string SourceRoot,
    IReadOnlyList<SourceArtifact> Artifacts,
    bool Truncated)
{
    private static readonly Dictionary<string, string> ExtensionKinds = new(StringComparer.OrdinalIgnoreCase)
    {
        [".fmb"] = "FormsModuleSource",
        [".fmt"] = "FormsModuleSource",
        [".mmb"] = "MenuModuleSource",
        [".mmt"] = "MenuModuleSource",
        [".pll"] = "SharedLibrarySource",
        [".pld"] = "SharedLibrarySource",
        [".olb"] = "ObjectLibrarySource",
        [".olt"] = "ObjectLibrarySource",
        [".rdf"] = "OracleReportsInventory",
        [".rep"] = "OracleReportsInventory",
        [".pks"] = "PlSqlProgramUnit",
        [".pkb"] = "PlSqlProgramUnit",
        [".plb"] = "PlSqlProgramUnit",
        [".prc"] = "PlSqlProgramUnit",
        [".fnc"] = "PlSqlProgramUnit",
        [".trg"] = "PlSqlProgramUnit",
        [".dmp"] = "DatabaseSchemaExport",
    };

    private static readonly string[] FormsExtensions =
        [".fmb", ".fmt", ".mmb", ".mmt", ".pll", ".pld", ".olb", ".olt"];

    private static readonly string[] SchemaMarkers = ["schema", "ddl", "create_table", "createtable", "tablespace"];

    // Build and framework descriptors routinely sit beside Forms code and must never be read as an export.
    private static readonly string[] NonFormsXmlNames =
        ["build.xml", "pom.xml", "web.xml", "ivy.xml", "settings.xml", "logback.xml", "checkstyle.xml", "persistence.xml"];

    private static readonly string[] FormsDirectorySegments = ["forms", "form", "fmb", "formsxml"];

    public static SourceInventory Build(string root, int maxFiles, long maxBytes)
    {
        string prefix = Path.GetFullPath(root) + Path.DirectorySeparatorChar;
        var counts = new Dictionary<string, (int Count, string Example)>(StringComparer.Ordinal);
        var formsDirectories = new List<string[]>();
        var artifactDirectories = new List<string[]>();

        int fileCount = 0;
        long byteCount = 0;
        bool truncated = false;

        foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetFullPath(file).StartsWith(prefix, StringComparison.Ordinal)
                ? Path.GetFullPath(file)[prefix.Length..].Replace('\\', '/')
                : Path.GetFileName(file);

            if (WorkspacePath.IsWithin(WorkbenchExecution.OutputRoot, relative))
            {
                continue;
            }

            if (fileCount >= maxFiles || byteCount >= maxBytes)
            {
                truncated = true;
                break;
            }

            fileCount++;
            try
            {
                byteCount += new FileInfo(file).Length;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            if (Classify(relative) is not string kind)
            {
                continue;
            }

            counts[kind] = counts.TryGetValue(kind, out (int Count, string Example) existing)
                ? (existing.Count + 1, existing.Example)
                : (1, relative);

            string[] segments = relative.Split('/')[..^1];
            artifactDirectories.Add(segments);
            if (FormsExtensions.Contains(Path.GetExtension(relative), StringComparer.OrdinalIgnoreCase))
            {
                formsDirectories.Add(segments);
            }
        }

        // Enumerating every form file is itself the module inventory the planner asks for.
        if (counts.TryGetValue("FormsModuleSource", out (int Count, string Example) forms))
        {
            counts["FormsModuleInventory"] = (forms.Count, "derived from the form files in this source");
        }

        List<SourceArtifact> artifacts = [.. counts
            .Select(entry => new SourceArtifact(entry.Key, entry.Value.Count, entry.Value.Example))
            .OrderByDescending(artifact => artifact.Count)
            .ThenBy(artifact => artifact.Kind, StringComparer.Ordinal)];

        return new SourceInventory(
            fileCount,
            byteCount,
            CommonDirectory(formsDirectories.Count > 0 ? formsDirectories : artifactDirectories),
            artifacts,
            truncated);
    }

    private static string? Classify(string relativePath)
    {
        string name = Path.GetFileName(relativePath);
        string extension = Path.GetExtension(name);

        if (extension.Equals(".sql", StringComparison.OrdinalIgnoreCase))
        {
            return SchemaMarkers.Any(marker => name.Contains(marker, StringComparison.OrdinalIgnoreCase))
                ? "DatabaseSchemaExport"
                : "PlSqlProgramUnit";
        }

        if (extension.Equals(".xml", StringComparison.OrdinalIgnoreCase))
        {
            if (NonFormsXmlNames.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                return null;
            }

            // Matching "form" anywhere in the path made every .xml under a folder such as
            // "OracleFormsTester" look like an export, so only the file name or an exact
            // forms directory segment counts.
            return name.Contains("form", StringComparison.OrdinalIgnoreCase)
                || relativePath.Split('/')[..^1].Any(segment =>
                    FormsDirectorySegments.Contains(segment, StringComparer.OrdinalIgnoreCase))
                ? "FormsXmlExport"
                : null;
        }

        return ExtensionKinds.TryGetValue(extension, out string? kind) ? kind : null;
    }

    private static string CommonDirectory(List<string[]> directories)
    {
        if (directories.Count == 0)
        {
            return ".";
        }

        string[] prefix = directories[0];
        foreach (string[] candidate in directories)
        {
            int index = 0;
            while (index < prefix.Length && index < candidate.Length && prefix[index] == candidate[index])
            {
                index++;
            }

            prefix = prefix[..index];
        }

        return prefix.Length > 0 ? string.Join('/', prefix) : ".";
    }
}
