// Copyright (c) Microsoft. All rights reserved.

using System.Globalization;
using System.Text;
using OracleFormsMigrationFleet.Fleet.Agents;

namespace OracleFormsMigrationFleet.Fleet.Execution.Adapters;

/// <summary>
/// Converts the Oracle DDL found in the workspace source tree to the run's database target and writes the
/// DatabaseConversion artifacts the run blueprint declares.
///
/// Only <see cref="DatabaseTarget.PostgreSql"/> is implemented. Any other target fails the phase rather
/// than emitting DDL for the wrong engine. PL/SQL program units are never translated; they are reported
/// in the conversion report as manual PL/pgSQL rewrites.
///
/// An optional <see cref="IArtifactReviewer"/> reads the emitted DDL afterwards and adds advisory findings
/// to a separate artifact. That review cannot change the conversion, its report, or the phase outcome.
/// </summary>
public sealed class DatabaseConversionAdapter(
    IArtifactReviewer? reviewer = null,
    CritiqueRepairOrchestrator? orchestrator = null) : IPhaseAdapter
{
    private const int MaxFiles = 20_000;
    private const long MaxTextBytes = 8L * 1024 * 1024;

    public MigrationPhase Phase => MigrationPhase.DatabaseConversion;

    public async Task<PhaseExecutionResult> ExecuteAsync(PhaseExecutionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        DatabaseTarget target = context.Request.Target.Database;
        if (target != DatabaseTarget.PostgreSql)
        {
            return PhaseExecutionResult.Failure(
                $"No database conversion adapter is implemented for {target}. Emitting PostgreSQL DDL for a " +
                "different engine would produce invalid schema, so nothing was written. Use SSMA for the SQL Server family.");
        }

        string sourceRoot = WorkspacePath.Normalize(context.SourceRoot);
        string outputRoot = WorkspacePath.Normalize(context.OutputRoot);

        OracleVersionAssessment source = OracleLegacyVersionCatalog.Database(context.Request.OracleDatabaseVersion);
        if (source.Readiness == OracleConversionReadiness.Rejected)
        {
            // Converting anyway would attach a release label to the output that means nothing.
            return PhaseExecutionResult.Failure(
                $"The run declares Oracle Database version '{context.Request.OracleDatabaseVersion}'. {source.Disposition} " +
                "Nothing was converted and nothing was written. Supply a recognized release, or 'unknown' if it has not been established.",
                source.Warnings);
        }

        if (source.IsUnknown)
        {
            context.Warn(
                "No Oracle Database release was supplied. Conversion runs from the supplied SQL text, and the report records that " +
                "the release that produced that text was never established.");
        }

        if (!context.Workspace.DirectoryExists(sourceRoot))
        {
            return PhaseExecutionResult.Failure(
                $"The source root '{sourceRoot}' does not exist in the workspace. Nothing was converted.");
        }

        List<string> sources = [];
        List<OracleSchema> schemas = [];
        List<(string Path, OracleSchema Schema)> parsedScripts = [];
        List<BehaviourScenario> scenarios = [];

        try
        {
            foreach (WorkspaceFile file in context.Workspace.EnumerateFiles(sourceRoot, MaxFiles))
            {
                cancellationToken.ThrowIfCancellationRequested();

                string extension = Path.GetExtension(file.RelativePath);
                if (extension.Equals(".yaml", StringComparison.OrdinalIgnoreCase)
                    || extension.Equals(".yml", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        if (ScenarioReader.Read(context.Workspace.ReadText(file.RelativePath, MaxTextBytes)) is { } scenario)
                        {
                            scenarios.Add(scenario);
                        }
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                    {
                        context.Warn($"{file.RelativePath} could not be read and was skipped.");
                    }

                    continue;
                }

                if (!OracleSourceFile.IsSqlText(file.RelativePath))
                {
                    continue;
                }

                try
                {
                    string text = context.Workspace.ReadText(file.RelativePath, MaxTextBytes);
                    sources.Add(text);

                    OracleSchema parsed = OracleSchemaParser.Parse(text);
                    schemas.Add(parsed);
                    parsedScripts.Add((file.RelativePath, parsed));
                    context.Info($"Parsed {file.RelativePath}.");
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    context.Warn($"{file.RelativePath} could not be read and was skipped.");
                }
            }
        }
        catch (WorkspaceLimitExceededException limit)
        {
            // DDL emitted from the part that fitted would be a schema missing tables nobody was told about.
            return PhaseExecutionResult.Failure(
                $"Nothing was converted and nothing was written: {limit.Message}");
        }

        if (schemas.Count == 0)
        {
            return PhaseExecutionResult.Failure(
                $"No '.sql' files were found under '{sourceRoot}', so there was no schema to convert.");
        }

        OracleSchema schema = OracleSchema.Merge(schemas);
        if (schema.Tables.Count == 0 && schema.Sequences.Count == 0)
        {
            return PhaseExecutionResult.Failure(
                "The supplied SQL contained no CREATE TABLE or CREATE SEQUENCE statement, so no PostgreSQL DDL was emitted.");
        }

        PostgreSqlConversion conversion = PostgreSqlEmitter.Convert(schema, string.Join('\n', sources));

        IReadOnlyList<ScriptAccounting> accounting =
            [.. parsedScripts.Select(script => new ScriptAccounting(script.Path, OracleStatementAccounting.Account(script.Schema)))];

        // A statement nothing could classify is the same loss as one that was classified and not emitted:
        // in both cases the export declared something the target does not have, and neither may pass as a
        // finding on an otherwise clean report.
        IReadOnlyList<(string Path, OracleStatementAccount Account)> omitted =
        [
            .. accounting.SelectMany(script => script.Accounts
                .Where(account => account.Disposition
                    is OracleStatementDisposition.OmittedSchemaBearing or OracleStatementDisposition.Unclassified)
                .Select(account => (script.Path, Account: account))),
        ];

        string ddlPath = $"{outputRoot}/database/postgresql/schema/schema.sql";
        string reportPath = $"{outputRoot}/database/postgresql/conversion-report.md";

        context.Workspace.WriteText(ddlPath, conversion.Ddl);
        context.Workspace.WriteText(
            reportPath,
            PostgreSqlEmitter.RenderReport(conversion.Report, context.Request.ApplicationName)
            + RenderSourceRelease(source, sources.Count)
            + RenderStatementAccounting(accounting));

        int unsupported = conversion.Report.Findings.Count(finding => finding.Severity == ConversionSeverity.Unsupported);
        int review = conversion.Report.Findings.Count(finding => finding.Severity == ConversionSeverity.ManualReview);

        context.Info(
            $"Converted {conversion.Report.Tables.ToString(CultureInfo.InvariantCulture)} tables and " +
            $"{conversion.Report.Sequences.ToString(CultureInfo.InvariantCulture)} sequences to PostgreSQL.");

        if (unsupported > 0)
        {
            context.Warn($"{unsupported.ToString(CultureInfo.InvariantCulture)} constructs were not converted and need a manual rewrite.");
        }

        if (review > 0)
        {
            context.Warn($"{review.ToString(CultureInfo.InvariantCulture)} converted constructs need human review before use.");
        }

        List<ArtifactReference> artifacts =
        [
            Declared(context, ddlPath, ArtifactKind.DatabaseSchema, "Converted PostgreSql schema and programmable objects."),
            Declared(context, reportPath, ArtifactKind.ValidationReport, "Type mappings, unsupported constructs, and manual remediation list."),
        ];

        List<string> accountingFindings =
        [
            .. accounting.SelectMany(script => script.Accounts
                .Where(account => account.Disposition
                    is not (OracleStatementDisposition.ClassifiedProgramUnit or OracleStatementDisposition.Comment))
                .Select(account =>
                    $"{(account.Disposition == OracleStatementDisposition.OmittedSchemaBearing || account.Disposition == OracleStatementDisposition.Unclassified ? ConversionSeverity.Unsupported : ConversionSeverity.ManualReview)}: " +
                    $"Unparsed statement ({account.Disposition}) — {script.Path}: {account.Kind}: {account.Snippet}")),
        ];

        if (omitted.Count > 0)
        {
            // The DDL and the report stay on disk: they are the evidence for the refusal. What must not
            // happen is the run reporting a clean conversion while an object the export declared is absent
            // from the target and absent from the report.
            string detail =
                $"{omitted.Count.ToString(CultureInfo.InvariantCulture)} statement(s) in the supplied SQL either create or alter a schema object that " +
                "this conversion does not emit, or could not be classified at all, so the converted schema is incomplete and nothing may treat it as a " +
                "faithful copy: " +
                string.Join("; ", omitted.Take(10).Select(entry => $"{entry.Path}: {entry.Account.Disposition}: {entry.Account.Kind}")) +
                (omitted.Count > 10 ? "; ..." : string.Empty) + ". " +
                "The emitted DDL and the conversion report were kept as the evidence for this refusal. Remove the objects from the export, " +
                "or extend the converter to emit them, and re-run.";

            context.Warn(detail);
            return new PhaseExecutionResult(
                false,
                artifacts,
                [
                    .. conversion.Report.Findings.Select(finding => $"{finding.Severity}: {finding.Category} — {finding.Construct}: {finding.Reason}"),
                    .. accountingFindings,
                ],
                detail);
        }

        if (scenarios.Count > 0)
        {
            IReadOnlyList<ScenarioCoverage> coverage = ScenarioReader.Cover(scenarios, conversion.Report.Findings);
            string coveragePath = $"{outputRoot}/database/postgresql/behaviour-coverage.md";
            context.Workspace.WriteText(coveragePath, ScenarioReader.Render(context.Request.ApplicationName, coverage));

            int blocked = coverage.Count(entry => entry.BlockedBy.Count > 0);
            context.Info(
                $"Read {scenarios.Count.ToString(CultureInfo.InvariantCulture)} documented scenarios; " +
                $"{blocked.ToString(CultureInfo.InvariantCulture)} depend on logic that did not migrate.");

            foreach (ScenarioCoverage entry in coverage.Where(entry => entry.BlockedBy.Count > 0))
            {
                context.Warn(
                    $"{entry.Scenario.Name}: blocked by {entry.BlockedBy.Count.ToString(CultureInfo.InvariantCulture)} " +
                    $"untranslated program units in {entry.Scenario.LegacyPackage}.");
            }

            artifacts.Add(Declared(
                context,
                coveragePath,
                ArtifactKind.ValidationReport,
                "Documented behaviours matched against the program units this conversion refused. Nothing was executed."));
        }

        List<string> findings =
            [.. conversion.Report.Findings.Select(finding => $"{finding.Severity}: {finding.Category} — {finding.Construct}: {finding.Reason}"), .. accountingFindings];

        if (reviewer is not null)
        {
            string reviewPath = $"{outputRoot}/database/postgresql/model-review.md";
            context.Info("Reviewing the generated DDL with the review model.");

            IReadOnlyList<AdvisoryFinding> advisories;
            try
            {
                advisories = await reviewer.ReviewAsync(
                    new ArtifactReviewRequest(context.Request.ApplicationName, target, conversion.Ddl, findings)
                    {
                        OracleDatabaseVersion = context.Request.OracleDatabaseVersion,
                    },
                    cancellationToken).ConfigureAwait(false);

                context.Workspace.WriteText(reviewPath, ArtifactReviewReport.Render(context.Request.ApplicationName, target, advisories));
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // The conversion already succeeded and is on disk; a failed review must not discard it.
                advisories = [];
                context.Warn($"The model review did not complete ({FailureText.Describe(exception)}). The converted schema is unaffected.");
                context.Workspace.WriteText(reviewPath, ArtifactReviewReport.RenderFailure(
                    context.Request.ApplicationName, target, exception.Message));
            }

            artifacts.Add(new ArtifactReference(
                reviewPath,
                ArtifactKind.ValidationReport,
                "Unverified model review of the generated DDL. Advisory only; it gates nothing."));

            int willFail = advisories.Count(advisory => advisory.Severity == AdvisorySeverity.WillFail);
            if (willFail > 0)
            {
                context.Warn(
                    $"The review model claims {willFail.ToString(CultureInfo.InvariantCulture)} statements will not execute on PostgreSQL. " +
                    "Unverified: confirm each against a real instance.");
            }

            // Prefixed so an advisory claim is never mistaken for a converter result in the phase findings.
            findings.AddRange(advisories.Select(advisory =>
                $"Advisory ({advisory.Severity}): {advisory.Construct} — {advisory.Reason}"));
        }

        if (orchestrator is not null)
        {
            await ProposeRepairAsync(context, conversion, target, appRootless: outputRoot, artifacts, findings, cancellationToken)
                .ConfigureAwait(false);
        }

        return PhaseExecutionResult.Success(artifacts, findings);
    }

    private sealed record ScriptAccounting(string Path, IReadOnlyList<OracleStatementAccount> Accounts);

    /// <summary>
    /// Accounts for every statement the structural parser did not recognise, per source file.
    ///
    /// The section exists because the old report could only describe constructs the converter had a
    /// signature for. Anything outside that list produced no DDL and no finding, so the report read clean
    /// while the target was missing an object. Counts and snippets here make that impossible to miss.
    /// </summary>
    private static string RenderStatementAccounting(IReadOnlyList<ScriptAccounting> accounting)
    {
        StringBuilder section = new();
        section.Append('\n').AppendLine("## Unparsed statement accounting").AppendLine();

        int total = accounting.Sum(script => script.Accounts.Count);
        if (total == 0)
        {
            section.AppendLine("Every statement in the supplied SQL was recognised by the structural parser.").AppendLine();
            return section.ToString();
        }

        section.AppendLine("| Source file | Statements not structurally parsed | Classified program units | Client directives | Comments | Data statements | Administrative | Omitted schema-bearing | Unclassified |");
        section.AppendLine("| --- | --- | --- | --- | --- | --- | --- | --- | --- |");

        foreach (ScriptAccounting script in accounting)
        {
            int Count(OracleStatementDisposition disposition) =>
                script.Accounts.Count(account => account.Disposition == disposition);

            section
                .Append("| `").Append(script.Path).Append("` | ")
                .Append(script.Accounts.Count.ToString(CultureInfo.InvariantCulture)).Append(" | ")
                .Append(Count(OracleStatementDisposition.ClassifiedProgramUnit).ToString(CultureInfo.InvariantCulture)).Append(" | ")
                .Append(Count(OracleStatementDisposition.ClientDirective).ToString(CultureInfo.InvariantCulture)).Append(" | ")
                .Append(Count(OracleStatementDisposition.Comment).ToString(CultureInfo.InvariantCulture)).Append(" | ")
                .Append(Count(OracleStatementDisposition.DataStatement).ToString(CultureInfo.InvariantCulture)).Append(" | ")
                .Append(Count(OracleStatementDisposition.Administrative).ToString(CultureInfo.InvariantCulture)).Append(" | ")
                .Append(Count(OracleStatementDisposition.OmittedSchemaBearing).ToString(CultureInfo.InvariantCulture)).Append(" | ")
                .Append(Count(OracleStatementDisposition.Unclassified).ToString(CultureInfo.InvariantCulture)).AppendLine(" |");
        }

        section.AppendLine();

        (string Path, OracleStatementAccount Account)[] omitted =
        [
            .. accounting.SelectMany(script => script.Accounts
                .Where(account => account.Disposition is OracleStatementDisposition.OmittedSchemaBearing or OracleStatementDisposition.Unclassified)
                .Select(account => (script.Path, Account: account))),
        ];

        if (omitted.Length == 0)
        {
            section.AppendLine("No statement creates or alters a schema object that this conversion left out.").AppendLine();
            return section.ToString();
        }

        section.AppendLine("### Statements this conversion did not emit").AppendLine();
        section.AppendLine("| Source file | Disposition | Statement |");
        section.AppendLine("| --- | --- | --- |");

        foreach ((string path, OracleStatementAccount account) in omitted)
        {
            section
                .Append("| `").Append(path).Append("` | ").Append(account.Disposition).Append(" | `")
                .Append(account.Snippet.Replace("|", "\\|", StringComparison.Ordinal)).AppendLine("` |");
        }

        section.AppendLine();
        section.AppendLine("A statement listed as `OmittedSchemaBearing` creates or alters a persistent object that is not in the emitted DDL.");
        section.AppendLine("A statement listed as `Unclassified` matched nothing this fleet recognises, so what it declares is unknown and cannot be assumed harmless.");
        section.AppendLine("The conversion is refused while any of them remain, because a schema missing an object the export declared is not a copy of it.");
        section.AppendLine();

        return section.ToString();
    }

    /// <summary>
    /// States which Oracle release the converted text is claimed to come from, and what that claim rests on.
    /// The parser reads SQL, so a clean conversion proves the constructs present in the supplied export and
    /// says nothing about a release this fleet never connected to.
    /// </summary>
    private static string RenderSourceRelease(OracleVersionAssessment source, int scripts)
    {
        StringBuilder section = new();
        section.Append('\n').AppendLine("## Source Oracle Database release").AppendLine();
        section.AppendLine("| Question | Answer |");
        section.AppendLine("| --- | --- |");
        section.Append("| Declared at intake | ")
               .Append(source.Supplied.Length > 0 ? source.Supplied.Replace("|", "\\|", StringComparison.Ordinal) : "unknown")
               .AppendLine(" |");
        section.Append("| Interpreted as | ").Append(source.Label).AppendLine(" |");
        section.Append("| Recognized release | ").Append(source.IsRecognized ? "yes" : "no").AppendLine(" |");
        section.Append("| Conversion readiness | ").Append(source.Readiness).AppendLine(" |");
        section.Append("| SQL scripts read | ").Append(scripts.ToString(CultureInfo.InvariantCulture)).AppendLine(" |");
        section.AppendLine();
        section.AppendLine(source.Disposition).AppendLine();

        section.AppendLine("This conversion is evidence about **the supplied text**, not about an Oracle instance. Everything above was");
        section.AppendLine("read from SQL files an operator placed in the workspace. This fleet has no live Oracle extraction adapter: it");
        section.AppendLine("opened no database, ran no query, and confirmed no release. A clean conversion therefore proves that the");
        section.AppendLine("constructs appearing in that export were translated, and does not establish support for every construct the");
        section.Append("release ").Append(source.Label).AppendLine(" can produce.").AppendLine();

        foreach (string warning in source.Warnings)
        {
            section.Append("- ").AppendLine(warning);
        }

        return section.ToString();
    }

    /// <summary>
    /// Runs the critic-and-repair exchange and writes any revision <em>beside</em> the deterministic DDL.
    /// The converter's own output is never overwritten: a model editing reviewed SQL must leave the
    /// difference visible rather than silently replacing what a human signed off.
    /// </summary>
    private async Task ProposeRepairAsync(
        PhaseExecutionContext context,
        PostgreSqlConversion conversion,
        DatabaseTarget target,
        string appRootless,
        List<ArtifactReference> artifacts,
        List<string> findings,
        CancellationToken cancellationToken)
    {
        context.Info("Running the critic and repair agents over the generated schema.");

        OrchestrationResult result = await orchestrator!.RunAsync(
            new FleetAgentRequest(context.Request.ApplicationName, target, conversion.Ddl, findings),
            step => context.Info($"  [{step.Role}] {step.Agent}: {step.Summary}"),
            cancellationToken).ConfigureAwait(false);

        context.Info($"Exchange ended: {result.Termination} after {result.Steps.Count.ToString(CultureInfo.InvariantCulture)} steps.");

        if (!result.ProducedRevision)
        {
            return;
        }

        string proposedPath = $"{appRootless}/database/postgresql/schema/schema.proposed.sql";
        context.Workspace.WriteText(proposedPath, result.ProposedArtifact!);

        artifacts.Add(new ArtifactReference(
            proposedPath,
            ArtifactKind.DatabaseSchema,
            "Agent-proposed repair of the generated DDL. Unverified, and not a replacement for schema.sql."));

        context.Warn(
            "The repair agent proposed a revised schema. It was written beside the converted DDL, not over it, " +
            "and has not been executed or reviewed.");

        findings.Add($"Proposal: a repair agent revised the schema after {result.Steps.Count.ToString(CultureInfo.InvariantCulture)} steps ({result.Termination}).");
    }

    /// <summary>Prefers the blueprint's own artifact description when the plan declares this exact path.</summary>
    private static ArtifactReference Declared(PhaseExecutionContext context, string path, ArtifactKind kind, string description) =>
        context.Plan.ExpectedOutputs.FirstOrDefault(artifact =>
            string.Equals(artifact.Path, path, StringComparison.OrdinalIgnoreCase)) ?? new ArtifactReference(path, kind, description);
}
