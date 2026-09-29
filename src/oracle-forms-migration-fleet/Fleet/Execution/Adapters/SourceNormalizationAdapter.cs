// Copyright (c) Microsoft. All rights reserved.

using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace OracleFormsMigrationFleet.Fleet.Execution.Adapters;

/// <summary>
/// Normalizes the Forms estate into the textual intermediate representation the converters read, and
/// refuses the run when that text does not exist.
///
/// The adapter runs no Oracle tool and decodes no binary itself. A .fmb, .mmb, .pll, or .olb is counted by
/// name and size and then left alone, because opening one needs Forms Builder or the Forms JDAPI and this
/// fleet bundles neither. Two things can therefore be read as Forms source: a supplied XML export, and an
/// extraction that the source gateway performed and that this server admitted against the module's own
/// digest.
///
/// The second one is the reason a server-side claim exists at all. Both artifacts of a prepared module sit
/// inside the operator's session copy, and that copy came from an uploaded archive or a public repository,
/// so a <c>.fleet-source/extraction</c> folder full of convincing JSON proves nothing whatsoever. What
/// decides the question is the ledger this server wrote outside the copy: an artifact it does not name is
/// an upload, and one it names but whose bytes do not hash to the digest it recorded is a tampered or a
/// half-written file. Either way the phase stops, because reading one would attribute a generated
/// application to a module nobody opened.
///
/// That makes the phase a gate as much as a converter: when the estate is binary-only and unprepared, it
/// fails closed with the operator-side normalization route written down, rather than letting a later phase
/// generate screens from table structure and present them as a migration of those modules.
/// </summary>
public sealed class SourceNormalizationAdapter : IPhaseAdapter
{
    private const int MaxFiles = 20_000;
    private const long MaxTextBytes = 8L * 1024 * 1024;

    /// <summary>Largest module this phase will digest to decide whether an admitted extraction covers it.</summary>
    private const long MaxModuleDigestBytes = 256L * 1024 * 1024;

    /// <summary>
    /// Largest prepared artifact this phase reads. It is the protocol's own ceiling on an inlined
    /// extraction, so every artifact the gateway could have returned and this server could have admitted is
    /// readable here: a smaller limit would refuse bytes it had already vouched for.
    /// </summary>
    private const long MaxPreparedArtifactBytes = SourceGatewayProtocol.MaxArtifactBytes;

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

    /// <summary>
    /// One extraction this server admitted, matched back to the module in the estate it was taken from.
    ///
    /// <paramref name="ModulePath"/> is not read from the artifact. It is the file in the source tree whose
    /// bytes hash to the digest the server recorded before it called the gateway, which is what ties a
    /// generated screen to a module that is actually present rather than to a name a document supplied.
    /// </summary>
    private sealed record NativeModule(
        string ArtifactPath,
        string ProvenancePath,
        string ModulePath,
        long ArtifactBytes,
        PreparedSourceClaim Claim,
        FormsModule Module,
        IReadOnlyList<ConversionFinding> Findings);

    private sealed record Inventory(
        IReadOnlyList<ModuleFile> Binaries,
        IReadOnlyList<ModuleFile> LegacyText,
        IReadOnlyList<XmlFile> Xml,
        IReadOnlyList<string> Unreadable,
        IReadOnlyList<NativeModule> Native,
        string? TrustRejection)
    {
        public IReadOnlyList<XmlFile> FormsXml => [.. Xml.Where(file => file.Declaration.HasFormModule)];

        /// <summary>Files supplied as .xml that could not be parsed as XML at all.</summary>
        public IReadOnlyList<XmlFile> MalformedXml => [.. Xml.Where(file => file.Declaration.ParseError is not null)];

        /// <summary>Well-formed XML that names a Forms element in a root shape or namespace this fleet does not read.</summary>
        public IReadOnlyList<XmlFile> RejectedShapeXml => [.. Xml.Where(file => file.Declaration.ShapeRejection is not null)];

        public IReadOnlyList<FormsModule> Modules =>
            [.. FormsXml.SelectMany(file => file.Modules), .. Native.Select(entry => entry.Module)];

        public IReadOnlyList<ConversionFinding> ParseFindings =>
            [.. FormsXml.SelectMany(file => file.Findings), .. Native.SelectMany(entry => entry.Findings)];

        /// <summary>Findings that say the file itself could not be read as a Forms module, not that its behaviour was skipped.</summary>
        public IReadOnlyList<ConversionFinding> BlockingParseFindings =>
        [
            .. ParseFindings.Where(finding =>
                finding.Severity == ConversionSeverity.Unsupported
                && string.Equals(finding.Category, "Forms module", StringComparison.Ordinal)),
        ];

        public IReadOnlyList<ModuleFile> UnopenedModules => [.. Binaries, .. LegacyText];

        /// <summary>Estate paths an admitted extraction already covers, so coverage does not ask for an export of them.</summary>
        public IReadOnlySet<string> NativelyCovered =>
            new HashSet<string>(Native.Select(entry => entry.ModulePath), StringComparer.Ordinal);

        public bool HasAnyFormsSource => Binaries.Count > 0 || LegacyText.Count > 0 || FormsXml.Count > 0;

        /// <summary>True when something readable as a Forms module was found, by either route.</summary>
        public bool HasReadableFormsSource => FormsXml.Count > 0 || Native.Count > 0;
    }

    private static Inventory Take(PhaseExecutionContext context, string sourceRoot, CancellationToken cancellationToken)
    {
        List<ModuleFile> binaries = [];
        List<ModuleFile> legacyText = [];
        List<XmlFile> xml = [];
        List<string> unreadable = [];
        List<WorkspaceFile> prepared = [];

        string preparedRoot = sourceRoot.Length == 0
            ? PreparedSourceTrustStore.PreparedFolder
            : $"{sourceRoot}/{PreparedSourceTrustStore.PreparedFolder}";

        foreach (WorkspaceFile file in context.Workspace.EnumerateFiles(sourceRoot, MaxFiles))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string extension = Path.GetExtension(file.RelativePath);

            if (file.RelativePath.StartsWith($"{preparedRoot}/", StringComparison.Ordinal))
            {
                prepared.Add(file);
                continue;
            }

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

        List<ModuleFile> orderedBinaries =
            [.. binaries.OrderBy(file => Array.IndexOf(s_dependencyOrder, file.Extension.ToLowerInvariant())).ThenBy(file => file.Path, StringComparer.Ordinal)];

        (IReadOnlyList<NativeModule> native, string? trustRejection) =
            Admit(context, sourceRoot, orderedBinaries, prepared, cancellationToken);

        foreach (NativeModule entry in native)
        {
            context.Info(
                $"Read Forms module '{entry.Module.Name}' from the admitted extraction {entry.ArtifactPath}, which this server pinned to " +
                $"{entry.ModulePath} by content digest.");
        }

        return new Inventory(
            orderedBinaries,
            [.. legacyText.OrderBy(file => file.Path, StringComparer.Ordinal)],
            [.. xml.OrderBy(file => file.Path, StringComparer.Ordinal)],
            unreadable,
            native,
            trustRejection);
    }

    // ---------- admitted extractions ----------

