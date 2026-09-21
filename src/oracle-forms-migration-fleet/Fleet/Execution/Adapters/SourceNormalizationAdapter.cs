// Copyright (c) Microsoft. All rights reserved.

using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace OracleFormsMigrationFleet.Fleet.Execution.Adapters;

/// <summary>
/// Normalizes the Forms estate into the textual intermediate representation the converters read, and
/// refuses the run when that text does not exist.
///
/// The adapter runs no Oracle tool and decodes no binary. A .fmb, .mmb, .pll, or .olb is counted by name
/// and size and then left alone, because opening one needs Forms Builder or the Forms JDAPI and this fleet
/// bundles neither. The only Forms source it can honestly claim to have read is a supplied XML export.
///
/// That makes the phase a gate as much as a converter: when the estate is binary-only, it fails closed with
/// the operator-side normalization route written down, rather than letting a later phase generate screens
/// from table structure and present them as a migration of those modules.
/// </summary>
public sealed class SourceNormalizationAdapter : IPhaseAdapter
{
    private const int MaxFiles = 20_000;
    private const long MaxTextBytes = 8L * 1024 * 1024;

    private static readonly JsonSerializerOptions s_json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Forms source that is a proprietary binary. Never opened, only counted.</summary>
    private static readonly Dictionary<string, string> s_binaryModules = new(StringComparer.OrdinalIgnoreCase)
    {
        [".olb"] = "Object library (binary)",
        [".pll"] = "PL/SQL library (binary)",
        [".mmb"] = "Menu module (binary)",
        [".fmb"] = "Forms module (binary)",
    };

    /// <summary>Forms text exports that a current Builder still cannot consume directly.</summary>
    private static readonly Dictionary<string, string> s_legacyTextModules = new(StringComparer.OrdinalIgnoreCase)
    {
        [".fmt"] = "Forms module (6i text export)",
        [".mmt"] = "Menu module (6i text export)",
    };

    /// <summary>Oracle's documented upgrade order; shared dependencies have to resolve before dependents.</summary>
    private static readonly string[] s_dependencyOrder = [".olb", ".pll", ".mmb", ".fmb"];

    /// <summary>The only module types a FormModule XML export can be coverage for.</summary>
    private static readonly string[] s_formModuleExtensions = [".fmb", ".fmt"];

    public MigrationPhase Phase => MigrationPhase.SourceNormalization;

    public Task<PhaseExecutionResult> ExecuteAsync(PhaseExecutionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        string sourceRoot = WorkspacePath.Normalize(context.SourceRoot);
        string outputRoot = WorkspacePath.Normalize(context.OutputRoot);

        if (!context.Workspace.DirectoryExists(sourceRoot))
        {
            return Task.FromResult(PhaseExecutionResult.Failure(
                $"The source root '{sourceRoot}' does not exist in the workspace. Nothing was normalized and no artifact was written."));
        }

        OracleVersionAssessment requestedForms = OracleLegacyVersionCatalog.Forms(context.Request.OracleFormsVersion);
        OracleVersionAssessment requestedDatabase = OracleLegacyVersionCatalog.Database(context.Request.OracleDatabaseVersion);

        Inventory inventory;

        // A refused limit means part of the tree was never looked at. The manifest and report below both
        // claim to be per-file inventories of the whole estate, so neither is written from a partial walk.
        try
        {
            inventory = Take(context, sourceRoot, cancellationToken);
        }
        catch (WorkspaceLimitExceededException limit)
        {
            return Task.FromResult(PhaseExecutionResult.Failure(
                $"Normalization was stopped before anything was written: {limit.Message} A normalization manifest covering only " +
                "the part that fitted would report modules as absent that were never looked at, and the conversion that reads it " +
                "would generate from that gap."));
        }

        Verdict verdict = Judge(requestedForms, inventory, context.Request);

        foreach (string warning in verdict.Warnings)
        {
            context.Warn(warning);
        }

        List<ArtifactReference> artifacts = [];
        string reportPath = $"{outputRoot}/intermediate/source-version-report.md";
        string manifestPath = $"{outputRoot}/intermediate/forms-normalization-manifest.json";

        context.Workspace.WriteText(reportPath, RenderReport(context, sourceRoot, requestedForms, requestedDatabase, inventory, verdict));
        artifacts.Add(new ArtifactReference(
            reportPath,
            ArtifactKind.Documentation,
            "Requested and detected Oracle Forms and Database releases, the normalization route, and what each claim rests on."));

        context.Workspace.WriteText(manifestPath, RenderManifest(sourceRoot, requestedForms, requestedDatabase, inventory, verdict));
        artifacts.Add(new ArtifactReference(
            manifestPath,
            ArtifactKind.NormalizedSource,
            "Per-file normalization inventory: which modules were read as text, which were counted unopened, and every version each file declares."));

        if (!verdict.Normalized)
        {
            // The diagnostics are the point of the failure, so they stay on disk and stay attributed.
            context.Warn(verdict.Reason);
            return Task.FromResult(new PhaseExecutionResult(false, artifacts, verdict.Findings, verdict.Reason));
        }

        string irPath = $"{outputRoot}/intermediate/forms-ir.json";
        string intermediate = RenderIntermediate(sourceRoot, inventory, verdict);
        int intermediateBytes = Encoding.UTF8.GetByteCount(intermediate);

        if (intermediateBytes > FormsIntermediateReader.MaxDocumentBytes)
        {
            const int MiB = 1024 * 1024;
            string reason =
                $"The normalized Forms representation would contain {intermediateBytes / MiB} MiB, while this build reads at most " +
                $"{FormsIntermediateReader.MaxDocumentBytes / MiB} MiB. Nothing was normalized, because writing an IR that the next " +
                "phase cannot read would turn a source-size limit into a late and misleading conversion failure.";
            context.Warn(reason);
            return Task.FromResult(new PhaseExecutionResult(false, artifacts, verdict.Findings, reason));
        }

        context.Workspace.WriteText(irPath, intermediate);
        artifacts.Add(new ArtifactReference(
            irPath,
            ArtifactKind.NormalizedSource,
            "Normalized intermediate representation of the Forms modules that were readable as text. Trigger bodies are retained as untrusted source text, not translated behaviour."));

        context.Info(verdict.Reason);

        return Task.FromResult(PhaseExecutionResult.Success(artifacts, verdict.Findings));
    }

