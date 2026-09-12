// Copyright (c) Microsoft. All rights reserved.

using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OracleFormsMigrationFleet.Fleet.Execution.Adapters;

/// <summary>
/// Produces the SourceAnalysis artifacts the run blueprint declares: a dependency graph plus the
/// inventory, data dictionary, dependency map, and technical debt report.
///
/// Oracle Forms binaries (.fmb, .fmx, .mmb, .mmx, .olb, .pll, .plx, .rdf, .rep) are treated as opaque.
/// Their bytes are never decoded as text and no trigger, block, or program unit is inferred from them;
/// they are reported by name and size, with structure extraction deferred to Forms Builder or JDAPI.
/// Every technical debt finding cites the parsed object it came from. Nothing is inferred or invented.
/// </summary>
public sealed class SourceAnalysisAdapter : IPhaseAdapter
{
    private const int MaxFiles = 20_000;
    private const long MaxTextBytes = 8L * 1024 * 1024;

    private static readonly JsonSerializerOptions s_json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly HashSet<string> s_opaqueBinary = new(StringComparer.OrdinalIgnoreCase)
    {
        ".fmb", ".fmx", ".mmb", ".mmx", ".olb", ".pll", ".plx", ".rdf", ".rep",
    };

    private static readonly Dictionary<string, string> s_categories = new(StringComparer.OrdinalIgnoreCase)
    {
        [".fmb"] = "Forms module (binary)",
        [".fmx"] = "Forms runtime module (binary)",
        [".mmb"] = "Menu module (binary)",
        [".mmx"] = "Menu runtime module (binary)",
        [".olb"] = "Object library (binary)",
        [".pll"] = "PL/SQL library (binary)",
        [".plx"] = "PL/SQL runtime library (binary)",
        [".rdf"] = "Oracle Reports definition (binary)",
        [".rep"] = "Oracle Reports runtime (binary)",
        [".fmt"] = "Forms module (text export)",
        [".mmt"] = "Menu module (text export)",
        [".olt"] = "Object library (text export)",
        [".pld"] = "PL/SQL library (text export)",
        [".xml"] = "XML export",
        [".sql"] = "SQL script",
        [".pks"] = "PL/SQL package specification",
        [".pkb"] = "PL/SQL package body",
        [".plb"] = "Wrapped PL/SQL",
        [".prc"] = "PL/SQL procedure",
        [".fnc"] = "PL/SQL function",
        [".trg"] = "Database trigger",
    };

    private static readonly string[] s_credentialNameMarkers = ["PASSWORD", "PASSWD", "PWD", "SECRET", "APIKEY", "API_KEY", "TOKEN"];

    private static readonly string[] s_hashNameMarkers = ["HASH", "DIGEST", "SALT", "ENCRYPTED", "CIPHER"];

    private static readonly string[] s_characterTypes = ["VARCHAR2", "VARCHAR", "NVARCHAR2", "CHAR", "NCHAR", "CLOB", "NCLOB", "LONG"];

    public MigrationPhase Phase => MigrationPhase.SourceAnalysis;

