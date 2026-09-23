// Copyright (c) Microsoft. All rights reserved.

using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OracleFormsMigrationFleet.Fleet.Execution.Adapters;

/// <summary>
/// Generates the Azure-targeted application tier so a run migrates the application, not only its schema.
///
/// This phase reads no Forms source directly. Whether the estate has readable Forms modules is decided by
/// <see cref="MigrationPhase.SourceNormalization"/>, and the normalized intermediate representation it
/// wrote is the only Forms model this adapter consumes. It used to fall back to re-parsing the original
/// exports when normalization had not run, which meant the estate normalization refused could be read a
/// second time here and generated from anyway; that fallback is gone, and there is no bypass for it.
///
/// Output goes to the operator's session workspace like every other adapter, and the phase produces no
/// attestation: generated code that has never been compiled or run is not evidence of anything.
/// </summary>
public sealed class ApplicationCodeConversionAdapter(IArtifactReviewer? reviewer = null) : IPhaseAdapter
{
    private const int MaxFiles = 20_000;
    private const long MaxTextBytes = 8L * 1024 * 1024;

    private static readonly JsonSerializerOptions s_coverageJson = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public MigrationPhase Phase => MigrationPhase.ApplicationCodeConversion;

    public async Task<PhaseExecutionResult> ExecuteAsync(PhaseExecutionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        // The executor validates the request before planning, but an adapter is also reachable directly.
        // A release nothing interpreted must not reach a generated artifact that cites it.
        if (OracleVersionIntake.Validate(context.Request.OracleFormsVersion, context.Request.OracleDatabaseVersion)
            is { Count: > 0 } versionErrors)
        {
            return PhaseExecutionResult.Failure(
                $"The Oracle release fields on this run were not accepted, so nothing was generated. {string.Join(" ", versionErrors)}");
        }

        string sourceRoot = WorkspacePath.Normalize(context.SourceRoot);
        string outputRoot = WorkspacePath.Normalize(context.OutputRoot);

        if (!context.Workspace.DirectoryExists(sourceRoot))
        {
            return PhaseExecutionResult.Failure($"The source root '{sourceRoot}' does not exist in the workspace. Nothing was generated.");
        }

        bool formsApplies;
        try
        {
            formsApplies = FormsSourcePresence.Applies(context.Request, context.Workspace, sourceRoot);
        }
        catch (WorkspaceLimitExceededException limit)
        {
            return PhaseExecutionResult.Failure(
                $"Nothing was generated: {limit.Message} Whether this run has Forms source at all could not be established from a " +
                "tree that was never fully walked, and generating from the schema while that is unknown would present CRUD over " +
                "the converted tables as a migration of modules nothing opened.");
        }

        bool normalized = context.CompletedPhases.Any(outcome =>
            outcome.Phase == MigrationPhase.SourceNormalization && outcome.State == PhaseExecutionState.Executed);

        if (formsApplies && !normalized)
        {
            return PhaseExecutionResult.Failure(
                "This run has Oracle Forms source, and source normalization did not complete successfully in it. Normalization is the phase " +
                "that decides whether that source is readable at all, so generating here would produce screens from table structure and present " +
                "them as a migration of modules nothing in this run opened. Run source normalization first and resolve what it reports.");
        }

        List<OracleSchema> schemas = [];
        List<FormsModule> forms = [];
        List<string> unopenedModules = [];
        string? intermediateDigest = null;

        if (normalized)
        {
            string irPath = $"{outputRoot}/intermediate/forms-ir.json";

            if (!context.Workspace.FileExists(irPath))
            {
                if (formsApplies)
                {
                    return PhaseExecutionResult.Failure(
                        $"Source normalization reported success but wrote no intermediate representation at '{irPath}', while this run does have " +
                        "Oracle Forms source. Nothing was generated, because the Forms model this phase is required to read does not exist.");
                }

                // Normalization succeeded with no Forms source to normalize. The schema-only path stays open.
                context.Info(
                    "Source normalization produced no Forms intermediate representation, so this run has no readable Forms module. " +
                    "The application tier below comes from database structure alone.");
            }
            else
            {
                string intermediate;
                try
                {
                    intermediate = context.Workspace.ReadText(irPath, FormsIntermediateReader.MaxDocumentBytes);
                    intermediateDigest = context.Workspace.Sha256(irPath, FormsIntermediateReader.MaxDocumentBytes);
                }
                catch (WorkspaceLimitExceededException limit)
                {
                    return PhaseExecutionResult.Failure(
                        $"The normalized Forms representation at '{irPath}' could not be read in full, so nothing was generated: {limit.Message}");
                }

                FormsIntermediateRead read = FormsIntermediateReader.Read(intermediate, sourceRoot);

                if (read.Modules is not { } modules)
                {
                    return PhaseExecutionResult.Failure(
                        $"The normalized Forms representation at '{irPath}' was refused, so the Forms model this phase is required to generate " +
                        $"from is unavailable: {read.Error} Generating from the schema instead would present CRUD over the converted tables as a " +
                        "migration of modules nothing read. Re-run source normalization.");
                }

                forms.AddRange(modules);
                context.Info(
                    $"Read {modules.Count.ToString(CultureInfo.InvariantCulture)} normalized Forms module(s) from {irPath}. " +
                    "The original exports were not re-read.");
            }
        }

        try
        {
            foreach (WorkspaceFile file in context.Workspace.EnumerateFiles(sourceRoot, MaxFiles))
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (FormsSourcePresence.ModuleExtensions.Contains(Path.GetExtension(file.RelativePath), StringComparer.OrdinalIgnoreCase))
                {
                    unopenedModules.Add(file.RelativePath);
                    continue;
                }

                if (!OracleSourceFile.IsSqlText(file.RelativePath))
                {
                    continue;
                }

                try
                {
                    schemas.Add(OracleSchemaParser.Parse(context.Workspace.ReadText(file.RelativePath, MaxTextBytes)));
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    context.Warn($"{file.RelativePath} could not be read and was skipped.");
                }
            }
        }
        catch (WorkspaceLimitExceededException limit)
        {
            return PhaseExecutionResult.Failure(
                $"Nothing was generated: {limit.Message} An application tier emitted from the schema that fitted would omit tables, " +
                "columns, and constraints nobody was told were missing.");
        }