    // ---------- inventory ----------

    private sealed record ModuleFile(string Path, string Extension, string Category, long Bytes, bool Decoded);

    private sealed record XmlFile(
        string Path,
        long Bytes,
        FormsXmlDeclaration Declaration,
        IReadOnlyList<FormsModule> Modules,
        IReadOnlyList<ConversionFinding> Findings);

    private sealed record Inventory(
        IReadOnlyList<ModuleFile> Binaries,
        IReadOnlyList<ModuleFile> LegacyText,
        IReadOnlyList<XmlFile> Xml,
        IReadOnlyList<string> Unreadable)
    {
        public IReadOnlyList<XmlFile> FormsXml => [.. Xml.Where(file => file.Declaration.HasFormModule)];

        /// <summary>Files supplied as .xml that could not be parsed as XML at all.</summary>
        public IReadOnlyList<XmlFile> MalformedXml => [.. Xml.Where(file => file.Declaration.ParseError is not null)];

        /// <summary>Well-formed XML that names a Forms element in a root shape or namespace this fleet does not read.</summary>
        public IReadOnlyList<XmlFile> RejectedShapeXml => [.. Xml.Where(file => file.Declaration.ShapeRejection is not null)];

        public IReadOnlyList<FormsModule> Modules => [.. FormsXml.SelectMany(file => file.Modules)];

        public IReadOnlyList<ConversionFinding> ParseFindings => [.. FormsXml.SelectMany(file => file.Findings)];

        /// <summary>Findings that say the file itself could not be read as a Forms module, not that its behaviour was skipped.</summary>
        public IReadOnlyList<ConversionFinding> BlockingParseFindings =>
        [
            .. ParseFindings.Where(finding =>
                finding.Severity == ConversionSeverity.Unsupported
                && string.Equals(finding.Category, "Forms module", StringComparison.Ordinal)),
        ];

        public IReadOnlyList<ModuleFile> UnopenedModules => [.. Binaries, .. LegacyText];

        public bool HasAnyFormsSource => Binaries.Count > 0 || LegacyText.Count > 0 || FormsXml.Count > 0;
    }

    private static Inventory Take(PhaseExecutionContext context, string sourceRoot, CancellationToken cancellationToken)
    {
        List<ModuleFile> binaries = [];
        List<ModuleFile> legacyText = [];
        List<XmlFile> xml = [];
        List<string> unreadable = [];

        foreach (WorkspaceFile file in context.Workspace.EnumerateFiles(sourceRoot, MaxFiles))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string extension = Path.GetExtension(file.RelativePath);

            if (s_binaryModules.TryGetValue(extension, out string? binaryCategory))
            {
                binaries.Add(new ModuleFile(file.RelativePath, extension, binaryCategory, file.Length, Decoded: false));
                continue;
            }

            if (s_legacyTextModules.TryGetValue(extension, out string? textCategory))
            {
                // Counted, not parsed: Oracle's own guidance is that these need 6i tooling before anything else.
                legacyText.Add(new ModuleFile(file.RelativePath, extension, textCategory, file.Length, Decoded: false));
                continue;
            }

            if (!extension.Equals(".xml", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                string text = context.Workspace.ReadText(file.RelativePath, MaxTextBytes);
                FormsXmlDeclaration declaration = FormsXmlVersionReader.Read(text);

                if (!declaration.HasFormModule)
                {
                    xml.Add(new XmlFile(file.RelativePath, file.Length, declaration, [], []));
                    continue;
                }

                FormsModuleParse parsed = FormsModuleParser.Parse(text);
                xml.Add(new XmlFile(file.RelativePath, file.Length, declaration, parsed.Modules, parsed.Findings));

                context.Info(
                    $"Read {parsed.Modules.Count.ToString(CultureInfo.InvariantCulture)} Forms module(s) from {file.RelativePath}" +
                    (declaration.AnyDeclaredVersion is { } declared ? $", which declares Forms version {declared}." : ", which declares no Forms version."));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                unreadable.Add(file.RelativePath);
                context.Warn($"{file.RelativePath} could not be read and was skipped.");
            }
        }

        return new Inventory(
            [.. binaries.OrderBy(file => Array.IndexOf(s_dependencyOrder, file.Extension.ToLowerInvariant())).ThenBy(file => file.Path, StringComparer.Ordinal)],
            [.. legacyText.OrderBy(file => file.Path, StringComparer.Ordinal)],
            [.. xml.OrderBy(file => file.Path, StringComparer.Ordinal)],
            unreadable);
    }

    // ---------- gate ----------

    /// <summary>
    /// <paramref name="Detected"/> is only ever what a supplied export declared. <paramref name="Effective"/> is
    /// the release the run proceeds on, which may be the operator's word. Conflating the two would let an
    /// intake value read back as though a file had confirmed it.
    /// </summary>
    private sealed record Verdict(
        bool Normalized,
        string Reason,
        OracleVersionAssessment? Detected,
        OracleVersionAssessment? Effective,
        string VersionAuthority,
        IReadOnlyList<string> Findings,
        IReadOnlyList<string> Warnings);