    /// <summary>
    /// Decides which files under the prepared-source folder are extractions this server admitted, and
    /// turns those into readable Forms modules.
    ///
    /// Every refusal here stops the whole phase rather than dropping one module, because each of them
    /// means the estate contains something that is presenting itself as admitted source and is not:
    /// continuing would normalize the rest and leave the operator with a partial migration described as a
    /// whole one.
    ///
    /// The checks, in the order a forgery meets them:
    /// <list type="number">
    /// <item><description>The server's own ledgers — one for modules, one for schema statements — are read
    /// from outside the session copy, so nothing in the copy can supply, edit, or reconstruct either. A
    /// ledger that exists and cannot be believed is a refusal, never an empty one.</description></item>
    /// <item><description>Every file in the prepared folder has to be named by a claim in one of them. An
    /// uploaded artifact has no claim, so it is refused by its presence alone.</description></item>
    /// <item><description>Both files of every claim have to be present and hash to the digests recorded for
    /// them, so a tampered artifact and a commit that stopped half-way are both refused rather than read.</description></item>
    /// <item><description>The artifact is read as the untrusted document it is, and has to be the native
    /// worker's own extraction output for the claim's module, profile, and content digest.</description></item>
    /// <item><description>The module it describes has to be a file in this estate whose bytes hash to the
    /// digest the server pinned before it called the gateway, and whose file name identifies that module.</description></item>
    /// </list>
    /// </summary>
    private static (IReadOnlyList<NativeModule> Modules, string? Rejection) Admit(
        PhaseExecutionContext context,
        string sourceRoot,
        IReadOnlyList<ModuleFile> binaries,
        IReadOnlyList<WorkspaceFile> prepared,
        CancellationToken cancellationToken)
    {
        PreparedSourceTrustRead read = PreparedSourceTrustStore.Read(context.WorkspaceRoot, context.PreparedSourceBinding);

        if (read.Ledger is not { } ledger)
        {
            return ([], read.Error);
        }

        PreparedSchemaTrustRead schemaRead = PreparedSchemaTrustStore.Read(context.WorkspaceRoot, context.PreparedSourceBinding);

        if (schemaRead.Ledger is not { } schemaLedger)
        {
            return ([], schemaRead.Error);
        }

        List<PreparedSourceClaim> claims =
        [
            .. ledger.Claims
                .Where(claim => string.Equals(WorkspacePath.Normalize(claim.SourceRoot), sourceRoot, StringComparison.Ordinal))
                .OrderBy(claim => claim.ArtifactPath, StringComparer.Ordinal),
        ];

        List<PreparedSchemaClaim> schemaClaims =
        [
            .. schemaLedger.Claims
                .Where(claim => string.Equals(WorkspacePath.Normalize(claim.SourceRoot), sourceRoot, StringComparison.Ordinal))
                .OrderBy(claim => claim.SchemaDdlPath, StringComparer.Ordinal),
        ];

        if (prepared.Count == 0 && claims.Count == 0 && schemaClaims.Count == 0)
        {
            return ([], null);
        }

        HashSet<string> admitted = new(StringComparer.Ordinal);
        foreach (PreparedSourceClaim claim in claims)
        {
            admitted.Add(WorkspacePath.Normalize(claim.ArtifactPath));
            admitted.Add(WorkspacePath.Normalize(claim.ProvenancePath));
        }

        foreach (PreparedSchemaClaim claim in schemaClaims)
        {
            foreach ((string path, _) in SchemaFiles(claim))
            {
                admitted.Add(path);
            }
        }

        List<string> unclaimed = [.. prepared.Select(file => file.RelativePath).Where(path => !admitted.Contains(path))];

        if (unclaimed.Count > 0)
        {
            return ([], Unclaimed(unclaimed));
        }

        if (AdmitSchema(context, claims, schemaClaims) is { } schemaRejection)
        {
            return ([], schemaRejection);
        }

        List<NativeModule> modules = [];

        // Every candidate module is digested once, before any claim is matched, so a copy holding many
        // modules and many claims costs one pass over the estate rather than one pass per claim.
        (Dictionary<string, List<ModuleFile>>? digests, string? digestRejection) = Digests(context, binaries);

        if (digests is null)
        {
            return ([], digestRejection);
        }

        foreach (PreparedSourceClaim claim in claims)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string artifactPath = WorkspacePath.Normalize(claim.ArtifactPath);
            string provenancePath = WorkspacePath.Normalize(claim.ProvenancePath);

            if (Intact(context, artifactPath, claim.ArtifactSha256, claim.ArtifactByteCount) is { } artifactRejection)
            {
                return ([], artifactRejection);
            }

            if (Intact(context, provenancePath, claim.ProvenanceSha256, claim.ProvenanceByteCount) is { } provenanceRejection)
            {
                return ([], provenanceRejection);
            }

            string text;
            try
            {
                text = context.Workspace.ReadText(artifactPath, MaxPreparedArtifactBytes);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or WorkspaceLimitExceededException)
            {
                return ([], $"The admitted extraction `{artifactPath}` could not be read, so nothing was normalized from it.");
            }

            (ExtractedModule? extracted, string? documentRejection) = ReadExtraction(text, claim);
            if (extracted is null)
            {
                return ([], documentRejection);
            }

            (ModuleFile? module, string? moduleRejection) = Pinned(digests, claim, extracted.Name);
            if (module is null)
            {
                return ([], moduleRejection);
            }

            (XElement? element, string? shapeRejection) = Build(extracted);
            if (element is null)
            {
                return ([], $"The admitted extraction `{artifactPath}` does not describe a Forms module this fleet reads: {shapeRejection}");
            }

            (FormsSourceFactSet? facts, string? factRejection) =
                FormsSourceFactReader.Read(element, claim.ArtifactSha256, wrapperDeclaredVersion: null);

            if (facts is null)
            {
                return ([], $"The admitted extraction `{artifactPath}` could not be retained element by element: {factRejection}");
            }

            List<ConversionFinding> findings = [];
            FormsModule projected = FormsModuleParser.Project(element, facts, findings);

            modules.Add(new NativeModule(
                artifactPath,
                provenancePath,
                module.Path,
                claim.ArtifactByteCount,
                claim,
                projected,
                findings));
        }

        return (modules, null);