        int formsModules = unopenedModules.Count;

        if (schemas.Count == 0)
        {
            return PhaseExecutionResult.Failure(
                $"No '.sql' files were found under '{sourceRoot}'. The application tier is generated from the schema, " +
                "so there was nothing to generate from.");
        }


        OracleSchema schema = OracleSchema.Merge(schemas);
        bool dotnet = context.Request.Target.BackEnd == BackEndStack.AspNetCore;
        TargetMapping? mapping = null;
        GenerationAuthorization? authorization = null;
        ApplicationConversion conversion;

        if (dotnet)
        {
            string manifestPath = $"{sourceRoot}/{TargetMappingReader.ConventionalPath}";

            if (!context.Workspace.FileExists(manifestPath))
            {
                return PhaseExecutionResult.Failure(
                    $"This run targets ASP.NET Core, which generates from a declared target-mapping manifest, and none was " +
                    $"supplied at '{manifestPath}'. The generator binds roles an operator declared — which table is the " +
                    "master record, which column carries the quantity, which one the stock — and it will not infer any of " +
                    "them from a table or column name.");
            }

            string manifest;
            string manifestDigest;
            try
            {
                manifest = context.Workspace.ReadText(manifestPath, TargetMappingReader.MaxManifestBytes);
                manifestDigest = context.Workspace.Sha256(manifestPath, TargetMappingReader.MaxManifestBytes);
            }
            catch (WorkspaceLimitExceededException limit)
            {
                return PhaseExecutionResult.Failure(
                    $"The target-mapping manifest at '{manifestPath}' could not be read in full, so nothing was generated: {limit.Message}");
            }

            TargetMappingRead read = TargetMappingReader.Read(manifest, schema, forms);

            if (read.Mapping is not { } validated)
            {
                return PhaseExecutionResult.Failure(
                    $"The target-mapping manifest at '{manifestPath}' was refused, so nothing was generated. Every reason " +
                    "is a claim the manifest made that the parsed schema or the normalized source did not confirm; none " +
                    "of them is something this phase may decide on the operator's behalf.",
                    read.Rejections);
            }

            mapping = validated;

            // The mapping says which source objects the generated application claims to carry. Whether
            // anybody decided what to do about those objects is a separate fact, and it lives in the
            // ledger, not in this manifest. A run with Forms source generates only when the server has
            // recorded a disposition for every property the ledger holds, anchored or not.
            if (formsApplies)
            {
                if (intermediateDigest is not { Length: > 0 } observedIntermediate)
                {
                    return PhaseExecutionResult.Failure(
                        "This run has Oracle Forms source, so generating requires the recorded dispositions for that source, and the " +
                        "normalized representation those decisions were made against was not read here. Nothing was generated.");
                }

                if (context.AuthorizationProvider is not { } provider)
                {
                    return PhaseExecutionResult.Failure(
                        "This run has Oracle Forms source, and this phase was given no server authority to generate from it. The " +
                        "decisions about that source live in the server's ledger, and a phase that cannot ask the server which ones " +
                        "were recorded has no way to tell a decided property from an undecided one. Nothing was generated.");
                }

                string[] modulePaths = [.. forms.Select(module => module.SourcePath ?? string.Empty)];

                // Asked here rather than carried in from earlier: a disposition moved to Defer between the
                // operator's review and this moment is a disposition this run does not generate under.
                GenerationAuthorizationDecision decision = await provider.AuthorizeAsync(
                    new GenerationAuthorizationRequest(Phase, observedIntermediate, manifestDigest, modulePaths),
                    cancellationToken).ConfigureAwait(false);

                if (decision.Authorization is not { } issued)
                {
                    return PhaseExecutionResult.Failure(
                        "Nothing was generated: the server did not authorize generating from this run's source at the moment this " +
                        "phase asked. Every reason below is the server's, read at that moment rather than from a decision taken earlier.",
                        decision.Denials);
                }

                if (GenerationCoverage.Reject(issued, validated, modulePaths, observedIntermediate, manifestDigest)
                    is { Count: > 0 } uncovered)
                {
                    return PhaseExecutionResult.Failure(
                        "Nothing was generated: the recorded dispositions do not cover generating this mapping from this source. " +
                        "Every reason below is a property the generator would otherwise have decided for itself, a property recorded " +
                        "as carried forward that nothing here emits, or a binding that changed after the decisions were recorded.",
                        uncovered);
                }

                authorization = issued;
                context.Info(
                    $"Generating under ledger {authorization.LedgerId}: " +
                    $"{authorization.Scope.Count.ToString(CultureInfo.InvariantCulture)} decided source properties — every property " +
                    "the ledger holds, not only the ones this mapping anchors on — over the normalized source and the manifest bytes " +
                    "those decisions were recorded against.");
            }

            context.Info(
                $"Accepted target mapping '{validated.Declaration.FixtureLabel}' for generator " +
                $"{TargetMappingReader.GeneratorId}: {string.Join(", ", validated.Objects.Select(entry => $"{entry.Role} = {entry.Table.Name}"))}.");
            conversion = DotNetApplicationEmitter.Convert(
                schema, validated, context.Request.ApplicationName, context.Request.Target.Database);
        }
        else
        {
            conversion = ApplicationCodeEmitter.Convert(
                schema, context.Request.ApplicationName, context.Request.Target.Database, forms);
        }