    /// <summary>
    /// Decides whether textual Forms source exists, covers the estate, and declares a release that agrees
    /// with the request. Every refusal names the operator-side step that would resolve it.
    /// </summary>
    private static Verdict Judge(OracleVersionAssessment requested, Inventory inventory, MigrationRunRequest request)
    {
        List<string> findings = [];
        List<string> warnings = [];

        if (requested.Readiness == OracleConversionReadiness.Rejected)
        {
            return Refuse($"The requested Oracle Forms version was not interpreted. {requested.Disposition}");
        }

        bool formsDeclaredByEvidence = request.Evidence?.Any(item =>
            item is { Kind: EvidenceKind.FormsModuleSource or EvidenceKind.FormsXmlExport }) == true;

        // A file supplied as .xml that will not parse is not the same as no Forms source. Treating it as
        // absent would hand a schema-only generation to a run that believes it migrated its screens.
        if (inventory.MalformedXml.Count > 0)
        {
            return Refuse(
                $"{inventory.MalformedXml.Count.ToString(CultureInfo.InvariantCulture)} supplied XML file(s) could not be parsed as XML, " +
                "so no Forms module was read from them and nothing was normalized: " +
                string.Join("; ", inventory.MalformedXml.Select(file => $"{file.Path} ({file.Declaration.ParseError})")) + ". " +
                "Re-export them with frmf2xml and supply the result. A Forms export carrying a DTD is also refused here, because this fleet " +
                "resolves no external entity in untrusted input.");
        }

        if (inventory.Unreadable.Count > 0)
        {
            return Refuse(
                $"{inventory.Unreadable.Count.ToString(CultureInfo.InvariantCulture)} supplied file(s) under the source root could not be read " +
                $"({string.Join(", ", inventory.Unreadable)}), so the estate was not fully inspected and nothing was normalized.");
        }

            if (inventory.Modules.Count > FormsIntermediateReader.MaxModules)
            {
                return Refuse(
                $"The supplied exports declare {inventory.Modules.Count.ToString(CultureInfo.InvariantCulture)} Forms modules across the estate, " +
                $"while this build retains at most {FormsIntermediateReader.MaxModules.ToString(CultureInfo.InvariantCulture)}. Nothing was " +
                "normalized, because writing an IR that the next phase refuses would move a known source-size blocker into a late conversion failure.");
            }

        // Well-formed XML that names a Forms element in a shape this fleet does not read is a refusal, not
        // an unrelated file. Passing over it would drop a module from the estate without saying so.
        if (inventory.RejectedShapeXml.Count > 0)
        {
            return Refuse(
                $"{inventory.RejectedShapeXml.Count.ToString(CultureInfo.InvariantCulture)} supplied XML file(s) name an Oracle Forms element " +
                "but are not an export in a root shape and namespace this fleet reads, so nothing was normalized: " +
                string.Join(" ", inventory.RejectedShapeXml.Select(file => $"{file.Path}: {file.Declaration.ShapeRejection}")));
        }

        if (!inventory.HasAnyFormsSource)
        {
            // Distinguishes "nothing was supplied" from "something was supplied and none of it is a Forms
            // export". Reporting the second as the first would hand a schema-only generation to a run that
            // believes its screens were migrated.
            string supplied = inventory.Xml.Count == 0
                ? "No file of any Forms module type was present either."
                : $"{inventory.Xml.Count.ToString(CultureInfo.InvariantCulture)} XML file(s) were supplied but none carries a FormModule element " +
                  $"({string.Join(", ", inventory.Xml.Select(file => file.Path))}), so none of them is an Oracle Forms export.";

            if (formsDeclaredByEvidence)
            {
                // The request attests that Forms source exists. Succeeding here would let a schema-only
                // generation inherit an attestation about modules that were never supplied.
                return Refuse(
                    "The run supplies verified FormsModuleSource or FormsXmlExport evidence, but no Oracle Forms module source of any kind was found " +
                    $"under the source root '{request.SourceRoot}'. {supplied} The evidence and the workspace contradict each other, so nothing was normalized. " +
                    "Supply the modules the evidence describes, or withdraw that evidence and run this as a database-only migration.");
            }

            // Nothing to normalize is not the same as normalization failing. The schema-only path stays open,
            // and the gap is recorded so no later artifact can imply a Forms module was migrated.
            findings.Add(
                $"No Oracle Forms module source of any kind was found under the source root and the run declared no Forms source evidence, so nothing was normalized. {supplied} " +
                "Any application tier generated by this run comes from database structure alone and reproduces no Forms screen, trigger, or navigation rule.");

            return new Verdict(true, findings[0], null, null, "no Forms source supplied", findings, []);
        }

        IReadOnlyList<XmlFile> formsXml = inventory.FormsXml;

        if (formsXml.Count == 0)
        {
            string reason = inventory.Binaries.Count > 0 || inventory.LegacyText.Count > 0
                ? inventory.LegacyText.Count > 0 && inventory.Binaries.Count == 0
                    ? BuildLegacyTextRefusal(requested, inventory)
                    : BuildBinaryRefusal(requested, inventory)
                : $"{inventory.Xml.Count.ToString(CultureInfo.InvariantCulture)} XML file(s) were supplied but none carries a FormModule element " +
                  $"({string.Join(", ", inventory.Xml.Select(file => file.Path))}), so none of them is an Oracle Forms export. " +
                  "Nothing was normalized. Export the modules with frmf2xml and supply that text.";

            findings.Add(reason);
            findings.AddRange(requested.NormalizationGuidance);

            return new Verdict(false, reason, null, null, "none", findings, [reason]);
        }

        if (inventory.BlockingParseFindings.Count > 0)
        {
            return Refuse(
                "The supplied Forms XML carries a FormModule element but could not be read as a Forms module: " +
                string.Join("; ", inventory.BlockingParseFindings.Select(finding => $"{finding.Construct} — {finding.Reason}")) + ". " +
                "Nothing was normalized, and no application conversion may generate from this estate.");
        }

        if (inventory.Modules.Count == 0 || inventory.Modules.All(module => module.Blocks.Count == 0))
        {
            return Refuse(
                $"The supplied Forms XML declares {inventory.Modules.Count.ToString(CultureInfo.InvariantCulture)} FormModule element(s) and no block in any of them, " +
                "so no screen, base table, or item was recovered. An export this empty carries no structure to normalize, and generating from the schema instead " +
                "would present CRUD over the converted tables as a migration of those modules.");
        }

        // Partial coverage is the failure mode that looks most like success: one exported module beside
        // several binaries used to normalize cleanly and leave the rest silently absent from the output.
        if (Coverage(inventory) is { Count: > 0 } problems)
        {
            return Refuse(
                "The supplied Forms estate is not covered one-to-one by readable XML exports, so it is only partially normalized: " +
                string.Join(" ", problems) + " " +
                "An unrelated export is not coverage for a module nobody opened, so the run was refused rather than migrating part of the " +
                $"application silently. Normalize every module in dependency order ({string.Join(", ", s_dependencyOrder)}), export each one " +
                "with frmf2xml under its own module name into the directory that module was supplied from, and supply the complete set.");
        }

        (OracleVersionAssessment? detected, string? conflict, IReadOnlyList<string> declared) = ResolveDeclared(formsXml);

        if (conflict is not null)
        {
            findings.Add(conflict);
            return new Verdict(false, conflict, null, null, "contradicted", findings, [conflict]);
        }

        if (declared.Count == 0)
        {
            if (requested.IsUnknown)
            {
                const string Reason =
                    "The supplied Forms XML declares no version attribute and the run supplied no Oracle Forms release, so the release behind this estate is not established from either side. " +
                    "Legacy generation needs one of the two: add the release at intake, or supply an export that declares it.";

                findings.Add(Reason);
                return new Verdict(false, Reason, null, null, "none", findings, [Reason]);
            }

            string operatorSupplied =
                $"The supplied Forms XML declares no version attribute. The release recorded for this run is Oracle Forms {requested.Label}, supplied by the operator; " +
                "the export did not verify it, and this fleet contacted nothing that could.";

            findings.Add(operatorSupplied);
            warnings.Add(operatorSupplied);

            return new Verdict(true, operatorSupplied, null, requested, "operator-supplied, unverified by the export", findings, warnings);
        }

        if (detected is null || !detected.IsRecognized)
        {
            string reason =
                $"The supplied Forms XML declares version '{declared[0]}', which matches no Oracle Forms release this catalog knows. " +
                "The export was not treated as evidence of a release, and normalization stopped rather than recording a version nothing can interpret.";

            findings.Add(reason);
            return new Verdict(false, reason, null, null, "none", findings, [reason]);
        }

        // Compared by lineage: a broad intake value such as '12c' agrees with a precise '12.2.1.4' export,
        // while '12.2.1.4' against '12.2.1.5' is a real contradiction about the same estate.
        if (!requested.IsUnknown && !detected.IsCompatibleWith(requested))
        {
            string reason =
                $"The run declares Oracle Forms {requested.Label} but the supplied export declares {detected.Label}. " +
                "One of the two is wrong, and generating from a source whose release contradicts the run would make every downstream version claim unreliable. " +
                "Correct the intake value or supply the export that matches it.";

            findings.Add(reason);
            return new Verdict(false, reason, detected, null, "contradicted", findings, [reason]);
        }

        if (detected.Readiness == OracleConversionReadiness.AssessmentOnly)
        {
            warnings.Add(detected.Disposition);
            findings.Add(detected.Disposition);
        }

        if (string.Equals(detected.Family, "6i", StringComparison.Ordinal))
        {
            string bridge =
                $"The export declares Oracle Forms 6i. Oracle recommends upgrading 6i modules through Forms {OracleLegacyVersionCatalog.BridgeRelease} in most cases before a current release, and FRM-18130 proves that bridge is mandatory where it is raised. " +
                "This fleet read the supplied text only; it did not perform, observe, or verify that upgrade.";

            findings.Add(bridge);
            warnings.Add(bridge);
        }

        findings.AddRange(inventory.ParseFindings.Select(finding => $"{finding.Severity}: {finding.Category} — {finding.Construct}: {finding.Reason}"));

        string summary =
            $"Normalized {inventory.Modules.Count.ToString(CultureInfo.InvariantCulture)} Forms module(s) from " +
            $"{formsXml.Count.ToString(CultureInfo.InvariantCulture)} XML export(s) declaring Oracle Forms {detected.Label}.";

        return new Verdict(
            true,
            summary,
            detected,
            detected,
            requested.IsUnknown ? "declared by the supplied export" : "declared by the export and matching the run",
            findings,
            warnings);

        static Verdict Refuse(string reason) => new(false, reason, null, null, "none", [reason], [reason]);
    }

