// Copyright (c) Microsoft. All rights reserved.

using System.Globalization;
using System.Text;

namespace OracleFormsMigrationFleet.Fleet.Execution.Adapters;

/// <summary>
/// Generates the Azure-targeted application tier so a run migrates the application, not only its schema.
///
/// Output goes to the operator's session workspace like every other adapter, and the phase produces no
/// attestation: generated code that has never been compiled or run is not evidence of anything.
/// </summary>
public sealed class ApplicationCodeConversionAdapter(IArtifactReviewer? reviewer = null) : IPhaseAdapter
{
    private const int MaxFiles = 20_000;
    private const long MaxTextBytes = 8L * 1024 * 1024;

    public MigrationPhase Phase => MigrationPhase.ApplicationCodeConversion;

    public async Task<PhaseExecutionResult> ExecuteAsync(PhaseExecutionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        string sourceRoot = WorkspacePath.Normalize(context.SourceRoot);
        string outputRoot = WorkspacePath.Normalize(context.OutputRoot);

        if (!context.Workspace.DirectoryExists(sourceRoot))
        {
            return PhaseExecutionResult.Failure($"The source root '{sourceRoot}' does not exist in the workspace. Nothing was generated.");
        }

        List<OracleSchema> schemas = [];
        List<FormsModule> forms = [];
        List<ConversionFinding> formsFindings = [];
        int formsModules = 0;

        foreach (WorkspaceFile file in context.Workspace.EnumerateFiles(sourceRoot, MaxFiles))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string extension = Path.GetExtension(file.RelativePath);

            if (extension.Equals(".fmb", StringComparison.OrdinalIgnoreCase))
            {
                formsModules++;
                continue;
            }

            if (extension.Equals(".xml", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    FormsModuleParse parsed = FormsModuleParser.Parse(context.Workspace.ReadText(file.RelativePath, MaxTextBytes));
                    if (parsed.Modules.Count > 0)
                    {
                        forms.AddRange(parsed.Modules);
                        formsFindings.AddRange(parsed.Findings);
                        context.Info($"Read Forms module {string.Join(", ", parsed.Modules.Select(module => module.Name))} from {file.RelativePath}.");
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
                schemas.Add(OracleSchemaParser.Parse(context.Workspace.ReadText(file.RelativePath, MaxTextBytes)));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                context.Warn($"{file.RelativePath} could not be read and was skipped.");
            }
        }

        if (schemas.Count == 0)
        {
            return PhaseExecutionResult.Failure(
                $"No '.sql' files were found under '{sourceRoot}'. The application tier is generated from the schema, " +
                "so there was nothing to generate from.");
        }

        OracleSchema schema = OracleSchema.Merge(schemas);
        bool recognized = NorthstarBankingApplicationProfile.Matches(schema);
        ApplicationConversion conversion = ApplicationCodeEmitter.Convert(
            schema, context.Request.ApplicationName, context.Request.Target.Database, forms);

        conversion = conversion with { Findings = [.. formsFindings, .. conversion.Findings] };

        if (conversion.Files.Count == 0)
        {
            return PhaseExecutionResult.Failure(
                conversion.Findings.FirstOrDefault()?.Reason ?? "No application code could be generated from the supplied schema.");
        }

        string appRoot = $"{outputRoot}/application";
        foreach (GeneratedFile file in conversion.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            context.Workspace.WriteText($"{appRoot}/{file.Path}", file.Contents);
        }

        context.Info(
            $"Generated {conversion.Files.Count.ToString(CultureInfo.InvariantCulture)} files: " +
            $"{schema.Tables.Count.ToString(CultureInfo.InvariantCulture)} entities with repositories, " +
            (recognized
                ? "the generated workflow routes, and a browser client."
                : "REST endpoints, and a React client."));

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
                (recognized
                    ? "No behaviour was taken from them: the generated workflows come from the schema and this fleet's template."
                    : "The generated screens come from table structure, not from those modules."));
        }

        string reportPath = $"{appRoot}/CONVERSION_NOTES.md";
        context.Workspace.WriteText(reportPath, RenderNotes(
            context.Request.ApplicationName, conversion, schema, formsModules, forms.Count, recognized));

        List<ArtifactReference> artifacts =
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
            context.Info("Reviewing the generated application with the review model.");

            try
            {
                IReadOnlyList<AdvisoryFinding> advisories = await reviewer.ReviewAsync(
                    new ArtifactReviewRequest(
                        context.Request.ApplicationName,
                        context.Request.Target.Database,
                        string.Join("\n\n", conversion.Files.Take(6).Select(file => $"-- {file.Path}\n{file.Contents}")),
                        findings),
                    cancellationToken).ConfigureAwait(false);

                context.Workspace.WriteText(reviewPath, ArtifactReviewReport.Render(
                    context.Request.ApplicationName, context.Request.Target.Database, advisories));

                findings.AddRange(advisories.Select(advisory => $"Advisory ({advisory.Severity}): {advisory.Construct} — {advisory.Reason}"));
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                context.Warn($"The model review did not complete ({FailureText.Describe(exception)}). The generated code is unaffected.");
                context.Workspace.WriteText(reviewPath, ArtifactReviewReport.RenderFailure(
                    context.Request.ApplicationName, context.Request.Target.Database, exception.Message));
            }

            artifacts.Add(new ArtifactReference(reviewPath, ArtifactKind.ValidationReport, "Unverified model review of the generated application. Advisory only."));
        }

        return PhaseExecutionResult.Success(artifacts, findings);
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