        bool recognized = !dotnet && NorthstarBankingApplicationProfile.Matches(schema);

        if (conversion.Files.Count == 0)
        {
            return PhaseExecutionResult.Failure(
                conversion.Findings.FirstOrDefault(finding => finding.Severity == ConversionSeverity.Unsupported)?.Reason
                ?? conversion.Findings.FirstOrDefault()?.Reason
                ?? "No application code could be generated from the supplied schema.",
                [.. conversion.Findings.Select(finding => $"{finding.Severity}: {finding.Category} — {finding.Construct}: {finding.Reason}")]);
        }

        string appRoot = $"{outputRoot}/application";
        HashSet<string> emittedPaths = new(StringComparer.Ordinal);

        foreach (GeneratedFile file in conversion.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!emittedPaths.Add(file.Path))
            {
                return PhaseExecutionResult.Failure(
                    $"The generator produced '{file.Path}' twice. One of the two would have overwritten the other, and the tier on " +
                    "disk would hold content no record of this generation describes.");
            }

            context.Workspace.WriteText($"{appRoot}/{file.Path}", file.Contents);
        }

        if (dotnet)
        {
            context.Info(
                $"Generated {conversion.Files.Count.ToString(CultureInfo.InvariantCulture)} files: an ASP.NET Core back end " +
                "on .NET 10, a React and TypeScript screen, the PostgreSQL tables and routine they run against, an " +
                "executable acceptance suite, and container packaging.");
            context.Info(
                "Nothing in this tier was chosen from an application, table, or column name. Every rule follows a role " +
                "the manifest declared and the parsed schema confirmed, so an estate with different identifiers takes " +
                "the same path through the generator.");
            context.Warn(
                "None of it has been compiled, started, or executed here. Generated code that has never been built is " +
                "not working software, and this phase deliberately produces no attestation.");
        }
        else
        {
            context.Info(
                $"Generated {conversion.Files.Count.ToString(CultureInfo.InvariantCulture)} files: " +
                $"{schema.Tables.Count.ToString(CultureInfo.InvariantCulture)} entities with repositories, " +
                (recognized
                    ? "the generated workflow routes, and a browser client."
                    : "REST endpoints, and a React client."));
        }

        if (recognized)
        {
            context.Info(
                "The schema carries the complete retail banking workflow, so a working replacement was generated " +
                $"rather than CRUD screens: {string.Join(", ", NorthstarBankingApplicationProfile.Routes)}.");
            context.Info(
                "No per-table CRUD controller was emitted: it would expose every column of every table, the " +
                "migrated password hashes included, with no session or role check.");
            context.Info(
                "The generated browser client carries the source application's modules: " +
                $"{string.Join(", ", NorthstarBankingApplicationProfile.Modules)}.");
        }

        if (formsModules > 0)
        {
            // Stated as a limit rather than silently producing screens that look authoritative.
            context.Warn(
                $"{formsModules.ToString(CultureInfo.InvariantCulture)} Forms modules were found and could not be read. " +
                (dotnet
                    ? "No behaviour was taken from them: the generated screens and rules follow the roles the mapping manifest declared."
                    : recognized
                        ? "No behaviour was taken from them: the generated workflows come from the schema and this fleet's template."
                        : "The generated screens come from table structure, not from those modules."));
        }

        string reportPath = $"{appRoot}/CONVERSION_NOTES.md";
        emittedPaths.Add("CONVERSION_NOTES.md");
        context.Workspace.WriteText(reportPath, mapping is { } accepted
            ? RenderDotNetNotes(context.Request.ApplicationName, conversion, accepted, formsModules, forms.Count)
            : RenderNotes(context.Request.ApplicationName, conversion, schema, formsModules, forms.Count, recognized));

        List<ArtifactReference> artifacts = dotnet
            ?
            [
                new($"{appRoot}/backend", ArtifactKind.BackEndCode,
                    "ASP.NET Core on .NET 10 over Npgsql, plus an executable acceptance suite that drives it over HTTP."),
                new($"{appRoot}/frontend", ArtifactKind.FrontEndCode,
                    "React and TypeScript master/detail screen with both lookups. Totals are read back from the server."),
                new($"{appRoot}/database", ArtifactKind.DatabaseSchema,
                    "PostgreSQL tables for every mapped object and the routine that performs one submission atomically."),
                new($"{appRoot}/mapping-manifest.json", ArtifactKind.ValidationReport,
                    "Resolved mapping: role bindings, evidence anchors, target types, transform decisions, and a binding digest."),
                new($"{appRoot}/backend/Api.Tests", ArtifactKind.TestSuite,
                    "Generated acceptance tests. They have not been executed here and attest to nothing."),
                new(reportPath, ArtifactKind.ValidationReport,
                    "What was generated, what the mapping declared, and what is still unverified."),
            ]
            :
            [
                new($"{appRoot}/backend", ArtifactKind.BackEndCode, recognized
                    ? "Spring Boot entities and repositories over the converted schema, plus the generated banking workflow service. No per-table CRUD controller was generated."
                    : "Spring Boot entities, repositories, and REST endpoints over the converted schema."),
                new($"{appRoot}/frontend", ArtifactKind.FrontEndCode, recognized
                    ? "Browser client reproducing the source application's modules against the generated workflow service."
                    : "React client and screen generated from the converted schema."),
                new(reportPath, ArtifactKind.ValidationReport, "What was generated, and the behaviour that still has to be rebuilt by hand."),
            ];

        List<string> findings =
            [.. conversion.Findings.Select(finding => $"{finding.Severity}: {finding.Category} — {finding.Construct}: {finding.Reason}")];

        if (reviewer is not null)
        {
            string reviewPath = $"{appRoot}/model-review.md";
            emittedPaths.Add("model-review.md");
            context.Info("Reviewing the generated application with the review model.");

            try
            {
                IReadOnlyList<AdvisoryFinding> advisories = await reviewer.ReviewAsync(
                    new ArtifactReviewRequest(
                        context.Request.ApplicationName,
                        context.Request.Target.Database,
                        string.Join("\n\n", conversion.Files.Take(6).Select(file => $"-- {file.Path}\n{file.Contents}")),
                        findings)
                    {
                        Kind = ArtifactReviewKind.ApplicationCode,
                        OracleFormsVersion = context.Request.OracleFormsVersion,
                        OracleDatabaseVersion = context.Request.OracleDatabaseVersion,
                    },
                    cancellationToken).ConfigureAwait(false);

                context.Workspace.WriteText(reviewPath, ArtifactReviewReport.Render(
                    context.Request.ApplicationName,
                    context.Request.Target.Database,
                    ArtifactReviewKind.ApplicationCode,
                    advisories));

                findings.AddRange(advisories.Select(advisory => $"Advisory ({advisory.Severity}): {advisory.Construct} — {advisory.Reason}"));
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                context.Warn($"The model review did not complete ({FailureText.Describe(exception)}). The generated code is unaffected.");
                context.Workspace.WriteText(reviewPath, ArtifactReviewReport.RenderFailure(
                    context.Request.ApplicationName,
                    context.Request.Target.Database,
                    ArtifactReviewKind.ApplicationCode,
                    exception.Message));
            }

            artifacts.Add(new ArtifactReference(reviewPath, ArtifactKind.ValidationReport, "Unverified model review of the generated application. Advisory only."));
        }

        // Written last, and only once nothing else will be added to the tier, so the set it describes is
        // the set a reader finds rather than the set this phase intended at the halfway point.
        if (authorization is not null)
        {
            if (WriteCoverage(context, outputRoot, appRoot, authorization, emittedPaths) is { } refusal)
            {
                return refusal;
            }

            artifacts.Add(new ArtifactReference($"{outputRoot}/{GenerationCoverage.RecordPath}", ArtifactKind.ValidationReport,
                "Which recorded dispositions covered this generation, and the digest of the files it emitted. It records " +
                "nothing as verified: no generated file here has been compiled, started, or executed."));

            context.Info(
                $"Recorded which decisions this generation was covered by at '{outputRoot}/{GenerationCoverage.RecordPath}'. It carries " +
                "the digest of the emitted files, not a claim about them, so the server records generation against bytes it hashes itself.");
        }

        return PhaseExecutionResult.Success(artifacts, findings);
    }

    /// <summary>
    /// Writes the coverage record over the tier as it stands, or returns the reason the tier could not be
    /// described.
    ///
    /// The file list is enumerated off disk rather than taken from what the emitters returned, and it is
    /// then checked against what this phase wrote. A file present that nothing wrote is as much a reason
    /// to stop as a file missing that something did: either way the record would describe an output set
    /// the workspace does not hold. The record itself is excluded from the set it describes, because a
    /// document cannot contain the digest of its own bytes.
    /// </summary>
    private static PhaseExecutionResult? WriteCoverage(
        PhaseExecutionContext context,
        string outputRoot,
        string appRoot,
        GenerationAuthorization authorization,
        HashSet<string> expected)
    {
        string coveragePath = $"{outputRoot}/{GenerationCoverage.RecordPath}";

        if (!WorkspacePath.IsWithin(appRoot, coveragePath))
        {
            return PhaseExecutionResult.Failure(
                $"The coverage record would be written to '{coveragePath}', outside the generated tier at '{appRoot}' it describes. " +
                "A record kept beside output it does not enclose is a record of some other output.");
        }

        IReadOnlyList<WorkspaceFile> present;
        try
        {
            present = context.Workspace.EnumerateFiles(appRoot, GenerationCoverage.MaxOutputFiles + 1);
        }
        catch (WorkspaceLimitExceededException limit)
        {
            return PhaseExecutionResult.Failure(
                $"The generated tier could not be listed in full, so no record of what it holds was written: {limit.Message}");
        }

        List<string> observed = [];
        HashSet<string> distinct = new(StringComparer.Ordinal);

        foreach (WorkspaceFile file in present)
        {
            if (string.Equals(file.RelativePath, coveragePath, StringComparison.Ordinal))
            {
                continue;
            }

            if (!WorkspacePath.IsWithin(appRoot, file.RelativePath))
            {
                return PhaseExecutionResult.Failure(
                    $"'{file.RelativePath}' was listed under the generated tier but does not resolve inside it, so the set of files " +
                    "this generation produced could not be established.");
            }

            string relative = file.RelativePath[(appRoot.Length + 1)..];
            if (!distinct.Add(relative))
            {
                return PhaseExecutionResult.Failure(
                    $"'{relative}' appears twice in the generated tier, so which of the two the record would describe is ambiguous.");
            }

            observed.Add(relative);
        }

        if (observed.Count > GenerationCoverage.MaxOutputFiles)
        {
            return PhaseExecutionResult.Failure(
                $"The generated tier holds {observed.Count.ToString(CultureInfo.InvariantCulture)} files, beyond the " +
                $"{GenerationCoverage.MaxOutputFiles.ToString(CultureInfo.InvariantCulture)} one coverage record describes. A record " +
                "listing only some of them would offer a partial reading of the output as evidence of the whole generation.");
        }

        string[] missing = [.. expected.Except(distinct, StringComparer.Ordinal).Order(StringComparer.Ordinal)];
        string[] unexpected = [.. distinct.Except(expected, StringComparer.Ordinal).Order(StringComparer.Ordinal)];

        if (missing.Length > 0 || unexpected.Length > 0)
        {
            return PhaseExecutionResult.Failure(
                "The generated tier on disk is not the tier this phase wrote, so nothing was recorded about it.",
                [
                    .. missing.Select(path => $"Written by this phase and not present afterwards: 'application/{path}'."),
                    .. unexpected.Select(path => $"Present in the tier and written by nothing in this phase: 'application/{path}'."),
                ]);
        }

        // Hashed back off disk rather than from the strings just emitted, so the digest describes the
        // bytes a reader would find and not the bytes this phase intended to write.
        string[] outputFiles = [.. observed.Select(path => $"application/{path}").Order(StringComparer.Ordinal)];
        List<(string Path, string ContentSha256)> hashed = [];

        try
        {
            foreach (string path in outputFiles)
            {
                hashed.Add((path, context.Workspace.Sha256($"{outputRoot}/{path}", MaxTextBytes)));
            }
        }
        catch (WorkspaceLimitExceededException limit)
        {
            return PhaseExecutionResult.Failure(
                $"A generated file could not be digested in full, so no record of this generation was written: {limit.Message}");
        }

        GenerationCoverageRecord record = new(
            authorization.LedgerId,
            authorization.TenantId,
            authorization.ProjectId,
            authorization.RunId,
            authorization.SourceSnapshotHash,
            authorization.IntermediateContentSha256,
            authorization.MappingManifestSha256,
            TargetMappingReader.GeneratorId,
            outputRoot,
            GenerationCoverage.OutputSetDigest(hashed),
            authorization.ScopeDigest,
            DateTimeOffset.UtcNow,
            outputFiles,
            [
                .. authorization.Scope
                    .OrderBy(entry => entry.EntryId, StringComparer.Ordinal)
                    .Select(entry => new GenerationCoveredEntry(
                        entry.EntryId, entry.DecisionRevision, entry.Decision, entry.MappingRuleId, entry.MappingRuleVersion)),
            ]);

        context.Workspace.WriteText(coveragePath, JsonSerializer.Serialize(record, s_coverageJson));

        // Read back and reparsed: the server will read this document, not this object, and a record that
        // does not survive its own round trip is not evidence of anything.
        GenerationCoverageRecord? reparsed;
        try
        {
            reparsed = JsonSerializer.Deserialize<GenerationCoverageRecord>(
                context.Workspace.ReadText(coveragePath, MaxTextBytes), s_coverageJson);
        }
        catch (Exception exception) when (exception is JsonException or WorkspaceLimitExceededException)
        {
            return PhaseExecutionResult.Failure(
                $"The record of this generation could not be read back after it was written: {exception.Message}");
        }

        return reparsed is null ||
            !string.Equals(reparsed.OutputSetSha256, record.OutputSetSha256, StringComparison.Ordinal) ||
            !string.Equals(reparsed.ScopeDigest, record.ScopeDigest, StringComparison.Ordinal) ||
            !reparsed.OutputFiles.SequenceEqual(record.OutputFiles, StringComparer.Ordinal) ||
            reparsed.CoveredEntries.Count != record.CoveredEntries.Count
                ? PhaseExecutionResult.Failure(
                    "The record of this generation did not read back as what was written, so it is not a description of this output.")
                : null;
    }

    private static string RenderDotNetNotes(
        string applicationName,
        ApplicationConversion conversion,
        TargetMapping mapping,
        int formsModules,
        int formsRead)
    {
        StringBuilder builder = new();
        builder.AppendLine("# Application conversion notes").AppendLine();
        builder.Append("Application: ").AppendLine(applicationName);
        builder.Append("Target stack: React and TypeScript, ASP.NET Core on .NET 10, Azure Database for PostgreSQL.");
        builder.AppendLine().AppendLine();
        builder.Append("Generated by `").Append(DotNetApplicationEmitter.GeneratorVersion).Append("` from mapping manifest `");
        builder.Append(mapping.Declaration.SchemaVersion).Append("`, fixture label `");
        builder.Append(mapping.Declaration.FixtureLabel).AppendLine("`.").AppendLine();
        builder.AppendLine("Oracle is not in the generated data path and no database password exists anywhere in the output.");
        builder.AppendLine();

        builder.AppendLine("## Roles the manifest declared, and what confirmed them").AppendLine();
        builder.AppendLine("Nothing below was inferred from an application, table, or column name. The manifest bound each");
        builder.AppendLine("role and the parsed schema had to confirm the type, scale, nullability, key, and foreign key that");
        builder.AppendLine("role requires, or the run would have stopped here.").AppendLine();
        builder.AppendLine("| Object role | Source table | Field roles |");
        builder.AppendLine("| --- | --- | --- |");

        foreach (ResolvedObject entry in mapping.Objects)
        {
            builder.Append("| ").Append(entry.Role).Append(" | `").Append(entry.Table.Name).Append("` | ");
            builder.Append(string.Join(", ", entry.Fields.Select(field => $"{field.Role} = `{field.Column.Name}`")));
            builder.AppendLine(" |");
        }

        builder.AppendLine().AppendLine("### Evidence anchors").AppendLine();
        foreach (ResolvedObject entry in mapping.Objects)
        {
            foreach (MappedSourceReference source in entry.Sources)
            {
                builder.Append("- ").Append(entry.Role).Append(" cites `").Append(source.Id).Append("` (")
                       .Append(source.Kind).Append("): `").Append(source.Path).Append('`');
                builder.AppendLine(source.Module is { Length: > 0 } module ? $" in `{module}`." : ".");
            }
        }

        builder.AppendLine().AppendLine("## Transform decisions").AppendLine();
        builder.AppendLine("These are choices the source did not state. They are recorded here and, machine-readably, in");
        builder.AppendLine("`mapping-manifest.json` with a content digest a run ledger can bind to.").AppendLine();

        foreach (DotNetTransformDecision decision in DotNetApplicationEmitter.TransformDecisions(mapping))
        {
            builder.Append("- **").Append(decision.Id).Append("** — ").AppendLine(decision.Decision);
            builder.Append("  ").AppendLine(decision.Rationale);
        }

        builder.AppendLine().AppendLine("## Generated").AppendLine();
        builder.AppendLine("| File | Purpose |");
        builder.AppendLine("| --- | --- |");
        foreach (GeneratedFile file in conversion.Files)
        {
            builder.Append("| `").Append(file.Path).Append("` | ").Append(file.Description).AppendLine(" |");
        }

        builder.AppendLine().AppendLine("## Not generated, and why").AppendLine();

        if (formsRead > 0)
        {
            builder.Append("- **").Append(formsRead.ToString(CultureInfo.InvariantCulture));
            builder.AppendLine(" Forms module(s) were read from an XML export**, and the manifest could cite paths inside");
            builder.AppendLine("  them. No trigger body, canvas geometry, or navigation rule was translated into the generated");
            builder.AppendLine("  screens; anything the source did outside the declared roles is still outstanding work.");
        }

        if (formsModules > 0)
        {
            builder.Append("- **").Append(formsModules.ToString(CultureInfo.InvariantCulture));
            builder.AppendLine(" Oracle Forms modules were not converted.** Their contents are a proprietary binary that");
            builder.AppendLine("  requires Forms Builder or the Forms JDAPI, so nothing was read from them.");
        }

        builder.AppendLine("- **No behaviour outside the declared roles was generated.** The generator implements one");
        builder.AppendLine("  master/detail workflow with two lookups. A rule the manifest did not bind a role for is absent,");
        builder.AppendLine("  not defaulted.");

        foreach (ConversionFinding finding in conversion.Findings.Where(finding => finding.Severity != ConversionSeverity.Note))
        {
            builder.Append("- **").Append(finding.Construct).Append("** — ").AppendLine(finding.Reason);
        }

        builder.AppendLine().AppendLine("## Before this replaces anything").AppendLine();
        builder.AppendLine("1. Build it. `BUILD.md` carries the exact commands and the pinned dependency story.");
        builder.AppendLine("2. Run the generated acceptance suite against a real PostgreSQL instance. Without a configured");
        builder.AppendLine("   target it executes nothing and reports nothing; treat that as unverified, not as a pass.");
        builder.AppendLine("3. Apply `database/schema.sql` and `database/routines.sql` to the target. DDL that has never");
        builder.AppendLine("   executed is not a migrated schema.");
        builder.AppendLine("4. Reconcile the migrated data against the source. Row counts are not a reconciliation.");
        builder.AppendLine();
        builder.AppendLine("Passing the generated tests says this application behaves as the mapping specified. It does not say");
        builder.AppendLine("it behaves as the Oracle original did: nothing here was compared against a running source system.");

        return builder.ToString();
    }

    private static string RenderNotes(
        string applicationName,
        ApplicationConversion conversion,
        OracleSchema schema,
        int formsModules,
        int formsRead,
        bool recognized)
    {
        StringBuilder builder = new();
        builder.AppendLine("# Application conversion notes").AppendLine();
        builder.Append("Application: ").AppendLine(applicationName);
        builder.AppendLine();
        builder.AppendLine("The generated back end talks to Azure Database for PostgreSQL with Entra authentication. Oracle is");
        builder.AppendLine("not in its data path and no database password exists anywhere in the output.").AppendLine();

        if (recognized)
        {
            builder.AppendLine("## Recognised workflow").AppendLine();
            builder.AppendLine("The schema declares every table and column the retail banking workflows read and write, so a");
            builder.AppendLine("working replacement for them was generated instead of CRUD screens. Recognition is structural:");
            builder.AppendLine("the application name was never consulted. These routes are implemented over PostgreSQL:").AppendLine();

            foreach (string route in NorthstarBankingApplicationProfile.Routes)
            {
                builder.Append("- `").Append(route).AppendLine("`");
            }

            builder.AppendLine();
            builder.Append("The browser client carries these modules: ");
            builder.Append(string.Join(", ", NorthstarBankingApplicationProfile.Modules)).AppendLine(".");
            builder.AppendLine("Nothing in either tier was recovered from a Forms module.").AppendLine();
            builder.AppendLine("These routes are the whole HTTP surface. No per-table CRUD controller was generated, because");
            builder.AppendLine("one would publish every column of every table — the migrated password hashes included — and");
            builder.AppendLine("accept unvalidated writes with no session or role check. Any other path under `/api` answers a");
            builder.AppendLine("JSON 404.").AppendLine();
        }

        builder.AppendLine("## Generated").AppendLine();
        builder.AppendLine("| File | Purpose |");
        builder.AppendLine("| --- | --- |");
        foreach (GeneratedFile file in conversion.Files)
        {
            builder.Append("| `").Append(file.Path).Append("` | ").Append(file.Description).AppendLine(" |");
        }

        builder.AppendLine().AppendLine("## Not generated, and why").AppendLine();

        if (recognized)
        {
            // The profile's screens are template-driven. Saying anything came from a Forms export would be false.
            builder.AppendLine("- **No behaviour came from a Forms module.** The workflows above were recognised from the");
            builder.AppendLine("  schema and generated from this fleet's own template. Any screen or rule outside the listed");
            builder.AppendLine("  modules and routes still has to be rebuilt against the real application.");

            if (formsModules > 0)
            {
                builder.Append("- **").Append(formsModules.ToString(CultureInfo.InvariantCulture));
                builder.AppendLine(" Oracle Forms modules were not converted.** Their contents are a proprietary binary");
                builder.AppendLine("  that requires Forms Builder or the Forms JDAPI, so nothing was read from them.");
            }
        }
        else if (formsRead > 0)
        {
            builder.Append("- **").Append(formsRead.ToString(CultureInfo.InvariantCulture));
            builder.AppendLine(" Forms module(s) were read from an XML export.** Blocks, item order, prompts, and required");
            builder.AppendLine("  flags came from the form. Canvas geometry, window navigation, and trigger behaviour did not.");
        }
        else if (formsModules > 0)
        {
            builder.Append("- **").Append(formsModules.ToString(CultureInfo.InvariantCulture));
            builder.AppendLine(" Oracle Forms modules were not converted.** Their contents are a proprietary binary that");
            builder.AppendLine("  requires Forms Builder or the Forms JDAPI, so no trigger, block, or navigation rule was read.");
            builder.AppendLine("  The screens here are CRUD over the converted tables and do not reproduce the original UI.");
            builder.AppendLine("  Export them with frmf2xml and supply the XML to have the screens follow the real form.");
        }
        else
        {
            builder.AppendLine("- **No Forms modules were present in the source.** The screens are generated from table structure.");
        }

        foreach (ConversionFinding finding in conversion.Findings.Where(finding => finding.Severity == ConversionSeverity.Unsupported))
        {
            builder.Append("- **").Append(finding.Construct).Append("** — ").AppendLine(finding.Reason);
        }

        builder.AppendLine().AppendLine("## Before this replaces anything").AppendLine();
        builder.AppendLine("1. Compile and run it. Generated code that has never been built is not working software, and this");
        builder.AppendLine("   phase deliberately produces no attestation for that reason.");

        if (recognized)
        {
            builder.AppendLine("2. Re-enrol credentials. The migrated password hashes are unsalted SHA-256, because that is what");
            builder.AppendLine("   the source stored, and the generated service can compare them but not strengthen them.");
            builder.AppendLine("3. Check the routes against the real application. No per-table CRUD controller was generated, so");
            builder.AppendLine("   anything the source did outside the listed routes is not served by anything yet.");
        }
        else
        {
            builder.AppendLine("2. Add authentication and authorization. Every endpoint is currently open.");
            builder.AppendLine("3. Test the translated PL/pgSQL against the original behaviour, then decide for each rule");
            builder.AppendLine("   whether it stays in the database or moves into this tier. Nothing here calls it yet.");
        }

        builder.AppendLine("4. Reconcile the migrated data against the source. Row counts are not a reconciliation.");

        if (schema.Unparsed.Count > 0)
        {
            builder.AppendLine().Append("Statements the parser did not interpret: ")
                   .Append(schema.Unparsed.Count.ToString(CultureInfo.InvariantCulture)).AppendLine(".");
        }

        return builder.ToString();
    }
}