    public Task<PhaseExecutionResult> ExecuteAsync(PhaseExecutionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        string sourceRoot = WorkspacePath.Normalize(context.SourceRoot);
        string outputRoot = WorkspacePath.Normalize(context.OutputRoot);

        if (!context.Workspace.DirectoryExists(sourceRoot))
        {
            return Task.FromResult(PhaseExecutionResult.Failure(
                $"The source root '{sourceRoot}' does not exist in the workspace. Nothing was analysed and no artifact was written."));
        }

        IReadOnlyList<WorkspaceFile> files = context.Workspace.EnumerateFiles(sourceRoot, MaxFiles);
        if (files.Count == 0)
        {
            return Task.FromResult(PhaseExecutionResult.Failure(
                $"The source root '{sourceRoot}' contains no files. Nothing was analysed and no artifact was written."));
        }

        context.Info($"Indexed {files.Count.ToString(CultureInfo.InvariantCulture)} files under {sourceRoot}.");

        Analysis analysis = Analyse(context, files, cancellationToken);

        context.Info(
            $"Parsed {analysis.Schema.Tables.Count.ToString(CultureInfo.InvariantCulture)} tables, " +
            $"{analysis.Schema.Sequences.Count.ToString(CultureInfo.InvariantCulture)} sequences, and " +
            $"{analysis.Schema.Indexes.Count.ToString(CultureInfo.InvariantCulture)} indexes from SQL scripts.");

        if (analysis.Binaries.Count > 0)
        {
            context.Warn(
                $"{analysis.Binaries.Count.ToString(CultureInfo.InvariantCulture)} Oracle Forms binary modules were listed by name and size only. " +
                "Their structure cannot be read without Forms Builder or JDAPI, so no trigger or block logic was derived from them.");
        }

        List<ArtifactReference> written = [];

        Write(context, written, $"{outputRoot}/analysis/dependency-graph.json", ArtifactKind.ValidationReport,
            "Module, library, report, and schema dependency graph.", RenderGraph(analysis, sourceRoot));

        Write(context, written, $"{outputRoot}/analysis/APPLICATION_INVENTORY.md", ArtifactKind.Documentation,
            "Forms, menus, libraries, reports, and program units with size and complexity signals.", RenderInventory(context, analysis, sourceRoot));

        Write(context, written, $"{outputRoot}/analysis/DATA_DICTIONARY.md", ArtifactKind.Documentation,
            "Tables, views, columns, types, and constraints referenced by the estate.", RenderDataDictionary(context, analysis));

        Write(context, written, $"{outputRoot}/analysis/DEPENDENCY_MAP.md", ArtifactKind.Documentation,
            "Human-readable module, library, and schema dependency narrative behind the graph.", RenderDependencyMap(context, analysis));

        Write(context, written, $"{outputRoot}/analysis/TECHNICAL_DEBT_REPORT.md", ArtifactKind.Documentation,
            "Obsolete constructs, duplication, dead code, and remediation risk ranking.", RenderTechnicalDebt(context, analysis));

        return Task.FromResult(PhaseExecutionResult.Success(
            written,
            [.. analysis.Debt.Select(finding => $"{finding.Title}: {finding.Evidence}")]));
    }

    // ---------- analysis ----------

    private sealed record DebtFinding(string Title, string Evidence);

    private sealed record ParsedScript(string Path, OracleSchema Schema);

    private sealed record Analysis(
        IReadOnlyList<WorkspaceFile> Files,
        IReadOnlyList<WorkspaceFile> Binaries,
        IReadOnlyList<ParsedScript> Scripts,
        OracleSchema Schema,
        IReadOnlyDictionary<string, string> TableSources,
        IReadOnlyList<DebtFinding> Debt);