    /// <summary>
    /// Every way the supplied estate fails to map one-to-one onto readable exports.
    ///
    /// Coverage is typed, not name-shaped. A FormModule XML export is an export of a form and of nothing
    /// else, so it can cover a <c>.fmb</c> or its <c>.fmt</c> text form and can never cover a menu module,
    /// a PL/SQL library, or an object library: this parser has no representation for any of those, and the
    /// earlier name-only match let <c>ORDERS.mmb</c> be declared covered by <c>ORDERS.xml</c>, which meant
    /// a menu nobody could open was reported as normalized.
    ///
    /// Coverage is also directory-scoped. An estate that carries <c>forms-a/ORDERS.fmb</c> and
    /// <c>forms-b/ORDERS.fmb</c> carries two different modules that happen to share a name, and the earlier
    /// global match let one export in <c>forms-b</c> stand in for the module in <c>forms-a</c> that nobody
    /// opened. Each directory has to supply its own export, and two same-named modules in separate
    /// directories are accepted when each one is paired locally rather than refused for the name clash.
    ///
    /// Identity is the module name, normalized the same way on both sides so an export numbered for load
    /// order still matches the module it contains, and the file name and the embedded module name have to
    /// agree so an export cannot be filed under the name of a different module.
    /// </summary>
    private static IReadOnlyList<string> Coverage(Inventory inventory)
    {
        List<string> problems = [];

        // One XML per form per directory, and its file name has to name the module inside it.
        Dictionary<string, string> exported = new(StringComparer.Ordinal);

        foreach (XmlFile file in inventory.FormsXml)
        {
            string basename = Path.GetFileNameWithoutExtension(file.Path);
            string identity = Identity(basename);

            if (file.Modules.Count != 1)
            {
                problems.Add(
                    $"`{file.Path}` declares {file.Modules.Count.ToString(CultureInfo.InvariantCulture)} FormModule elements. " +
                    "One export carries one form, so a file carrying several cannot be attributed to a source module.");
                continue;
            }

            string declared = Identity(file.Modules[0].Name);

            if (!string.Equals(identity, declared, StringComparison.Ordinal))
            {
                problems.Add(
                    $"`{file.Path}` contains FormModule '{file.Modules[0].Name}', which does not match its own file name. " +
                    "An export filed under a different module's name cannot be used as coverage for either of them.");
                continue;
            }

            string key = LocalIdentity(file.Path, identity);

            if (exported.TryGetValue(key, out string? first))
            {
                problems.Add(
                    $"`{file.Path}` and `{first}` both export module '{file.Modules[0].Name}' from the same directory. " +
                    "Two exports of one module are ambiguous, and choosing either would make the generated application depend on path order.");
                continue;
            }

            exported[key] = file.Path;
        }

        // Ambiguous source modules: the same module name, of the same type, supplied twice from one directory.
        foreach (IGrouping<string, ModuleFile> duplicate in inventory.UnopenedModules
            .GroupBy(
                file => $"{file.Extension.ToLowerInvariant()}|{LocalIdentity(file.Path, Identity(Path.GetFileNameWithoutExtension(file.Path)))}",
                StringComparer.Ordinal)
            .Where(group => group.Count() > 1))
        {
            problems.Add(
                $"{string.Join(" and ", duplicate.Select(file => $"`{file.Path}`"))} are the same module name and type supplied twice from " +
                "one directory. Which one the estate actually uses is a FORMS_PATH question this fleet cannot answer, so neither was accepted.");
        }

        foreach (ModuleFile file in inventory.UnopenedModules)
        {
            string identity = Identity(Path.GetFileNameWithoutExtension(file.Path));

            if (!s_formModuleExtensions.Contains(file.Extension, StringComparer.OrdinalIgnoreCase))
            {
                // No FormModule XML represents a menu, a library, or an object library, so nothing supplied
                // as XML can be coverage for one. Saying so is the point: it is not an unfound match.
                problems.Add(
                    $"`{file.Path}` is a {file.Category.ToLowerInvariant()}. A Forms XML export carries a FormModule and has no representation " +
                    "for this module type at all, so no supplied export can cover it and its contents were never read.");
                continue;
            }

            if (!exported.ContainsKey(LocalIdentity(file.Path, identity)))
            {
                problems.Add(
                    $"`{file.Path}` has no readable FormModule XML export of the same module name in its own directory `{Folder(file.Path)}`. " +
                    "An export of that name elsewhere in the estate is not coverage for it, because which module a name resolves to is a " +
                    "FORMS_PATH question this fleet cannot answer.");
            }
        }

        return problems;
    }

