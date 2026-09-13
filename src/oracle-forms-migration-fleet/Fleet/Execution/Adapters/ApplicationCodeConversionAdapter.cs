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

            if (!extension.Equals(".sql", StringComparison.OrdinalIgnoreCase))
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
            $"{schema.Tables.Count.ToString(CultureInfo.InvariantCulture)} entities with repositories, REST endpoints, and a React client.");

        if (formsModules > 0)
        {
            // Stated as a limit rather than silently producing screens that look authoritative.
            context.Warn(
                $"{formsModules.ToString(CultureInfo.InvariantCulture)} Forms modules were found and could not be read. " +
                "The generated screens come from table structure, not from those modules.");
        }

        string reportPath = $"{appRoot}/CONVERSION_NOTES.md";
        context.Workspace.WriteText(reportPath, RenderNotes(context.Request.ApplicationName, conversion, schema, formsModules, forms.Count));

        List<ArtifactReference> artifacts =
        [
            new($"{appRoot}/backend", ArtifactKind.BackEndCode, "Spring Boot entities, repositories, and REST endpoints over the converted schema."),
            new($"{appRoot}/frontend", ArtifactKind.FrontEndCode, "React client and screen generated from the converted schema."),
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
                context.Warn($"The model review did not complete ({exception.GetType().Name}). The generated code is unaffected.");
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
        int formsRead)
    {
        StringBuilder builder = new();
        builder.AppendLine("# Application conversion notes").AppendLine();
        builder.Append("Application: ").AppendLine(applicationName);
        builder.AppendLine();
        builder.AppendLine("The generated back end talks to Azure Database for PostgreSQL with Entra authentication. Oracle is");
        builder.AppendLine("not in its data path and no database password exists anywhere in the output.").AppendLine();

        builder.AppendLine("## Generated").AppendLine();
        builder.AppendLine("| File | Purpose |");
        builder.AppendLine("| --- | --- |");
        foreach (GeneratedFile file in conversion.Files)
        {
            builder.Append("| `").Append(file.Path).Append("` | ").Append(file.Description).AppendLine(" |");
        }

        builder.AppendLine().AppendLine("## Not generated, and why").AppendLine();

        if (formsModules > 0)
        if (formsRead > 0)
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
        builder.AppendLine("2. Add authentication and authorization. Every endpoint is currently open.");
        builder.AppendLine("3. Test the translated PL/pgSQL against the original behaviour, then decide for each rule");
        builder.AppendLine("   whether it stays in the database or moves into this tier. Nothing here calls it yet.");
        builder.AppendLine("4. Reconcile the migrated data against the source. Row counts are not a reconciliation.");

        if (schema.Unparsed.Count > 0)
        {
            builder.AppendLine().Append("Statements the parser did not interpret: ")
                   .Append(schema.Unparsed.Count.ToString(CultureInfo.InvariantCulture)).AppendLine(".");
        }

        return builder.ToString();
    }
}