        static string Unclaimed(IReadOnlyList<string> paths) =>
            $"{paths.Count.ToString(CultureInfo.InvariantCulture)} file(s) in this source copy sit in the prepared-source folder and this " +
            $"server holds no record of preparing them: {string.Join(", ", paths.Select(path => $"`{path}`"))}. Those bytes arrived with the " +
            "copy rather than from the source gateway, and an extraction artifact is not evidence of itself, so nothing was normalized. " +
            "Remove them and prepare the modules from the workbench, which is what records the server-side claim that makes them readable.";
    }

    private enum PreparedFileState
    {
        Intact,
        Missing,
        Altered,
        Undigestible,
    }

    /// <summary>
    /// Whether the bytes on disk are the bytes a claim was recorded over.
    ///
    /// A missing file is the shape a commit that stopped between recording the claim and renaming the
    /// artifact leaves behind, and a short or altered one is the shape editing it leaves behind. Both are
    /// refusals with the same consequence, because the claim is what makes the file readable at all.
    ///
    /// <paramref name="expectedBytes"/> is null where the claim recorded only a digest. The digest is the
    /// invariant; the length is a second reading of the same fact where one was written down.
    /// </summary>
    private static PreparedFileState Observe(PhaseExecutionContext context, string path, string expectedSha256, int? expectedBytes)
    {
        if (!context.Workspace.FileExists(path))
        {
            return PreparedFileState.Missing;
        }

        try
        {
            string observed = context.Workspace.Sha256(path, MaxPreparedArtifactBytes);
            long length = new FileInfo(context.Workspace.Resolve(path)).Length;

            return string.Equals(observed, expectedSha256, StringComparison.Ordinal)
                && (expectedBytes is not { } bytes || length == bytes)
                    ? PreparedFileState.Intact
                    : PreparedFileState.Altered;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or WorkspaceLimitExceededException or WorkspacePathException)
        {
            return PreparedFileState.Undigestible;
        }
    }

    /// <summary>Why the module artifact on disk is not the one the claim was recorded over, or nothing.</summary>
    private static string? Intact(PhaseExecutionContext context, string path, string expectedSha256, int expectedBytes) =>
        Observe(context, path, expectedSha256, expectedBytes) switch
        {
            PreparedFileState.Missing =>
                $"This server recorded preparing `{path}` into this source copy and the file is not there. A prepared artifact and the " +
                "record of it are committed together, so a record without its bytes is an incomplete admission rather than a smaller " +
                "estate. Nothing was normalized. Prepare the module again from the workbench.",
            PreparedFileState.Altered =>
                $"`{path}` does not hash to the digest this server recorded when it admitted those bytes. The file in this source copy " +
                "is not the artifact the source gateway returned, so nothing was normalized and no module was read from it.",
            PreparedFileState.Undigestible =>
                $"`{path}` could not be digested against the record this server holds for it, so nothing was normalized.",
            _ => null,
        };

    /// <summary>The files one admitted schema claim covers, each with the digest recorded for it.</summary>
    private static IEnumerable<(string Path, string Sha256)> SchemaFiles(PreparedSchemaClaim claim)
    {
        yield return (WorkspacePath.Normalize(claim.SchemaDdlPath), claim.SchemaDdlSha256);
        yield return (WorkspacePath.Normalize(claim.ProvenancePath), claim.ProvenanceSha256);

        if (claim.ProgramUnitPath is { Length: > 0 } units && claim.ProgramUnitSha256 is { Length: > 0 } digest)
        {
            yield return (WorkspacePath.Normalize(units), digest);
        }
    }

    /// <summary>
    /// Decides whether the prepared schema statements in this copy are the ones this server admitted.
    ///
    /// A module claim is pinned to the bytes of a file in the estate. A schema extraction has no such
    /// file, because its original source is the database the source environment profile names, so the
    /// profile IS that pin: the same immutable source environment, version and canonical hash the module
    /// claims are held to. A copy carrying statements prepared against a different profile describes two
    /// source environments, and one estate cannot have come from both.
    ///
    /// Nothing is read out of these files here. The schema half is admitted so that a combined
    /// preparation stops presenting itself as an upload; converting it remains the schema phase's work.
    /// </summary>
    private static string? AdmitSchema(
        PhaseExecutionContext context,
        IReadOnlyList<PreparedSourceClaim> modules,
        IReadOnlyList<PreparedSchemaClaim> schemas)
    {
        if (schemas.Count == 0)
        {
            return null;
        }

        (string Subject, string Environment, int Version, string Hash) pinned = modules.Count > 0
            ? ($"`{modules[0].ModuleAlias}`", modules[0].SourceEnvironmentId, modules[0].ProfileVersion, modules[0].ProfileHash)
            : ($"`{schemas[0].SchemaDdlPath}`", schemas[0].SourceEnvironmentId, schemas[0].ProfileVersion, schemas[0].ProfileHash);

        foreach (PreparedSchemaClaim claim in schemas)
        {
            foreach ((string path, string expected) in SchemaFiles(claim))
            {
                if (SchemaIntact(context, path, expected) is { } rejection)
                {
                    return rejection;
                }
            }

            if (!string.Equals(claim.SourceEnvironmentId, pinned.Environment, StringComparison.Ordinal)
                || claim.ProfileVersion != pinned.Version
                || !string.Equals(claim.ProfileHash, pinned.Hash, StringComparison.Ordinal))
            {
                return
                    $"`{claim.SchemaDdlPath}` was prepared against source environment '{claim.SourceEnvironmentId}' version " +
                    $"{claim.ProfileVersion.ToString(CultureInfo.InvariantCulture)} and {pinned.Subject} against " +
                    $"'{pinned.Environment}' version {pinned.Version.ToString(CultureInfo.InvariantCulture)}. A source environment " +
                    "profile is an immutable version because the release and schema allowlist behind it are what an operator approved, " +
                    "so this copy holds extractions of two different source environments and nothing was normalized. Take a fresh copy " +
                    "of the source and prepare every module and its schema against the current profile version.";
            }
        }

        return null;
    }

    /// <summary>Why the schema statements on disk are not the ones the claim was recorded over, or nothing.</summary>
    private static string? SchemaIntact(PhaseExecutionContext context, string path, string expectedSha256) =>
        Observe(context, path, expectedSha256, expectedBytes: null) switch
        {
            PreparedFileState.Missing =>
                $"This server recorded preparing `{path}` into this source copy and the file is not there. Prepared schema statements " +
                "and the record of them are committed together, so a record without its bytes is an incomplete admission rather than a " +
                "smaller estate. Nothing was normalized. Prepare the source schema again from the workbench.",
            PreparedFileState.Altered =>
                $"`{path}` does not hash to the digest this server recorded when it admitted those bytes. The schema statements in this " +
                "source copy are not the ones the source gateway returned, so nothing was normalized.",
            PreparedFileState.Undigestible =>
                $"`{path}` could not be digested against the record this server holds for it, so nothing was normalized.",
            _ => null,
        };

    /// <summary>
    /// Digests every form-module file in the estate, grouped by digest.
    ///
    /// Grouping rather than mapping is deliberate: two byte-identical modules are two files an extraction
    /// could equally have come from, and silently choosing one would attribute a generated screen to a path
    /// the operator never selected.
    /// </summary>
    private static (Dictionary<string, List<ModuleFile>>? Digests, string? Rejection) Digests(
        PhaseExecutionContext context,
        IReadOnlyList<ModuleFile> binaries)
    {
        Dictionary<string, List<ModuleFile>> digests = new(StringComparer.Ordinal);

        foreach (ModuleFile candidate in binaries)
        {
            if (!s_formModuleExtensions.Contains(candidate.Extension, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                string digest = context.Workspace.Sha256(candidate.Path, MaxModuleDigestBytes);

                if (!digests.TryGetValue(digest, out List<ModuleFile>? sharing))
                {
                    digests[digest] = sharing = [];
                }

                sharing.Add(candidate);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or WorkspaceLimitExceededException or WorkspacePathException)
            {
                return (null, $"`{candidate.Path}` could not be digested, so no admitted extraction could be matched to it and nothing was normalized.");
            }
        }

        return (digests, null);
    }

    /// <summary>
    /// Finds the module in this estate that the claim was pinned to, by digest and never by name.
    ///
    /// The digest was computed by this server over its own session copy before the gateway was called, so
    /// matching it establishes that the file still present is the file that was extracted. A claim whose
    /// module has been removed, replaced, or renamed to a different module matches nothing and stops the
    /// phase, because the alternative is generating a screen for a module the estate no longer contains.
    /// </summary>
    private static (ModuleFile? Module, string? Rejection) Pinned(
        Dictionary<string, List<ModuleFile>> digests,
        PreparedSourceClaim claim,
        string moduleName)
    {
        if (!digests.TryGetValue(claim.ModuleContentSha256, out List<ModuleFile>? sharing))
        {
            return (null,
                $"This server recorded extracting `{claim.ModuleAlias}` from this source copy, and no Forms module file under the source root " +
                "now has the content digest it was pinned to. The module was removed, replaced, or edited after it was prepared, so the " +
                "extraction describes source this run no longer holds and nothing was normalized.");
        }

        if (sharing.Count > 1)
        {
            return (null,
                $"{string.Join(" and ", sharing.Select(file => $"`{file.Path}`"))} are byte-identical, and the extraction of " +
                $"`{claim.ModuleAlias}` was pinned to that content. Which of them the estate actually uses is a FORMS_PATH question this " +
                "fleet cannot answer, so the extraction was attributed to neither and nothing was normalized.");
        }

        ModuleFile module = sharing[0];

        return FormsModuleIdentity.MatchesFile(module.Path, moduleName)
            ? (module, null)
            : (null,
                $"The admitted extraction of `{claim.ModuleAlias}` reports Forms module '{moduleName}' while the module it was pinned to " +
                $"is `{module.Path}`. An export filed under a different module's name cannot be attributed to either of them, so " +
                "nothing was normalized.");
    }

    // ---------- the admitted extraction document ----------

    private sealed record ExtractedTrigger(string Name, string Scope, string? Body);

    private sealed record ExtractedItem(
        string Name,
        string ItemType,
        string? DataType,
        string? ColumnName,
        string? Prompt,
        bool Required,
        bool Visible,
        int? MaxLength);

    private sealed record ExtractedBlock(
        string Name,
        string? BaseTable,
        int RecordsDisplayed,
        IReadOnlyList<ExtractedItem> Items,
        IReadOnlyList<ExtractedTrigger> Triggers);

    private sealed record ExtractedModule(
        string Name,
        string? Title,
        IReadOnlyList<ExtractedBlock> Blocks,
        IReadOnlyList<ExtractedTrigger> Triggers,
        IReadOnlyList<string> ProgramUnits,
        IReadOnlyList<string> Lovs);

    /// <summary>
    /// Reads an admitted artifact field by field, the way the intermediate reader reads its own output.
    ///
    /// The bytes were verified against the server's claim before this ran, so what is being guarded here is
    /// not transport but meaning: the document still has to be the native worker's extraction output for
    /// exactly this module, profile version and content digest, and it still must not declare the
    /// adjudication fields only the phase running right now is allowed to write.
    /// </summary>
    private static (ExtractedModule? Module, string? Rejection) ReadExtraction(string json, PreparedSourceClaim claim)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip });
        }
        catch (JsonException exception)
        {
            return (null, $"The admitted extraction of `{claim.ModuleAlias}` is not valid JSON: {exception.Message}");
        }

        using (document)
        {
            JsonElement root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
            {
                return (null, $"The admitted extraction of `{claim.ModuleAlias}` is not a JSON object.");
            }

            foreach (string field in SourceGatewayProtocol.AdjudicationFields)
            {
                if (root.TryGetProperty(field, out _))
                {
                    return (null,
                        $"The admitted extraction of `{claim.ModuleAlias}` declares '{field}', which only this phase may write. " +
                        "Extraction output that claims adjudication was refused rather than read.");
                }
            }

            if (!string.Equals(Text(root, "generator"), SourceGatewayProtocol.ExtractedIrGenerator, StringComparison.Ordinal)
                || !string.Equals(Text(root, "schemaVersion"), SourceGatewayProtocol.ExtractedIrSchemaVersion, StringComparison.Ordinal))
            {
                return (null,
                    $"The admitted extraction of `{claim.ModuleAlias}` was not written by the native source worker at the body version this " +
                    "build reads, so nothing is known to carry its field meanings and nothing was normalized.");
            }

            if (!string.Equals(Text(root, "moduleAlias"), claim.ModuleAlias, StringComparison.Ordinal)
                || !string.Equals(Text(root, "sourceEnvironmentId"), claim.SourceEnvironmentId, StringComparison.Ordinal)
                || !string.Equals(Text(root, "profileHash"), claim.ProfileHash, StringComparison.Ordinal)
                || !string.Equals(Text(root, "contentSha256"), claim.ModuleContentSha256, StringComparison.OrdinalIgnoreCase)
                || !root.TryGetProperty("profileVersion", out JsonElement version)
                || version.ValueKind != JsonValueKind.Number
                || !version.TryGetInt32(out int declaredVersion)
                || declaredVersion != claim.ProfileVersion)
            {
                return (null,
                    $"The admitted extraction of `{claim.ModuleAlias}` describes a different module, source environment, profile version, or " +
                    "content digest than the one this server recorded admitting. Nothing was normalized.");
            }

            if (!root.TryGetProperty("modules", out JsonElement modules)
                || modules.ValueKind != JsonValueKind.Array
                || modules.GetArrayLength() != 1)
            {
                return (null,
                    $"The admitted extraction of `{claim.ModuleAlias}` carries {(root.TryGetProperty("modules", out JsonElement declared) && declared.ValueKind == JsonValueKind.Array ? declared.GetArrayLength().ToString(CultureInfo.InvariantCulture) : "no")} " +
                    "extracted module. One module was prepared, so an artifact carrying any other number cannot be attributed to it.");
            }

            return ReadModule(modules[0], claim);
        }
    }

    private static (ExtractedModule? Module, string? Rejection) ReadModule(JsonElement module, PreparedSourceClaim claim)
    {
        string scope = $"the admitted extraction of `{claim.ModuleAlias}`";

        if (module.ValueKind != JsonValueKind.Object || Named(module) is not { } name)
        {
            return (null, $"The module in {scope} has no non-empty 'name'.");
        }

        if (Optional(module, "title") is var (title, titleError) && titleError is not null)
        {
            return (null, $"{titleError} in {scope}.");
        }

        (IReadOnlyList<ExtractedTrigger>? triggers, string? triggerError) = ReadTriggers(module, scope);
        if (triggers is null)
        {
            return (null, triggerError);
        }

        (IReadOnlyList<string>? units, string? unitError) = ReadNames(module, "programUnits", scope);
        (IReadOnlyList<string>? lovs, string? lovError) = ReadNames(module, "lovs", scope);

        if (units is null || lovs is null)
        {
            return (null, unitError ?? lovError);
        }

        // A bare name is all an older worker recorded for these, and a name is not what the object does.
        // Normalizing it would put a program unit with no PL/SQL body, or a LOV with no record group,
        // column mapping or return items, into the representation the generator reads as the whole module.
        if (units.Count > 0 || lovs.Count > 0)
        {
            return (null,
                $"The module in {scope} declares {units.Count.ToString(CultureInfo.InvariantCulture)} program unit(s) and " +
                $"{lovs.Count.ToString(CultureInfo.InvariantCulture)} list(s) of values as names only, with no body, type, record " +
                "group, column mapping, or return items beside them. That is what a worker predating this build recorded when it " +
                "could name those objects without reading them, and this fleet cannot normalize behaviour it was never given, so " +
                "nothing was normalized. Prepare the module again from the workbench against a source gateway that reads them.");
        }

        if (!module.TryGetProperty("blocks", out JsonElement declared) || declared.ValueKind != JsonValueKind.Array)
        {
            return (null, $"The module in {scope} declares no 'blocks' array.");
        }

        List<ExtractedBlock> blocks = [];

        foreach (JsonElement block in declared.EnumerateArray())
        {
            if (block.ValueKind != JsonValueKind.Object || Named(block) is not { } blockName)
            {
                return (null, $"A block in {scope} has no non-empty 'name'.");
            }

            if (Optional(block, "baseTable") is var (baseTable, baseTableError) && baseTableError is not null)
            {
                return (null, $"{baseTableError} of block '{blockName}' in {scope}.");
            }

            if (!block.TryGetProperty("recordsDisplayed", out JsonElement records)
                || records.ValueKind != JsonValueKind.Number
                || !records.TryGetInt32(out int recordsDisplayed)
                || recordsDisplayed < 0)
            {
                return (null, $"Block '{blockName}' in {scope} declares no whole 'recordsDisplayed' count.");
            }

            (IReadOnlyList<ExtractedTrigger>? blockTriggers, string? blockTriggerError) = ReadTriggers(block, scope);
            if (blockTriggers is null)
            {
                return (null, blockTriggerError);
            }

            if (!block.TryGetProperty("items", out JsonElement declaredItems) || declaredItems.ValueKind != JsonValueKind.Array)
            {
                return (null, $"Block '{blockName}' in {scope} declares no 'items' array.");
            }

            List<ExtractedItem> items = [];

            foreach (JsonElement item in declaredItems.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object || Named(item) is not { } itemName)
                {
                    return (null, $"An item of block '{blockName}' in {scope} has no non-empty 'name'.");
                }

                string itemScope = $"item '{blockName}.{itemName}' in {scope}";

                if (Text(item, "itemType") is not { Length: > 0 } itemType)
                {
                    return (null, $"The {itemScope} declares no 'itemType'.");
                }

                (string? dataType, string? dataTypeError) = Optional(item, "dataType");
                (string? columnName, string? columnError) = Optional(item, "columnName");
                (string? prompt, string? promptError) = Optional(item, "prompt");

                if ((dataTypeError ?? columnError ?? promptError) is { } optionalError)
                {
                    return (null, $"{optionalError} of the {itemScope}.");
                }

                if (Flag(item, "required") is not { } required || Flag(item, "visible") is not { } visible)
                {
                    return (null, $"The {itemScope} declares 'required' or 'visible' as something other than a boolean.");
                }

                if (!item.TryGetProperty("maxLength", out JsonElement declaredLength))
                {
                    return (null, $"The {itemScope} declares no 'maxLength'.");
                }

                int? maxLength;
                if (declaredLength.ValueKind == JsonValueKind.Null)
                {
                    maxLength = null;
                }
                else if (declaredLength.ValueKind == JsonValueKind.Number && declaredLength.TryGetInt32(out int length) && length >= 0)
                {
                    maxLength = length;
                }
                else
                {
                    return (null, $"The {itemScope} declares a 'maxLength' that is neither null nor a whole count.");
                }

                items.Add(new ExtractedItem(itemName, itemType, dataType, columnName, prompt, required, visible, maxLength));
            }

            blocks.Add(new ExtractedBlock(blockName, baseTable, recordsDisplayed, items, blockTriggers));
        }

        return (new ExtractedModule(name, title, blocks, triggers, units, lovs), null);
    }

    private static (IReadOnlyList<ExtractedTrigger>? Triggers, string? Rejection) ReadTriggers(JsonElement owner, string scope)
    {
        if (!owner.TryGetProperty("triggers", out JsonElement declared) || declared.ValueKind != JsonValueKind.Array)
        {
            return (null, $"An element of {scope} declares no 'triggers' array.");
        }

        List<ExtractedTrigger> triggers = [];

        foreach (JsonElement trigger in declared.EnumerateArray())
        {
            if (trigger.ValueKind != JsonValueKind.Object
                || Named(trigger) is not { } name
                || Text(trigger, "scope") is not { Length: > 0 } triggerScope)
            {
                return (null, $"A trigger in {scope} declares no non-empty 'name' and 'scope'.");
            }

            (string? body, string? bodyError) = Optional(trigger, "body");
            if (bodyError is not null)
            {
                return (null, $"{bodyError} of trigger '{name}' in {scope}.");
            }

            triggers.Add(new ExtractedTrigger(name, triggerScope, body));
        }

        return (triggers, null);
    }

    private static (IReadOnlyList<string>? Names, string? Rejection) ReadNames(JsonElement owner, string field, string scope)
    {
        if (!owner.TryGetProperty(field, out JsonElement declared) || declared.ValueKind != JsonValueKind.Array)
        {
            return (null, $"The module in {scope} declares no '{field}' array.");
        }

        List<string> names = [];

        foreach (JsonElement entry in declared.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.String || entry.GetString() is not { Length: > 0 } name)
            {
                return (null, $"A '{field}' entry in {scope} is not a non-empty string.");
            }

            names.Add(name);
        }

        return (names, null);
    }

    private static string? Text(JsonElement element, string field) =>
        element.TryGetProperty(field, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string? Named(JsonElement element) => Text(element, "name") is { } name && name.Trim().Length > 0 ? name : null;

    private static bool? Flag(JsonElement element, string field) =>
        element.TryGetProperty(field, out JsonElement value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.ValueKind == JsonValueKind.True
            : null;

    /// <summary>A field that is allowed to be absent or null, but never anything other than a string.</summary>
    private static (string? Value, string? Error) Optional(JsonElement element, string field)
    {
        if (!element.TryGetProperty(field, out JsonElement value) || value.ValueKind == JsonValueKind.Null)
        {
            return (null, null);
        }

        return value.ValueKind == JsonValueKind.String
            ? (value.GetString(), null)
            : (null, $"The '{field}' field is not a string");
    }

    /// <summary>
    /// Rebuilds the module the native worker read into the element shape an Oracle export carries, so the
    /// facts beside it and the structure generated from it both come out of the existing reader rather than
    /// out of a second projection written here.
    ///
    /// This is a translation of an admitted extraction, not the manufacture of an export: the tree is never
    /// written to disk, never presented as something frmf2xml produced, and never given a provenance it did
    /// not earn. What it buys is that the schema-3 reader's reconciliation — which rebuilds the retained
    /// facts and re-projects them — has one interpretation to compare against instead of two.
    /// </summary>
    private static (XElement? Element, string? Rejection) Build(ExtractedModule module)
    {
        XNamespace forms = FormsXmlDocument.Namespace;
        XElement element = new(forms + "FormModule", new XAttribute("Name", module.Name));

        if (module.Title is { Length: > 0 } title)
        {
            element.Add(new XAttribute("Title", title));
        }

        foreach (ExtractedTrigger trigger in module.Triggers)
        {
            if (!string.Equals(trigger.Scope, module.Name, StringComparison.Ordinal))
            {
                return (null,
                    $"a trigger on the module declares scope '{trigger.Scope}' rather than the module '{module.Name}'. A trigger this fleet " +
                    "cannot place is behaviour it cannot attribute, so nothing was read from it.");
            }

            element.Add(Trigger(forms, trigger));
        }

        foreach (string unit in module.ProgramUnits)
        {
            element.Add(new XElement(forms + "ProgramUnit", new XAttribute("Name", unit)));
        }

        foreach (string lov in module.Lovs)
        {
            element.Add(new XElement(forms + "LOV", new XAttribute("Name", lov)));
        }

        foreach (ExtractedBlock block in module.Blocks)
        {
            XElement declared = new(
                forms + "Block",
                new XAttribute("Name", block.Name),
                new XAttribute("RecordsDisplayed", block.RecordsDisplayed.ToString(CultureInfo.InvariantCulture)));

            if (block.BaseTable is { Length: > 0 } baseTable)
            {
                declared.Add(new XAttribute("QueryDataSourceName", baseTable));
            }

            foreach (ExtractedTrigger trigger in block.Triggers.Where(candidate =>
                string.Equals(candidate.Scope, block.Name, StringComparison.Ordinal)))
            {
                declared.Add(Trigger(forms, trigger));
            }

            foreach (ExtractedItem item in block.Items)
            {
                XElement declaredItem = new(
                    forms + "Item",
                    new XAttribute("Name", item.Name),
                    new XAttribute("ItemType", item.ItemType),
                    new XAttribute("Required", item.Required ? "Yes" : "No"),
                    new XAttribute("Visible", item.Visible ? "Yes" : "No"));

                if (item.DataType is { Length: > 0 } dataType)
                {
                    declaredItem.Add(new XAttribute("DataType", dataType));
                }

                // A column is what makes an item a database item, so the two are written together: an
                // extraction that reported no column describes an item Forms logic populates, and leaving
                // DatabaseItem to its default would invent a column named after the item.
                if (item.ColumnName is { Length: > 0 } column)
                {
                    declaredItem.Add(new XAttribute("ColumnName", column));
                    declaredItem.Add(new XAttribute("DatabaseItem", "Yes"));
                }
                else
                {
                    declaredItem.Add(new XAttribute("DatabaseItem", "No"));
                }

                if (item.Prompt is { Length: > 0 } prompt)
                {
                    declaredItem.Add(new XAttribute("Prompt", prompt));
                }

                if (item.MaxLength is { } maxLength)
                {
                    declaredItem.Add(new XAttribute("MaximumLength", maxLength.ToString(CultureInfo.InvariantCulture)));
                }

                string itemScope = $"{block.Name}.{item.Name}";

                foreach (ExtractedTrigger trigger in block.Triggers.Where(candidate =>
                    string.Equals(candidate.Scope, itemScope, StringComparison.Ordinal)))
                {
                    declaredItem.Add(Trigger(forms, trigger));
                }

                declared.Add(declaredItem);
            }

            if (block.Triggers.FirstOrDefault(candidate =>
                    !string.Equals(candidate.Scope, block.Name, StringComparison.Ordinal)
                    && !block.Items.Any(item => string.Equals(candidate.Scope, $"{block.Name}.{item.Name}", StringComparison.Ordinal)))
                is { } orphan)
            {
                return (null,
                    $"a trigger of block '{block.Name}' declares scope '{orphan.Scope}', which is neither the block nor one of its items. " +
                    "A trigger this fleet cannot place is behaviour it cannot attribute, so nothing was read from it.");
            }

            element.Add(declared);
        }

        return FormsXmlDocument.ShapeRejection(element) is { } rejection ? (null, rejection) : (element, null);

        static XElement Trigger(XNamespace forms, ExtractedTrigger trigger)
        {
            XElement declared = new(forms + "Trigger", new XAttribute("Name", trigger.Name));

            // A trigger with no body omits TriggerText entirely: an empty one is refused by the shape gate,
            // because a declared but blank body and an absent body are different facts about the source.
            if (!string.IsNullOrWhiteSpace(trigger.Body))
            {
                declared.Add(new XElement(forms + "TriggerText", trigger.Body));
            }

            return declared;
        }
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

        // Before anything is counted. A prepared-source folder this server cannot vouch for means the copy
        // contains something presenting itself as admitted extraction output, and normalizing the rest
        // around it would leave the operator with part of an estate reported as all of it.
        if (inventory.TrustRejection is { } trustRejection)
        {
            return Refuse(trustRejection);
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

        if (!inventory.HasReadableFormsSource)
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
                "Readable Forms source carries a module element but could not be read as a Forms module: " +
                string.Join("; ", inventory.BlockingParseFindings.Select(finding => $"{finding.Construct} — {finding.Reason}")) + ". " +
                "Nothing was normalized, and no application conversion may generate from this estate.");
        }

        if (inventory.Modules.Count == 0 || inventory.Modules.All(module => module.Blocks.Count == 0))
        {
            return Refuse(
                $"The readable Forms source declares {inventory.Modules.Count.ToString(CultureInfo.InvariantCulture)} module(s) and no block in any of them, " +
                "so no screen, base table, or item was recovered. Source this empty carries no structure to normalize, and generating from the schema instead " +
                "would present CRUD over the converted tables as a migration of those modules.");
        }

        // Partial coverage is the failure mode that looks most like success: one exported module beside
        // several binaries used to normalize cleanly and leave the rest silently absent from the output.
        if (Coverage(inventory) is { Count: > 0 } problems)
        {
            return Refuse(
                "The supplied Forms estate is not covered one-to-one by source this fleet can read, so it is only partially normalized: " +
                string.Join(" ", problems) + " " +
                "An unrelated export is not coverage for a module nobody opened, so the run was refused rather than migrating part of the " +
                $"application silently. Normalize every module in dependency order ({string.Join(", ", s_dependencyOrder)}), export each one " +
                "with frmf2xml under its own module name into the directory that module was supplied from, or prepare it from the workbench " +
                "against a source environment this server can reach, and supply the complete set.");
        }

        (OracleVersionAssessment? profileRelease, string? profileRejection) = ProfileRelease(inventory, requested);

        if (profileRejection is not null)
        {
            findings.Add(profileRejection);
            return new Verdict(false, profileRejection, null, null, "contradicted", findings, [profileRejection]);
        }

        (OracleVersionAssessment? detected, string? conflict, IReadOnlyList<string> declared) = ResolveDeclared(formsXml);

        if (conflict is not null)
        {
            findings.Add(conflict);
            return new Verdict(false, conflict, null, null, "contradicted", findings, [conflict]);
        }

        if (detected is { IsRecognized: true } && profileRelease is not null && !detected.IsCompatibleWith(profileRelease))
        {
            string reason =
                $"A supplied export declares Oracle Forms {detected.Label} while the source environment profile the admitted extractions were " +
                $"pinned to declares {profileRelease.Label}. One estate cannot have been produced by two releases, so nothing was normalized.";

            findings.Add(reason);
            return new Verdict(false, reason, detected, null, "contradicted", findings, [reason]);
        }

        if (declared.Count == 0)
        {
            if (requested.IsUnknown && profileRelease is null)
            {
                const string Reason =
                    "The supplied Forms XML declares no version attribute and the run supplied no Oracle Forms release, so the release behind this estate is not established from either side. " +
                    "Legacy generation needs one of the two: add the release at intake, or supply an export that declares it.";

                findings.Add(Reason);
                return new Verdict(false, Reason, null, null, "none", findings, [Reason]);
            }

            OracleVersionAssessment effective = requested.IsUnknown ? profileRelease! : requested;

            string authority = requested.IsUnknown
                ? "declared by the source environment profile the admitted extractions were pinned to, unverified by the extraction"
                : profileRelease is null
                    ? "operator-supplied, unverified by the export"
                    : "operator-supplied and agreeing with the source environment profile the admitted extractions were pinned to";

            string unverified = inventory.Native.Count > 0
                ? $"Nothing this fleet read declares a version attribute. The release recorded for this run is Oracle Forms {effective.Label}, {authority}; " +
                  "the extraction did not verify it, and this fleet ran no Oracle tool that could."
                : $"The supplied Forms XML declares no version attribute. The release recorded for this run is Oracle Forms {effective.Label}, supplied by the operator; " +
                  "the export did not verify it, and this fleet contacted nothing that could.";

            findings.Add(unverified);
            warnings.Add(unverified);
            findings.AddRange(NativeFindings(inventory));
            findings.AddRange(inventory.ParseFindings.Select(finding => $"{finding.Severity}: {finding.Category} — {finding.Construct}: {finding.Reason}"));

            return new Verdict(true, unverified, null, effective, authority, findings, warnings);
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

        findings.AddRange(NativeFindings(inventory));
        findings.AddRange(inventory.ParseFindings.Select(finding => $"{finding.Severity}: {finding.Category} — {finding.Construct}: {finding.Reason}"));

        string summary =
            $"Normalized {inventory.Modules.Count.ToString(CultureInfo.InvariantCulture)} Forms module(s) from " +
            $"{formsXml.Count.ToString(CultureInfo.InvariantCulture)} XML export(s) declaring Oracle Forms {detected.Label}" +
            (inventory.Native.Count == 0
                ? "."
                : $" and {inventory.Native.Count.ToString(CultureInfo.InvariantCulture)} admitted native extraction(s).");

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
    /// The release the source environment profile behind the admitted extractions declares, or the reason
    /// the extractions cannot be read as one estate.
    ///
    /// Two things are decided here. First, every admitted extraction under this source root has to have
    /// been prepared against the same immutable profile version: profiles are versioned because the
    /// release, path alias and schema allowlist an operator approved are what the gateway acted on, so a
    /// copy holding extractions from two of them describes two different source environments and stops the
    /// run rather than producing one estate out of both. Second, the release that profile declares has to
    /// agree with the release the run was started under, because a generated application is attributed to
    /// one of them and there is no honest way to pick.
    ///
    /// The profile's release is the operator's word, recorded before extraction. It is treated as exactly
    /// that: it establishes which release the run proceeds on and it verifies nothing.
    /// </summary>
    private static (OracleVersionAssessment? Release, string? Rejection) ProfileRelease(
        Inventory inventory,
        OracleVersionAssessment requested)
    {
        if (inventory.Native.Count == 0)
        {
            return (null, null);
        }

        PreparedSourceClaim first = inventory.Native[0].Claim;

        if (inventory.Native.FirstOrDefault(entry =>
                !string.Equals(entry.Claim.SourceEnvironmentId, first.SourceEnvironmentId, StringComparison.Ordinal)
                || entry.Claim.ProfileVersion != first.ProfileVersion
                || !string.Equals(entry.Claim.ProfileHash, first.ProfileHash, StringComparison.Ordinal))
            is { } divergent)
        {
            return (null,
                $"`{inventory.Native[0].ModulePath}` was prepared against source environment '{first.SourceEnvironmentId}' version " +
                $"{first.ProfileVersion.ToString(CultureInfo.InvariantCulture)} and `{divergent.ModulePath}` against " +
                $"'{divergent.Claim.SourceEnvironmentId}' version {divergent.Claim.ProfileVersion.ToString(CultureInfo.InvariantCulture)}. " +
                "A source environment profile is an immutable version because the release and allowlist behind it are what an operator " +
                "approved, so this copy holds extractions of two different source environments and nothing was normalized. Take a fresh " +
                "copy of the source and prepare every module against the current profile version.");
        }

        OracleVersionAssessment release = OracleLegacyVersionCatalog.Forms(first.ExpectedFormsRelease);

        if (!release.IsRecognized)
        {
            return (null,
                $"The source environment profile these extractions were pinned to declares Oracle Forms '{first.ExpectedFormsRelease}', which " +
                "matches no release this catalog knows. The release a module is attributed to has to be interpretable, so nothing was normalized.");
        }

        return !requested.IsUnknown && !release.IsCompatibleWith(requested)
            ? (null,
                $"The run declares Oracle Forms {requested.Label} while the source environment profile these extractions were pinned to " +
                $"declares {release.Label}. One of the two is wrong, and generating from a source whose release contradicts the run would make " +
                "every downstream version claim unreliable. Correct the intake value, or prepare against the source environment that matches it.")
            : (release, null);
    }

    /// <summary>
    /// What an admitted extraction is, and what it is not, written into the run's findings so no later
    /// artifact can read it as more than it is.
    /// </summary>
    private static IEnumerable<string> NativeFindings(Inventory inventory)
    {
        if (inventory.Native.Count == 0)
        {
            yield break;
        }

        foreach (NativeModule entry in inventory.Native)
        {
            yield return
                $"Module '{entry.Module.Name}' was read from `{entry.ArtifactPath}`, an extraction the source gateway produced and this server " +
                $"admitted against the content digest of `{entry.ModulePath}`. This fleet did not open that binary and ran no Oracle tool.";
        }

        yield return
            "An admitted extraction establishes that a source worker opened these modules and that the bytes reaching this run are the bytes " +
            "it returned. It is not a behavioural baseline and it is not a schema: nothing here was executed, no Forms runtime was observed, " +
            "and no database object was read, so no baseline or schema evidence follows from it.";
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
        IReadOnlySet<string> covered = inventory.NativelyCovered;

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

            // An admitted extraction is coverage for the exact bytes it was pinned to, which is a stronger
            // statement than a name match in a directory: the server computed that digest over its own copy
            // before the gateway opened anything, so no renamed or substituted file can inherit it.
            if (covered.Contains(file.Path))
            {
                continue;
            }

            if (!s_formModuleExtensions.Contains(file.Extension, StringComparer.OrdinalIgnoreCase))
            {
                // No FormModule XML represents a menu, a library, or an object library, so nothing supplied
                // as XML can be coverage for one. Saying so is the point: it is not an unfound match.
                problems.Add(
                    $"`{file.Path}` is a {file.Category.ToLowerInvariant()}. A Forms XML export carries a FormModule and has no representation " +
                    "for this module type at all, so no supplied export can cover it, no admitted extraction of it can be read as one either, " +
                    "and its contents were never read.");
                continue;
            }

            if (!exported.ContainsKey(LocalIdentity(file.Path, identity)))
            {
                problems.Add(
                    $"`{file.Path}` has no readable FormModule XML export of the same module name in its own directory `{Folder(file.Path)}` " +
                    "and no extraction of it that this server admitted. An export of that name elsewhere in the estate is not coverage for it, " +
                    "because which module a name resolves to is a FORMS_PATH question this fleet cannot answer.");
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
        IReadOnlySet<string> covered = inventory.NativelyCovered;

        List<ManifestFile> files =
        [
            .. inventory.Binaries.Select(file => new ManifestFile(
                file.Path,
                covered.Contains(file.Path)
                    ? $"{file.Category}, opened by the source gateway and admitted against its content digest"
                    : file.Category,
                file.Bytes,
                false,
                null,
                null,
                null,
                [],
                0)),
            .. inventory.LegacyText.Select(file => new ManifestFile(file.Path, file.Category, file.Bytes, false, null, null, null, [], 0)),
            .. inventory.Native.Select(entry => new ManifestFile(
                entry.ArtifactPath,
                $"Forms module (native extraction admitted for `{entry.ModulePath}`)",
                entry.ArtifactBytes,
                true,
                null,
                null,
                null,
                [entry.Module.Name],
                entry.Module.SourceFacts?.Facts.Count ?? 0)),
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
                "Files with readAsText=false were counted by name and size only. No binary Forms module was decoded by this fleet and no Oracle tool was executed here.",
                "A file described as a native extraction was produced by the source gateway and admitted by this server against the content digest of the binary named beside it. The binary itself was still never opened in this process.",
                "An admitted extraction is not a behavioural baseline and not a database schema. It grants no baseline or schema evidence, because nothing was executed and no database object was read.",
                "detectedForms is present only when a supplied export declared a version. effectiveForms is the release this run proceeded on, which may be the operator's intake value or the release recorded on the source environment profile an extraction was pinned to; versionAuthority says which.",
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

    /// <summary>
    /// One module as the representation records it, whichever route made it readable.
    ///
    /// <paramref name="DeclaredVersion"/> is only ever an attribute a supplied export carried. An admitted
    /// extraction declares none, because the native worker read a binary and no version attribute existed
    /// to read: the release such a module is attributed to is the one the document as a whole settled on,
    /// and writing the profile's value here would make an operator's intake value read back as something a
    /// file declared.
    /// </summary>
    private sealed record NormalizedModule(
        FormsModule Module,
        string SourcePath,
        string? DeclaredVersion,
        string DeclaredFamily,
        string Origin);

    private static IReadOnlyList<NormalizedModule> Normalized(Inventory inventory)
    {
        List<NormalizedModule> modules = [];

        foreach (XmlFile file in inventory.FormsXml)
        {
            string? declaredVersion = file.Declaration.AnyDeclaredVersion;
            string declaredFamily = declaredVersion is null
                ? OracleLegacyVersionCatalog.UnknownFamily
                : OracleLegacyVersionCatalog.Forms(declaredVersion).Family;

            modules.AddRange(file.Modules.Select(module =>
                new NormalizedModule(module, file.Path, declaredVersion, declaredFamily, "supplied XML export")));
        }

        modules.AddRange(inventory.Native.Select(entry => new NormalizedModule(
            entry.Module,
            entry.ModulePath,
            null,
            OracleLegacyVersionCatalog.UnknownFamily,
            "admitted native extraction")));

        return modules;
    }

    private static string RenderIntermediate(string sourceRoot, Inventory inventory, Verdict verdict)
    {
        List<IntermediateModule> modules =
        [
            .. Normalized(inventory).Select(entry => new IntermediateModule(
                entry.Module.Name,
                entry.Module.Title,
                entry.SourcePath,
                entry.DeclaredVersion,
                entry.DeclaredFamily,
                [.. entry.Module.Blocks.Select(block => new IntermediateBlock(
                    block.Name,
                    block.BaseTable,
                    block.RecordsDisplayed,
                    [.. block.Items.Select(item => new IntermediateItem(
                        item.Name, item.ItemType, item.DataType, item.ColumnName, item.Prompt, item.Required, item.Visible, item.MaxLength))],
                    [.. block.Triggers.Select(trigger => new IntermediateTrigger(
                        trigger.Name, trigger.Scope, trigger.Body, trigger.BodyEncoding?.ToString()))]))],
                [.. entry.Module.Triggers.Select(trigger => new IntermediateTrigger(
                    trigger.Name, trigger.Scope, trigger.Body, trigger.BodyEncoding?.ToString()))],
                entry.Module.ProgramUnits,
                entry.Module.Lovs,
                Facts(entry.Module.SourceFacts))),
        ];

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
                "Program-unit bodies and LOV queries are absent from the interpreted structure above, which carries their names only. Where the export declared them they are still retained verbatim in sourceFacts, as the declared attributes or elements the file carried: retained means readable, not translated.",
                "Modules that exist only as a binary and were never prepared through the source gateway are absent entirely. Their absence here is not evidence that they carry no behaviour.",
                "sourceFacts retains every element of the export in document order with its source-object path, its parent, and all of its declared attributes, so what the interpreted structure above leaves out stays recoverable. Values are the XML-normalized text the export carried and are not decoded again.",
                "Every source fact is Declared: it is something the export wrote down. None is a default, an inherited value, or a statement about what Oracle Forms would do at runtime, and retaining an element is not a claim that this build understands it.",
                "wrapperDeclaredVersion is the Module wrapper's version attribute verbatim. It is a string the file carried, not a release this fleet adjudicated; formsFamily above is the adjudicated one.",
                "textDigest is SHA-256 of the source text this fleet read for that module: the export text for a supplied XML module, and the admitted extraction artifact for a module the source gateway opened. It is not a snapshot identifier.",
                "A module whose sourcePath names a binary was read from an extraction the source gateway performed and this server admitted against that binary's content digest. Its facts are the extraction's declared structure, not the contents of an Oracle XML export, and it declares no version because no version attribute existed to read.",
                "Nothing here is a behavioural baseline and nothing here is a database schema. No Forms runtime was observed, no statement was executed, and no baseline or schema evidence follows from any module below.",
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

            foreach (NativeModule entry in inventory.Native)
            {
                Line($"| `{entry.ArtifactPath}` | Native extraction admitted for `{entry.ModulePath}` | {entry.ArtifactBytes.ToString(CultureInfo.InvariantCulture)} | yes | {Escape(entry.Claim.ExpectedFormsRelease)} (source environment profile, not declared by the module) |");
            }

            foreach (XmlFile file in inventory.Xml)
            {
                Line($"| `{file.Path}` | {DescribeXml(file)} | {file.Bytes.ToString(CultureInfo.InvariantCulture)} | {(file.Declaration.HasFormModule ? "yes" : "no")} | {Escape(string.Join(", ", file.Declaration.DeclaredVersions) is { Length: > 0 } declared ? declared : "none")} |");
            }

            Line();
            Line("A binary module is counted by name and size and never decoded here. Opening one requires Oracle Forms Builder or the");
            Line("Forms JDAPI, and this fleet bundles neither and executes no Oracle tool. Where a row above names a native extraction, the");
            Line("source gateway opened that module and this server admitted the result against the module's own content digest; a file in");
            Line("this copy that presents itself as an extraction and carries no such record is refused, not read.");
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
        Line("- An admitted native extraction establishes that the source gateway opened a module and that the bytes read here are the bytes");
        Line("  it returned. It is not a behavioural baseline and not a database schema, and no baseline or schema evidence follows from it.");

        return markdown.ToString();
    }

    private static string Escape(string value) => value.Replace("|", "\\|", StringComparison.Ordinal);
}