    /// <summary>
    /// A module identity scoped to the directory it was supplied from, so coverage is decided locally.
    /// The directory is case-folded because the file systems these estates arrive on treat it that way.
    /// </summary>
    private static string LocalIdentity(string path, string identity) => $"{Folder(path).ToUpperInvariant()}|{identity}";

    /// <summary>The containing directory of a workspace-relative path, as written.</summary>
    private static string Folder(string path)
    {
        string normalized = WorkspacePath.Normalize(path);
        int separator = normalized.LastIndexOf('/');

        return separator < 0 ? string.Empty : normalized[..separator];
    }

    /// <summary>
    /// The comparable identity of a module name or export file name.
    ///
    /// Case is folded, separators are unified, and a numeric load-order prefix is dropped, because Oracle
    /// estates routinely number exports for ordering (<c>005_bank_account_request_form.xml</c> carries
    /// FormModule <c>BANK_ACCOUNT_REQUEST_FORM</c>). Nothing else is removed: two different modules must
    /// not normalize to the same identity.
    /// </summary>
    private static string Identity(string name) => FormsModuleIdentity.Normalize(name);

    /// <summary>
    /// Reduces every release the supplied exports declare to one, or reports the contradiction.
    ///
    /// Releases are compared by lineage, not as text. A family alias such as "12c", a broad "12.2", and a
    /// wildcard "12.2.1.x" are all the same claim as "12.2.1.4" stated less precisely, so the most precise
    /// declaration wins. Two releases that diverge at any segment are a real disagreement and the run stops:
    /// choosing the first in path order would make the recorded release depend on file names.
    /// </summary>
    private static (OracleVersionAssessment? Detected, string? Conflict, IReadOnlyList<string> Declared) ResolveDeclared(
        IReadOnlyList<XmlFile> formsXml)
    {
        List<(string Value, string Path)> declarations = [];

        foreach (XmlFile file in formsXml)
        {
            foreach (string value in file.Declaration.DeclaredVersions)
            {
                declarations.Add((value, file.Path));
            }
        }

        List<string> distinct = [.. declarations.Select(entry => entry.Value).Distinct(StringComparer.OrdinalIgnoreCase)];
        if (distinct.Count == 0)
        {
            return (null, null, distinct);
        }

        List<OracleVersionAssessment> assessed = [.. distinct.Select(OracleLegacyVersionCatalog.Forms)];
        if (assessed.Any(assessment => !assessment.IsRecognized))
        {
            return (assessed.First(assessment => !assessment.IsRecognized), null, distinct);
        }

        string Cite(Func<OracleVersionAssessment, bool> match) => string.Join(
            ", ",
            declarations
                .Where(entry => match(OracleLegacyVersionCatalog.Forms(entry.Value)))
                .Select(entry => $"'{entry.Value}' in {entry.Path}")
                .Distinct(StringComparer.Ordinal));

        List<string> families = [.. assessed.Select(assessment => assessment.Family).Distinct(StringComparer.Ordinal)];
        if (families.Count > 1)
        {
            return (
                null,
                $"The supplied Forms exports declare more than one Oracle Forms family ({string.Join(" and ", families)}): {Cite(_ => true)}. " +
                "A single run cannot be normalized against two releases, so nothing was normalized. Split the estate by release, or correct the exports.",
                distinct);
        }

        List<OracleVersionAssessment> withRelease = [.. assessed.Where(assessment => assessment.Release is { Length: > 0 })];

        foreach (OracleVersionAssessment left in withRelease)
        {
            foreach (OracleVersionAssessment right in withRelease)
            {
                if (!OracleVersionAssessment.SharesLineage(left.Release!, right.Release!))
                {
                    return (
                        null,
                        $"The supplied Forms exports declare two Oracle Forms {families[0]} releases that are not the same release stated at " +
                        $"different precision ({left.Release} and {right.Release}): {Cite(assessment => assessment.Release is { Length: > 0 })}. " +
                        "Two patch levels cannot both have produced this estate, so nothing was normalized rather than recording whichever file was read first.",
                        distinct);
                }
            }
        }

        // Every remaining declaration is the same release at a different precision, so the most precise wins.
        OracleVersionAssessment detected = withRelease.Count > 0
            ? withRelease
                .OrderByDescending(assessment => assessment.Specificity == VersionSpecificity.Exact)
                .ThenByDescending(assessment => assessment.Release!.Count(character => character == '.'))
                .ThenBy(assessment => assessment.Release, StringComparer.Ordinal)
                .First()
            : assessed[0];

        return (detected, null, distinct);
    }