    private static Analysis Analyse(PhaseExecutionContext context, IReadOnlyList<WorkspaceFile> files, CancellationToken cancellationToken)
    {
        List<WorkspaceFile> binaries = [];
        List<ParsedScript> scripts = [];

        foreach (WorkspaceFile file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string extension = Path.GetExtension(file.RelativePath);

            if (s_opaqueBinary.Contains(extension))
            {
                binaries.Add(file);
                continue;
            }

            if (!extension.Equals(".sql", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                scripts.Add(new ParsedScript(file.RelativePath, OracleSchemaParser.Parse(context.Workspace.ReadText(file.RelativePath, MaxTextBytes))));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                context.Warn($"{file.RelativePath} could not be read and was skipped.");
            }
        }

        OracleSchema schema = OracleSchema.Merge(scripts.Select(script => script.Schema));

        Dictionary<string, string> tableSources = new(StringComparer.OrdinalIgnoreCase);
        foreach (ParsedScript script in scripts)
        {
            foreach (OracleTable table in script.Schema.Tables)
            {
                tableSources.TryAdd(table.Name, script.Path);
            }
        }

        return new Analysis(files, binaries, scripts, schema, tableSources, FindDebt(schema, binaries, scripts));
    }

    /// <summary>Only facts visible in the parsed model become findings. Nothing here is inferred.</summary>
    private static IReadOnlyList<DebtFinding> FindDebt(
        OracleSchema schema,
        IReadOnlyList<WorkspaceFile> binaries,
        IReadOnlyList<ParsedScript> scripts)
    {
        List<DebtFinding> debt = [];
        HashSet<string> tables = new(schema.Tables.Select(table => table.Name), StringComparer.OrdinalIgnoreCase);

        foreach (OracleTable table in schema.Tables)
        {
            foreach (OracleColumn column in table.Columns)
            {
                string upper = column.Name.ToUpperInvariant();
                bool looksLikeCredential = s_credentialNameMarkers.Any(marker => upper.Contains(marker, StringComparison.Ordinal));
                bool looksProtected = s_hashNameMarkers.Any(marker => upper.Contains(marker, StringComparison.Ordinal));

                if (looksLikeCredential && !looksProtected && s_characterTypes.Contains(column.BaseType))
                {
                    debt.Add(new DebtFinding(
                        "Credential stored in a character column",
                        $"{table.Name}.{column.Name} is declared {column.RawType}. The column name states it holds a credential and the type is a character type, so the value is stored as readable text."));
                }
            }

            if (!table.Constraints.Any(constraint => constraint.Kind == OracleConstraintKind.PrimaryKey))
            {
                debt.Add(new DebtFinding(
                    "Table without a primary key",
                    $"{table.Name} declares {table.Columns.Count.ToString(CultureInfo.InvariantCulture)} columns and no PRIMARY KEY constraint in the supplied DDL."));
            }

            foreach (OracleConstraint constraint in table.Constraints.Where(c => c.Kind == OracleConstraintKind.ForeignKey))
            {
                if (constraint.ReferencedTable is string referenced && !tables.Contains(referenced))
                {
                    debt.Add(new DebtFinding(
                        "Foreign key to an undefined table",
                        $"{table.Name}.{constraint.Name ?? "FOREIGN KEY"} references '{referenced}', which no CREATE TABLE statement in the supplied source defines."));
                }
            }
        }

        if (binaries.Count > 0)
        {
            debt.Add(new DebtFinding(
                "Modules that cannot be analysed statically",
                $"{binaries.Count.ToString(CultureInfo.InvariantCulture)} Oracle Forms binary modules were found ({string.Join(", ", binaries.Take(5).Select(file => Path.GetFileName(file.RelativePath)))}" +
                $"{(binaries.Count > 5 ? ", ..." : string.Empty)}). Their contents are a proprietary binary format; structure requires Forms Builder or the Forms JDAPI, so no trigger, block, or program unit was extracted from them here."));
        }

        int unparsed = scripts.Sum(script => script.Schema.Unparsed.Count);
        if (unparsed > 0)
        {
            debt.Add(new DebtFinding(
                "Statements the schema parser did not recognise",
                $"{unparsed.ToString(CultureInfo.InvariantCulture)} statements across {scripts.Count.ToString(CultureInfo.InvariantCulture)} SQL scripts were not recognised as CREATE TABLE, CREATE SEQUENCE, CREATE INDEX, or ALTER TABLE ADD CONSTRAINT. They include every PL/SQL program unit, which this tool does not interpret."));
        }

        return debt;
    }

    // ---------- rendering ----------

    private sealed record GraphNode(string Id, string Kind, string Name, string Source, long? Bytes, bool Analyzable);

    private sealed record GraphEdge(string From, string To, string Kind, string? Constraint);

    private sealed record DependencyGraph(
        string Generator,
        string SourceRoot,
        IReadOnlyList<GraphNode> Nodes,
        IReadOnlyList<GraphEdge> Edges,
        IReadOnlyList<string> Notes);

    private static string RenderGraph(Analysis analysis, string sourceRoot)
    {
        List<GraphNode> nodes = [];
        List<GraphEdge> edges = [];

        foreach (OracleTable table in analysis.Schema.Tables)
        {
            nodes.Add(new GraphNode($"table:{table.Name}", "Table", table.Name, Source(analysis, table.Name), null, true));
        }

        foreach (OracleSequence sequence in analysis.Schema.Sequences)
        {
            nodes.Add(new GraphNode($"sequence:{sequence.Name}", "Sequence", sequence.Name, string.Empty, null, true));
        }

        foreach (OracleIndex index in analysis.Schema.Indexes)
        {
            nodes.Add(new GraphNode($"index:{index.Name}", "Index", index.Name, string.Empty, null, true));
            edges.Add(new GraphEdge($"index:{index.Name}", $"table:{index.Table}", "IndexOn", null));
        }

        foreach (WorkspaceFile binary in analysis.Binaries)
        {
            string name = Path.GetFileName(binary.RelativePath);
            nodes.Add(new GraphNode($"module:{name}", Category(binary.RelativePath), name, binary.RelativePath, binary.Length, false));
        }

        foreach (OracleTable table in analysis.Schema.Tables)
        {
            foreach (OracleConstraint constraint in table.Constraints.Where(c => c.Kind == OracleConstraintKind.ForeignKey))
            {
                if (constraint.ReferencedTable is string referenced)
                {
                    edges.Add(new GraphEdge($"table:{table.Name}", $"table:{referenced}", "ForeignKey", constraint.Name));
                }
            }
        }

        DependencyGraph graph = new(
            "oracle-forms-migration-fleet/source-analysis",
            sourceRoot,
            [.. nodes.OrderBy(node => node.Id, StringComparer.Ordinal)],
            [.. edges.OrderBy(edge => edge.From, StringComparer.Ordinal).ThenBy(edge => edge.To, StringComparer.Ordinal).ThenBy(edge => edge.Kind, StringComparer.Ordinal)],
            [
                "Nodes with analyzable=false are Oracle Forms binaries. Their contents were not decoded; only the file name and byte count are reported.",
                "Edges are derived only from parsed DDL. No dependency was inferred from a binary module.",
            ]);

        return JsonSerializer.Serialize(graph, s_json);
    }

    private static string RenderInventory(PhaseExecutionContext context, Analysis analysis, string sourceRoot)
    {
        StringBuilder markdown = new();
        void Line(string text = "") => markdown.Append(text).Append('\n');

        Line("# Application inventory");
        Line();
        Line($"Application: {context.Request.ApplicationName}");
        Line($"Engagement: {context.Request.EngagementId}");
        Line($"Source root: `{sourceRoot}`");
        Line();
        Line($"Files indexed: {analysis.Files.Count.ToString(CultureInfo.InvariantCulture)}");
        Line();
        Line("## Artifacts by category");
        Line();
        Line("| Category | Files | Bytes |");
        Line("| --- | --- | --- |");

        IEnumerable<IGrouping<string, WorkspaceFile>> groups = analysis.Files
            .GroupBy(file => Category(file.RelativePath))
            .OrderBy(group => group.Key, StringComparer.Ordinal);

        foreach (IGrouping<string, WorkspaceFile> group in groups)
        {
            Line($"| {group.Key} | {group.Count().ToString(CultureInfo.InvariantCulture)} | {group.Sum(file => file.Length).ToString(CultureInfo.InvariantCulture)} |");
        }

        Line();
        Line("## Modules that require Oracle tooling to open");
        Line();

        if (analysis.Binaries.Count == 0)
        {
            Line("No Oracle Forms binary modules were found in the source tree.");
        }
        else
        {
            Line("These files are a proprietary binary format. This tool reports their name and size only; it did not");
            Line("decode their bytes and derived no trigger, block, canvas, or program unit from them. Structure");
            Line("extraction requires Oracle Forms Builder or the Forms JDAPI, neither of which is bundled here.");
            Line();
            Line("| Module | Category | Bytes |");
            Line("| --- | --- | --- |");
            foreach (WorkspaceFile binary in analysis.Binaries)
            {
                Line($"| `{binary.RelativePath}` | {Category(binary.RelativePath)} | {binary.Length.ToString(CultureInfo.InvariantCulture)} |");
            }
        }

        Line();
        Line("## SQL scripts parsed");
        Line();

        if (analysis.Scripts.Count == 0)
        {
            Line("No `.sql` scripts were found in the source tree.");
        }
        else
        {
            Line("| Script | Tables | Sequences | Indexes | Unrecognised statements |");
            Line("| --- | --- | --- | --- | --- |");
            foreach (ParsedScript script in analysis.Scripts)
            {
                Line($"| `{script.Path}` | {script.Schema.Tables.Count.ToString(CultureInfo.InvariantCulture)} | {script.Schema.Sequences.Count.ToString(CultureInfo.InvariantCulture)} | {script.Schema.Indexes.Count.ToString(CultureInfo.InvariantCulture)} | {script.Schema.Unparsed.Count.ToString(CultureInfo.InvariantCulture)} |");
            }
        }

        Line();
        return markdown.ToString();
    }

    private static string RenderDataDictionary(PhaseExecutionContext context, Analysis analysis)
    {
        StringBuilder markdown = new();
        void Line(string text = "") => markdown.Append(text).Append('\n');

        Line("# Data dictionary");
        Line();
        Line($"Application: {context.Request.ApplicationName}");
        Line();
        Line("Every entry below was read from a CREATE statement in the supplied SQL. Objects that exist only");
        Line("inside a binary Forms module or a database this tool never contacted are not represented.");
        Line();

        if (analysis.Schema.Tables.Count == 0)
        {
            Line("No tables were parsed from the supplied source.");
        }

        foreach (OracleTable table in analysis.Schema.Tables)
        {
            Line($"## {table.Name}");
            Line();
            Line($"Source: `{Source(analysis, table.Name)}`");
            Line();
            Line("| Column | Oracle type | Nullable | Default |");
            Line("| --- | --- | --- | --- |");

            foreach (OracleColumn column in table.Columns)
            {
                Line($"| {column.Name} | {column.RawType} | {(column.NotNull ? "NOT NULL" : "NULL")} | {column.Default ?? string.Empty} |");
            }

            Line();

            if (table.Constraints.Count > 0)
            {
                Line("Constraints:");
                Line();
                foreach (OracleConstraint constraint in table.Constraints)
                {
                    Line($"- {Describe(constraint)}");
                }

                Line();
            }
        }

        if (analysis.Schema.Sequences.Count > 0)
        {
            Line("## Sequences");
            Line();
            Line("| Sequence | Start with | Increment by |");
            Line("| --- | --- | --- |");
            foreach (OracleSequence sequence in analysis.Schema.Sequences)
            {
                Line($"| {sequence.Name} | {Number(sequence.StartWith)} | {Number(sequence.IncrementBy)} |");
            }

            Line();
        }

        if (analysis.Schema.Indexes.Count > 0)
        {
            Line("## Indexes");
            Line();
            Line("| Index | Table | Columns | Unique |");
            Line("| --- | --- | --- | --- |");
            foreach (OracleIndex index in analysis.Schema.Indexes)
            {
                Line($"| {index.Name} | {index.Table} | {string.Join(", ", index.Columns)} | {(index.IsUnique ? "yes" : "no")} |");
            }

            Line();
        }

        return markdown.ToString();
    }

    private static string RenderDependencyMap(PhaseExecutionContext context, Analysis analysis)
    {
        StringBuilder markdown = new();
        void Line(string text = "") => markdown.Append(text).Append('\n');

        HashSet<string> tables = new(analysis.Schema.Tables.Select(table => table.Name), StringComparer.OrdinalIgnoreCase);

        Line("# Dependency map");
        Line();
        Line($"Application: {context.Request.ApplicationName}");
        Line();
        Line("This narrative explains `dependency-graph.json`. Both are derived only from parsed DDL.");
        Line();
        Line("## Table to table");
        Line();

        List<string> references = [];
        foreach (OracleTable table in analysis.Schema.Tables)
        {
            foreach (OracleConstraint constraint in table.Constraints.Where(c => c.Kind == OracleConstraintKind.ForeignKey))
            {
                if (constraint.ReferencedTable is not string referenced)
                {
                    continue;
                }

                string status = tables.Contains(referenced) ? string.Empty : " — **referenced table is not defined in the supplied source**";
                references.Add($"- `{table.Name}` ({string.Join(", ", constraint.Columns)}) depends on `{referenced}` via {constraint.Name ?? "an unnamed foreign key"}{status}");
            }
        }

        if (references.Count == 0)
        {
            Line("No foreign key relationships were parsed from the supplied source.");
        }
        else
        {
            foreach (string reference in references)
            {
                Line(reference);
            }
        }

        Line();
        Line("## Index to table");
        Line();

        if (analysis.Schema.Indexes.Count == 0)
        {
            Line("No indexes were parsed from the supplied source.");
        }
        else
        {
            foreach (OracleIndex index in analysis.Schema.Indexes)
            {
                Line($"- `{index.Name}` indexes `{index.Table}` on ({string.Join(", ", index.Columns)})");
            }
        }

        Line();
        Line("## Forms modules");
        Line();

        if (analysis.Binaries.Count == 0)
        {
            Line("No Oracle Forms binary modules were found.");
        }
        else
        {
            Line($"{analysis.Binaries.Count.ToString(CultureInfo.InvariantCulture)} binary modules are present. No module-to-module, module-to-library, or");
            Line("module-to-table dependency is listed for them: the dependencies live inside the binary format and");
            Line("cannot be read without Oracle Forms Builder or the Forms JDAPI. Treat this section as unknown, not empty.");
        }

        Line();
        return markdown.ToString();
    }

    private static string RenderTechnicalDebt(PhaseExecutionContext context, Analysis analysis)
    {
        StringBuilder markdown = new();
        void Line(string text = "") => markdown.Append(text).Append('\n');

        Line("# Technical debt report");
        Line();
        Line($"Application: {context.Request.ApplicationName}");
        Line();
        Line("Every finding below cites the parsed object it came from. Findings that would require reading a");
        Line("binary Forms module, running the application, or querying a live database are not listed, because");
        Line("none of those happened.");
        Line();

        if (analysis.Debt.Count == 0)
        {
            Line("No evidence-based findings. The supplied source did not exhibit any of the conditions this");
            Line("analysis checks for.");
            return markdown.ToString();
        }

        foreach (IGrouping<string, DebtFinding> group in analysis.Debt.GroupBy(finding => finding.Title))
        {
            Line($"## {group.Key}");
            Line();
            foreach (DebtFinding finding in group)
            {
                Line($"- {finding.Evidence}");
            }

            Line();
        }

        return markdown.ToString();
    }

    // ---------- helpers ----------

    private static void Write(
        PhaseExecutionContext context,
        List<ArtifactReference> written,
        string path,
        ArtifactKind kind,
        string description,
        string content)
    {
        context.Workspace.WriteText(path, content);
        written.Add(context.Plan.ExpectedOutputs.FirstOrDefault(artifact =>
            string.Equals(artifact.Path, path, StringComparison.OrdinalIgnoreCase)) ?? new ArtifactReference(path, kind, description));
        context.Info($"Wrote {path}.");
    }

    private static string Source(Analysis analysis, string table) =>
        analysis.TableSources.TryGetValue(table, out string? path) ? path : "unknown";

    private static string Category(string relativePath) =>
        s_categories.TryGetValue(Path.GetExtension(relativePath), out string? category) ? category : "Other";

    private static string Number(long? value) =>
        value is long resolved ? resolved.ToString(CultureInfo.InvariantCulture) : "not specified";

    private static string Describe(OracleConstraint constraint) => constraint.Kind switch
    {
        OracleConstraintKind.PrimaryKey => $"PRIMARY KEY ({string.Join(", ", constraint.Columns)}) {Named(constraint)}",
        OracleConstraintKind.Unique => $"UNIQUE ({string.Join(", ", constraint.Columns)}) {Named(constraint)}",
        OracleConstraintKind.ForeignKey => $"FOREIGN KEY ({string.Join(", ", constraint.Columns)}) REFERENCES {constraint.ReferencedTable} {Named(constraint)}",
        _ => $"CHECK ({constraint.CheckExpression}) {Named(constraint)}",
    };

    private static string Named(OracleConstraint constraint) =>
        constraint.Name is string name ? $"— `{name}`" : "— unnamed";
}
