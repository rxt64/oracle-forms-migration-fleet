// Copyright (c) Microsoft. All rights reserved.

using System.Globalization;

namespace OracleFormsMigrationFleet.Fleet.Execution.Adapters;

/// <summary>
/// Loads the supplied Oracle data export into the sandbox target.
///
/// This is the first adapter that writes outside the workspace, so the gate matters more here than
/// anywhere else: the executor only calls it when the planner resolved SandboxDataMigration to
/// <see cref="PhaseStatus.Planned"/>, which needs an execution approval distinct from plan approval.
/// Without a configured gateway the phase fails rather than reporting success it did not achieve.
/// </summary>
public sealed class SandboxDataMigrationAdapter(
    IDataMigrationGateway? gateway = null,
    ProgramUnitRepairLoop? programUnitRepair = null) : IPhaseAdapter
{
    private const int MaxFiles = 20_000;
    private const long MaxTextBytes = 16L * 1024 * 1024;

    public MigrationPhase Phase => MigrationPhase.SandboxDataMigration;

    public async Task<PhaseExecutionResult> ExecuteAsync(PhaseExecutionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (gateway is null)
        {
            return PhaseExecutionResult.Failure(
                "No data migration gateway is configured, so this build cannot reach a database. The phase was " +
                "authorized but nothing was moved, and no row was claimed to have been migrated.");
        }

        if (context.Request.Target.Database != DatabaseTarget.PostgreSql)
        {
            return PhaseExecutionResult.Failure(
                $"Data migration is implemented for PostgreSQL only, not {context.Request.Target.Database}.");
        }

        string sourceRoot = WorkspacePath.Normalize(context.SourceRoot);
        string outputRoot = WorkspacePath.Normalize(context.OutputRoot);

        if (!context.Workspace.DirectoryExists(sourceRoot))
        {
            return PhaseExecutionResult.Failure($"The source root '{sourceRoot}' does not exist in the workspace.");
        }

        List<DataMigrationStatement> statements = [];
        List<string> skipped = [];
        List<string> programUnitFailures = [];
        string? repairPath = null;
        string? repairAuditPath = null;

        foreach (WorkspaceFile file in context.Workspace.EnumerateFiles(sourceRoot, MaxFiles))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!OracleSourceFile.IsSqlText(file.RelativePath))
            {
                continue;
            }

            try
            {
                IReadOnlyList<DataMigrationStatement> translated = DataMigrationTranslator.Translate(
                    context.Workspace.ReadText(file.RelativePath, MaxTextBytes), out IReadOnlyList<string> ignored);

                if (translated.Count > 0)
                {
                    context.Info($"{file.RelativePath}: {translated.Count.ToString(CultureInfo.InvariantCulture)} rows to load.");
                }

                statements.AddRange(translated);
                skipped.AddRange(ignored);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                context.Warn($"{file.RelativePath} could not be read and was skipped.");
            }
        }

        if (statements.Count == 0)
        {
            return PhaseExecutionResult.Failure(
                "No INSERT statement was found in the supplied source, so there was no data to migrate.");
        }

        string[] tables = [.. statements.Select(statement => statement.Table).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];

        // Rows cannot land in tables that do not exist, and the converted schema is this fleet's own output
        // from the conversion phase. Applying it here is what keeps a migration reproducible by the tool
        // alone: without it the target has to be prepared by hand, and a hand-prepared target is untracked.
        string schemaPath = $"{outputRoot}/database/postgresql/schema/schema.sql";
        if (context.Workspace.FileExists(schemaPath))
        {
            string schema = context.Workspace.ReadText(schemaPath, MaxTextBytes);

            // Tables must be right before rows can land. A translated routine that will not compile is
            // remediation work, not a reason to refuse the data, so the two are applied and judged apart.
            int split = schema.IndexOf(PlSqlTranslator.ProgramUnitsMarker, StringComparison.Ordinal);
            string structural = split < 0 ? schema : schema[..split];
            string programmable = split < 0 ? string.Empty : schema[split..];

            try
            {
                SchemaDeploymentOutcome prepared = await gateway
                    .PrepareAsync(DataMigrationTranslator.SplitSchema(structural), cancellationToken)
                    .ConfigureAwait(false);

                context.Info(
                    $"Schema: {prepared.Applied.ToString(CultureInfo.InvariantCulture)} objects created, " +
                    $"{prepared.AlreadyPresent.ToString(CultureInfo.InvariantCulture)} already present.");

                if (prepared.Failures.Count > 0)
                {
                    return PhaseExecutionResult.Failure(
                        $"{prepared.Failures.Count.ToString(CultureInfo.InvariantCulture)} schema statements failed, so " +
                        "no rows were loaded into a target that does not match the converted schema.",
                        [.. prepared.Failures]);
                }

                List<string> acceptedRepairs = [];
                if (programmable.Length > 0)
                {
                    SchemaDeploymentOutcome units = await gateway
                        .PrepareAsync(DataMigrationTranslator.SplitSchema(programmable), cancellationToken)
                        .ConfigureAwait(false);

                    context.Info(
                        $"Program units: {units.Applied.ToString(CultureInfo.InvariantCulture)} created, " +
                        $"{units.AlreadyPresent.ToString(CultureInfo.InvariantCulture)} already present.");

                    foreach (string failure in units.Failures)
                    {
                        context.Warn($"Program unit rejected by the target: {failure}");
                    }

                    if (units.Failures.Count > 0)
                    {
                        repairPath = $"{outputRoot}/database/postgresql/schema/program-unit-repairs.sql";
                        SchemaDeploymentOutcome repairInput = units;
                        if (context.Workspace.FileExists(repairPath) && units.StatementFailures.Count > 0)
                        {
                            IReadOnlyList<string> persisted = DataMigrationTranslator.SplitSchema(
                                context.Workspace.ReadText(repairPath, MaxTextBytes));

                            if (ProgramUnitRepairLoop.IsBodyOnlySubset(
                                    units.StatementFailures.Select(failure => failure.Statement),
                                    persisted,
                                    out IReadOnlySet<string> replacedEnvelopes))
                            {
                                SchemaDeploymentOutcome revalidated = await gateway
                                    .PrepareAsync(persisted, cancellationToken)
                                    .ConfigureAwait(false);

                                if (revalidated.Failures.Count == 0)
                                {
                                    acceptedRepairs.AddRange(persisted);
                                    repairAuditPath = $"{outputRoot}/database/postgresql/schema/program-unit-repair-audit.md";
                                    context.Workspace.WriteText(
                                        repairAuditPath,
                                        RenderRevalidationAudit(context.Request.ApplicationName, persisted));
                                    context.Info(
                                        $"Revalidated {persisted.Count.ToString(CultureInfo.InvariantCulture)} accepted " +
                                        "program-unit repairs from the previous run.");

                                    SchemaStatementFailure[] remaining =
                                    [.. units.StatementFailures.Where(failure =>
                                        !replacedEnvelopes.Contains(ProgramUnitRepairLoop.RoutineEnvelope(failure.Statement)))];
                                    repairInput = new SchemaDeploymentOutcome(
                                        0,
                                        0,
                                        [.. remaining.Select(failure => failure.Diagnostic)])
                                    {
                                        StatementFailures = remaining,
                                    };
                                }
                            }
                        }

                        if (repairInput.Failures.Count > 0 && programUnitRepair is not null)
                        {
                            ProgramUnitRepairOutcome repair = await programUnitRepair.RunAsync(
                                context.Request.ApplicationName,
                                gateway,
                                repairInput,
                                context.Info,
                                cancellationToken).ConfigureAwait(false);

                            acceptedRepairs.AddRange(repair.AcceptedStatements);
                            programUnitFailures.AddRange(repair.OutstandingFailures);
                            foreach (string failure in repair.OutstandingFailures)
                            {
                                context.Warn($"Program unit remains rejected after repair: {failure}");
                            }

                            context.Info(
                                $"Program-unit repair: {acceptedRepairs.Count.ToString(CultureInfo.InvariantCulture)} accepted " +
                                $"after {repair.Attempts.ToString(CultureInfo.InvariantCulture)} attempts; " +
                                $"{repair.OutstandingFailures.Count.ToString(CultureInfo.InvariantCulture)} remain rejected.");

                            repairAuditPath = $"{outputRoot}/database/postgresql/schema/program-unit-repair-audit.md";
                            context.Workspace.WriteText(
                                repairAuditPath,
                                RenderRepairAudit(context.Request.ApplicationName, repair));
                        }

                        if (repairInput.Failures.Count > 0 && programUnitRepair is null)
                        {
                            programUnitFailures.AddRange(repairInput.Failures);
                        }

                        int outstanding = programUnitFailures.Count;

                        if (outstanding > 0)
                        {
                            context.Warn(
                                $"{outstanding.ToString(CultureInfo.InvariantCulture)} translated program units did not compile. " +
                                "The tables are correct and the rows below were still loaded, but that logic is absent from the target.");
                        }
                    }

                    if (acceptedRepairs.Count > 0)
                    {
                        repairPath ??= $"{outputRoot}/database/postgresql/schema/program-unit-repairs.sql";
                        context.Workspace.WriteText(repairPath, string.Join(";\n\n", acceptedRepairs) + ";\n");
                        context.Info($"Accepted program-unit repairs written to {repairPath}.");
                    }
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                return PhaseExecutionResult.Failure($"The sandbox schema could not be applied: {exception.Message}");
            }
        }
        else
        {
            context.Warn(
                $"No converted schema was found at {schemaPath}. Rows will only load if the target was already prepared.");
        }

        context.Info(
            $"Loading {statements.Count.ToString(CultureInfo.InvariantCulture)} rows into " +
            $"{tables.Length.ToString(CultureInfo.InvariantCulture)} tables.");

        DataMigrationOutcome outcome;
        try
        {
            outcome = await gateway.ApplyAsync(statements, tables, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return PhaseExecutionResult.Failure($"The data migration did not complete: {exception.Message}");
        }

        string reportPath = $"{outputRoot}/data/migration-report.md";
        context.Workspace.WriteText(reportPath, DataMigrationReport.Render(context.Request.ApplicationName, outcome, skipped));

        if (outcome.RowsAlreadyPresent > 0)
        {
            context.Info(
                $"{outcome.RowsAlreadyPresent.ToString(CultureInfo.InvariantCulture)} rows were already in the target " +
                "under the same key and were not inserted by this run.");
        }

        foreach (TableRowCount count in outcome.RowCounts)
        {
            context.Info($"{count.Table}: {count.Rows.ToString(CultureInfo.InvariantCulture)} rows in the target.");
        }

        if (outcome.StatementsFailed > 0)
        {
            context.Warn($"{outcome.StatementsFailed.ToString(CultureInfo.InvariantCulture)} statements failed. The target is partially loaded.");

            return PhaseExecutionResult.Failure(
                $"{outcome.StatementsFailed.ToString(CultureInfo.InvariantCulture)} of " +
                $"{statements.Count.ToString(CultureInfo.InvariantCulture)} statements failed, so the target holds an " +
                "incomplete copy. See the migration report.",
                [.. outcome.Failures]);
        }

        List<ArtifactReference> artifacts =
        [
            new(reportPath, ArtifactKind.ReconciliationReport, "Statements executed and the row counts read back from the target."),
        ];

        if (repairPath is not null)
        {
            artifacts.Add(new ArtifactReference(
                repairPath,
                ArtifactKind.DatabaseSchema,
                "Program-unit revisions accepted by the PostgreSQL compiler."));
        }

        if (repairAuditPath is not null)
        {
            artifacts.Add(new ArtifactReference(
                repairAuditPath,
                ArtifactKind.ValidationReport,
                "Repair attempts, compiler acceptance, and outstanding program-unit failures."));
        }

        if (programUnitFailures.Count > 0)
        {
            return new PhaseExecutionResult(
                false,
                artifacts,
                programUnitFailures,
                $"Rows were loaded, but {programUnitFailures.Count.ToString(CultureInfo.InvariantCulture)} program-unit " +
                "failures remain, so the sandbox migration cannot be attested as complete.");
        }

        return PhaseExecutionResult.Success(
            artifacts,
            [.. outcome.RowCounts.Select(count => $"Loaded: {count.Table} — {count.Rows.ToString(CultureInfo.InvariantCulture)} rows")]);
    }

    private static string RenderRepairAudit(string applicationName, ProgramUnitRepairOutcome repair)
    {
        string attempted = repair.AttemptedStatements.Count == 0
            ? "_No proposal reached the database compiler._"
            : string.Join("\n\n", repair.AttemptedStatements.Select(statement => $"```sql\n{statement}\n```"));
        string failures = repair.OutstandingFailures.Count == 0
            ? "None."
            : string.Join("\n", repair.OutstandingFailures.Select(failure => $"- {failure}"));

        return $"""
            # Program-unit repair audit — {applicationName}

            Attempts: {repair.Attempts.ToString(CultureInfo.InvariantCulture)}

            Accepted statements: {repair.AcceptedStatements.Count.ToString(CultureInfo.InvariantCulture)}

            ## Attempted SQL

            {attempted}

            ## Outstanding failures

            {failures}
            """;
    }

    private static string RenderRevalidationAudit(string applicationName, IReadOnlyList<string> persisted) =>
        $"""
        # Program-unit repair audit — {applicationName}

        {persisted.Count.ToString(CultureInfo.InvariantCulture)} repairs retained from the previous run passed the
        body-only contract policy and were accepted again by the PostgreSQL compiler. The repair model was not called.

        ## Revalidated SQL

        {string.Join("\n\n", persisted.Select(statement => $"```sql\n{statement}\n```"))}
        """;
}