    private static string BuildBinaryRefusal(OracleVersionAssessment requested, Inventory inventory)
    {
        string release = requested.IsUnknown ? "an unrecorded release" : $"Oracle Forms {requested.Label}";

        return
            $"{inventory.Binaries.Count.ToString(CultureInfo.InvariantCulture)} binary Oracle Forms module(s) were supplied at {release} and no readable FormModule XML accompanies them. " +
            "Their contents are a proprietary binary that needs Forms Builder or the Forms JDAPI, neither of which this fleet bundles or runs, so no intermediate representation was produced and no application conversion may claim to have migrated them. " +
            $"Normalize the estate with an operator-provided Oracle toolchain in dependency order ({string.Join(", ", s_dependencyOrder)}), export the result to Forms XML with frmf2xml, and supply that text.";
    }

    private static string BuildLegacyTextRefusal(OracleVersionAssessment requested, Inventory inventory)
    {
        string release = requested.IsUnknown ? "an unrecorded release" : $"Oracle Forms {requested.Label}";

        return
            $"{inventory.LegacyText.Count.ToString(CultureInfo.InvariantCulture)} .fmt/.mmt text module(s) were supplied at {release} and no normalized FormModule XML accompanies them. " +
            "Oracle states a current Builder cannot convert 6i .fmt/.mmt directly, because obsolete properties may be present. The conversion is two steps: " +
            "first use a 6i Builder or Compiler to produce 6i .fmb/.mmb, then open, save, and compile those binaries with the newer toolchain and export to Forms XML. " +
            "This fleet performed neither step and produced no intermediate representation.";
    }

    // ---------- artifacts ----------

    private sealed record ManifestVersion(
        string Requested,
        string Family,
        string? Release,
        bool Recognized,
        bool InRequestedLegacyRange,
        string Readiness,
        string Disposition,
        IReadOnlyList<string> NormalizationGuidance,
        IReadOnlyList<string> Warnings);

    private sealed record ManifestFile(
        string Path,
        string Category,
        long Bytes,
        bool ReadAsText,
        string? DeclaredVersion,
        string? DeclaredFormsVersion,
        string? DeclaredFamily,
        IReadOnlyList<string> Modules,
        int RetainedSourceFacts);

    private sealed record Manifest(
        string Generator,
        string SourceRoot,
        bool Normalized,
        string Outcome,
        string VersionAuthority,
        ManifestVersion RequestedForms,
        ManifestVersion RequestedDatabase,
        ManifestVersion? DetectedForms,
        ManifestVersion? EffectiveForms,
        IReadOnlyList<string> DependencyOrder,
        IReadOnlyList<ManifestFile> Files,
        IReadOnlyList<string> Findings,
        IReadOnlyList<string> Notes);

    private static string DescribeXml(XmlFile file) => file switch
    {
        { Declaration.ParseError: not null } => "XML file that could not be parsed",
        { Declaration.ShapeRejection: not null } => "XML file naming a Forms element in a shape this fleet does not read",
        { Declaration.HasFormModule: true } => "Forms module (XML export)",
        _ => "XML file with no FormModule element",
    };

    private static ManifestVersion Describe(OracleVersionAssessment assessment) => new(
        assessment.Supplied.Length > 0 ? assessment.Supplied : OracleLegacyVersionCatalog.UnknownFamily,
        assessment.Family,
        assessment.Release,
        assessment.IsRecognized,
        assessment.IsInLegacyRange,
        assessment.Readiness.ToString(),
        assessment.Disposition,
        assessment.NormalizationGuidance,
        assessment.Warnings);

    private static string RenderManifest(
        string sourceRoot,
        OracleVersionAssessment requestedForms,
        OracleVersionAssessment requestedDatabase,
        Inventory inventory,
        Verdict verdict)
    {
        List<ManifestFile> files =
        [
            .. inventory.Binaries.Select(file => new ManifestFile(file.Path, file.Category, file.Bytes, false, null, null, null, [], 0)),
            .. inventory.LegacyText.Select(file => new ManifestFile(file.Path, file.Category, file.Bytes, false, null, null, null, [], 0)),
            .. inventory.Xml.Select(file => new ManifestFile(
                file.Path,
                DescribeXml(file),
                file.Bytes,
                file.Declaration.HasFormModule,
                file.Declaration.DeclaredVersion,
                file.Declaration.DeclaredFormsVersion,
                file.Declaration.AnyDeclaredVersion is { } declared ? OracleLegacyVersionCatalog.Forms(declared).Family : null,
                [.. file.Modules.Select(module => module.Name)],
                file.Modules.Sum(module => module.SourceFacts?.Facts.Count ?? 0))),
        ];

        Manifest manifest = new(
            FormsIntermediateReader.Generator,
            sourceRoot,
            verdict.Normalized,
            verdict.Reason,
            verdict.VersionAuthority,
            Describe(requestedForms),
            Describe(requestedDatabase),
            verdict.Detected is null ? null : Describe(verdict.Detected),
            verdict.Effective is null ? null : Describe(verdict.Effective),
            s_dependencyOrder,
            [.. files.OrderBy(file => file.Path, StringComparer.Ordinal)],
            verdict.Findings,
            [
                "Files with readAsText=false were counted by name and size only. No binary Forms module was decoded and no Oracle tool was executed.",
                "detectedForms is present only when a supplied export declared a version. effectiveForms is the release this run proceeded on, which may be the operator's intake value; versionAuthority says which.",
                "declaredVersion and declaredFormsVersion are attributes the supplied file carries. They are not proof of provenance: nothing here establishes that the file came from frmf2xml, from a licensed Forms installation, or from the release it names.",
                "A recognized release means an intake and normalization route is defined for it. It is not a statement that an application generated from that release has been compiled, deployed, or behaviourally tested.",
                "retainedSourceFacts counts the elements retained from that file's exports as declared source facts. It is a count of what was found, not a coverage measure: nothing here establishes that the export contained everything the module declares, and no fact retained is a statement about Oracle Forms runtime behaviour.",
            ]);

        return JsonSerializer.Serialize(manifest, s_json);
    }

    private sealed record IntermediateItem(string Name, string ItemType, string? DataType, string? ColumnName, string? Prompt, bool Required, bool Visible, int? MaxLength);

    private sealed record IntermediateTrigger(string Name, string Scope, string? Body, string? BodyEncoding);

    private sealed record IntermediateBlock(
        string Name,
        string? BaseTable,
        int RecordsDisplayed,
        IReadOnlyList<IntermediateItem> Items,
        IReadOnlyList<IntermediateTrigger> Triggers);

    private sealed record IntermediateAttribute(string Name, string Namespace, string Value);

    private sealed record IntermediateFact(
        string Id,
        int Order,
        string? ParentId,
        int ChildIndex,
        string LocalName,
        string Namespace,
        string? DeclaredName,
        IReadOnlyList<IntermediateAttribute> Attributes,
        string? Text,
        string Kind);

    private sealed record IntermediateFacts(
        string TextDigest,
        string? WrapperDeclaredVersion,
        IReadOnlyList<IntermediateFact> Facts);

    private sealed record IntermediateModule(
        string Name,
        string? Title,
        string SourcePath,
        string? DeclaredVersion,
        string DeclaredFamily,
        IReadOnlyList<IntermediateBlock> Blocks,
        IReadOnlyList<IntermediateTrigger> Triggers,
        IReadOnlyList<string> ProgramUnits,
        IReadOnlyList<string> Lovs,
        IntermediateFacts SourceFacts);

    private sealed record Intermediate(
        string Generator,
        string SchemaVersion,
        bool Normalized,
        string SourceRoot,
        string FormsFamily,
        string VersionAuthority,
        IReadOnlyList<IntermediateModule> Modules,
        IReadOnlyList<string> Notes);

    private static string RenderIntermediate(string sourceRoot, Inventory inventory, Verdict verdict)
    {
        List<IntermediateModule> modules = [];

        foreach (XmlFile file in inventory.FormsXml)
        {
            string? declaredVersion = file.Declaration.AnyDeclaredVersion;
            string declaredFamily = declaredVersion is null
                ? OracleLegacyVersionCatalog.UnknownFamily
                : OracleLegacyVersionCatalog.Forms(declaredVersion).Family;

            foreach (FormsModule module in file.Modules)
            {
                modules.Add(new IntermediateModule(
                    module.Name,
                    module.Title,
                    file.Path,
                    declaredVersion,
                    declaredFamily,
                    [.. module.Blocks.Select(block => new IntermediateBlock(
                        block.Name,
                        block.BaseTable,
                        block.RecordsDisplayed,
                        [.. block.Items.Select(item => new IntermediateItem(
                            item.Name, item.ItemType, item.DataType, item.ColumnName, item.Prompt, item.Required, item.Visible, item.MaxLength))],
                        [.. block.Triggers.Select(trigger => new IntermediateTrigger(
                            trigger.Name, trigger.Scope, trigger.Body, trigger.BodyEncoding?.ToString()))]))],
                    [.. module.Triggers.Select(trigger => new IntermediateTrigger(
                        trigger.Name, trigger.Scope, trigger.Body, trigger.BodyEncoding?.ToString()))],
                    module.ProgramUnits,
                    module.Lovs,
                    Facts(module.SourceFacts)));
            }
        }

        Intermediate intermediate = new(
            FormsIntermediateReader.Generator,
            FormsIntermediateReader.SchemaVersion,
            Normalized: true,
            sourceRoot,
            verdict.Effective?.Family ?? OracleLegacyVersionCatalog.UnknownFamily,
            verdict.VersionAuthority,
            [.. modules.OrderBy(module => module.Name, StringComparer.Ordinal)],
            [
                "This representation carries blocks, base tables, items, trigger identities, and XML-normalized trigger body text, plus the names of program units and LOVs.",
                "Trigger bodies are retained as untrusted PL/SQL source text so changed behaviour remains distinguishable. BodyEncoding records whether Oracle supplied text as an XML attribute or child element; attribute text is subject to XML attribute whitespace normalization. Bodies are not translated or executed.",
                "Program-unit bodies and LOV queries are not retained.",
                "Modules that exist only as a binary are absent entirely. Their absence here is not evidence that they carry no behaviour.",
                "sourceFacts retains every element of the export in document order with its source-object path, its parent, and all of its declared attributes, so what the interpreted structure above leaves out stays recoverable. Values are the XML-normalized text the export carried and are not decoded again.",
                "Every source fact is Declared: it is something the export wrote down. None is a default, an inherited value, or a statement about what Oracle Forms would do at runtime, and retaining an element is not a claim that this build understands it.",
                "wrapperDeclaredVersion is the Module wrapper's version attribute verbatim. It is a string the file carried, not a release this fleet adjudicated; formsFamily above is the adjudicated one.",
                "textDigest is SHA-256 of the export text this fleet parsed. It is not a digest of the file's bytes on disk and not a snapshot identifier.",
            ]);

        return JsonSerializer.Serialize(intermediate, s_json);
    }

    /// <summary>
    /// Projects a retained fact set for serialization. A module whose facts were not retained writes an
    /// empty inventory, which its reader refuses: an IR that silently carried no facts would be
    /// indistinguishable from an export that declared nothing beyond the structure the parser interprets.
    /// </summary>
    private static IntermediateFacts Facts(FormsSourceFactSet? set) => set is null
        ? new IntermediateFacts(FormsSourceFactReader.TextDigest(null), null, [])
        : new IntermediateFacts(
            set.TextDigest,
            set.WrapperDeclaredVersion,
            [
                .. set.Facts.Select(fact => new IntermediateFact(
                    fact.Id,
                    fact.Order,
                    fact.ParentId,
                    fact.ChildIndex,
                    fact.LocalName,
                    fact.Namespace,
                    fact.DeclaredName,
                    [.. fact.Attributes.Select(attribute => new IntermediateAttribute(attribute.Name, attribute.Namespace, attribute.Value))],
                    fact.Text,
                    fact.Kind.ToString())),
            ]);

    private static string RenderReport(
        PhaseExecutionContext context,
        string sourceRoot,
        OracleVersionAssessment requestedForms,
        OracleVersionAssessment requestedDatabase,
        Inventory inventory,
        Verdict verdict)
    {
        StringBuilder markdown = new();
        void Line(string text = "") => markdown.Append(text).Append('\n');

        Line("# Source version report");
        Line();
        Line($"Application: {context.Request.ApplicationName}");
        Line($"Engagement: {context.Request.EngagementId}");
        Line($"Source root: `{sourceRoot}`");
        Line();
        Line($"Outcome: **{(verdict.Normalized ? "normalized" : "refused")}**. {verdict.Reason}");
        Line();

        Line("## Releases");
        Line();
        Line("| Question | Answer |");
        Line("| --- | --- |");
        Line($"| Oracle Forms release declared at intake | {Escape(requestedForms.Supplied.Length > 0 ? requestedForms.Supplied : "unknown")} |");
        Line($"| Interpreted as | {Escape(requestedForms.Label)} |");
        Line($"| Oracle Forms release declared by the supplied export | {Escape(verdict.Detected?.Label ?? "not declared by any supplied export")} |");
        Line($"| Release this run proceeded on | {Escape(verdict.Effective?.Label ?? "none established")} |");
        Line($"| Which release this run is treating as authoritative | {Escape(verdict.VersionAuthority)} |");
        Line($"| Oracle Database release declared at intake | {Escape(requestedDatabase.Supplied.Length > 0 ? requestedDatabase.Supplied : "unknown")} |");
        Line($"| Interpreted as | {Escape(requestedDatabase.Label)} |");
        Line($"| Forms conversion readiness | {requestedForms.Readiness} |");
        Line($"| Database conversion readiness | {requestedDatabase.Readiness} |");
        Line();
        Line(requestedForms.Disposition);
        Line();
        Line(requestedDatabase.Disposition);
        Line();

        Line("## Forms source found");
        Line();

        if (!inventory.HasAnyFormsSource)
        {
            Line("No Oracle Forms module source of any kind was found under the source root.");
        }
        else
        {
            Line("| File | Category | Bytes | Read as text | Declared version |");
            Line("| --- | --- | --- | --- | --- |");

            foreach (ModuleFile file in inventory.Binaries.Concat(inventory.LegacyText))
            {
                Line($"| `{file.Path}` | {file.Category} | {file.Bytes.ToString(CultureInfo.InvariantCulture)} | no | not readable without Oracle tooling |");
            }

            foreach (XmlFile file in inventory.Xml)
            {
                Line($"| `{file.Path}` | {DescribeXml(file)} | {file.Bytes.ToString(CultureInfo.InvariantCulture)} | {(file.Declaration.HasFormModule ? "yes" : "no")} | {Escape(string.Join(", ", file.Declaration.DeclaredVersions) is { Length: > 0 } declared ? declared : "none")} |");
            }

            Line();
            Line("A binary module is counted by name and size and never decoded. Opening one requires Oracle Forms Builder or the");
            Line("Forms JDAPI, and this fleet bundles neither and executes no Oracle tool.");
        }

        Line();
        Line("## Normalization route");
        Line();
        Line($"Oracle's documented dependency order is `{string.Join("`, `", s_dependencyOrder)}`, with shared dependencies resolvable through `FORMS_PATH`.");
        Line();

        if (requestedForms.NormalizationGuidance.Count == 0)
        {
            Line("No release-specific route can be selected until the Oracle Forms release is recorded at intake or declared by a supplied export.");
        }
        else
        {
            foreach (string step in requestedForms.NormalizationGuidance)
            {
                Line($"1. {step}");
            }
        }

        Line();
        Line("Every step above is performed by the operator, on their own Oracle installation, under their own licence and support");
        Line("terms. This fleet runs no Oracle tool, holds no `ORACLE_HOME`, and cannot confirm that any of them were carried out.");
        Line();

        if (verdict.Findings.Count > 0)
        {
            Line("## Findings");
            Line();
            foreach (string finding in verdict.Findings)
            {
                Line($"- {finding}");
            }

            Line();
        }

        Line("## What this report does not establish");
        Line();
        Line("- It does not prove the estate runs the release it names. Every release here was either typed at intake or read from");
        Line("  an attribute inside a supplied file, and a file declaring a version is not provenance.");
        Line("- It does not claim runtime parity for any release. A defined intake route is not a tested conversion.");
        Line("- It retains XML-normalized trigger body text as untrusted source in `forms-ir.json`, but does not translate or execute it.");
        Line("  Program-unit bodies, LOV queries, menu logic, library bodies, and object-library contents are not retained.");

        return markdown.ToString();
    }

    private static string Escape(string value) => value.Replace("|", "\\|", StringComparison.Ordinal);
}
