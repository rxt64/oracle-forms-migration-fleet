// Copyright (c) Microsoft. All rights reserved.

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OracleFormsMigrationFleet.Fleet.Execution.Adapters;

/// <summary>
/// Reads the approved target after a migration landed in it, and records what each recorded decision
/// actually got.
///
/// It runs after <see cref="MigrationPhase.SandboxDataMigration"/> because that is the phase that puts
/// the converted schema into the database an operator approved. Before it there is no migrated target to
/// read, and the only way to produce one would be for this process to execute the generated DDL itself —
/// which would prove that the DDL parses under the host's own privileges and nothing about the customer's
/// destination.
///
/// Three things are deliberately not done here. No generated file is executed, so nothing a generator or
/// a hostile source tree emitted reaches a database. No expectation is read out of the generated output,
/// so the target is compared against the source rather than against itself. And no case is planned, and
/// no connection is opened, until the server has granted a claim over a generation it already recorded
/// durably — a probe of a customer database to prove something about output nothing has recorded would be
/// evidence with nothing to attach it to.
/// </summary>
public sealed class TargetContractVerificationAdapter(
    IEntryRuntimeVerificationGateway? gateway = null) : IPhaseAdapter
{
    private const int MaxFiles = 20_000;
    private const long MaxTextBytes = 8L * 1024 * 1024;

    /// <summary>How many gaps a refusal repeats inline. The written record always holds every one.</summary>
    private const int MaxReportedGaps = 25;

    /// <summary>
    /// The coverage record is written by the generation phase with default naming, so it is read back with
    /// default naming. Reading it through camel-cased options would silently produce a record with every
    /// field empty, which is the fail-open reading this adapter exists to avoid.
    /// </summary>
    private static readonly JsonSerializerOptions s_json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public MigrationPhase Phase => MigrationPhase.TargetContractVerification;

    public async Task<PhaseExecutionResult> ExecuteAsync(
        PhaseExecutionContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        string outputRoot = WorkspacePath.Normalize(context.OutputRoot);
        string coveragePath = $"{outputRoot}/{GenerationCoverage.RecordPath}";

        if (context.CompletedPhases.FirstOrDefault(outcome => outcome.Phase == MigrationPhase.SandboxDataMigration)
            is not { State: PhaseExecutionState.Executed })
        {
            return PhaseExecutionResult.Failure(
                "The sandbox data migration did not complete in this run, so the approved target does not hold this run's " +
                "converted schema. Nothing was read, because the only alternative would be for this process to build a target " +
                "out of generated DDL, which proves nothing about the database the operator approved.");
        }

        if (!context.Workspace.FileExists(coveragePath))
        {
            return PhaseExecutionResult.Success(
                [],
                [
                    "This run generated under no disposition ledger, so there is no recorded decision a result could be " +
                    "attributed to and the approved target was not read.",
                ]);
        }

        if (gateway is null)
        {
            return PhaseExecutionResult.Failure(
                "This run generated under a disposition ledger and this host has no read-only target verification gateway, so " +
                "no recorded decision was checked against the migrated database. The generated test suites say how many cases " +
                "ran, not which decision any of them was about.");
        }

        if (context.VerificationProvider is null)
        {
            return PhaseExecutionResult.Failure(
                "This host established no authority over verifying this run's generation, so nothing was read. A result about " +
                "a generation the server has not recorded is a result with nothing to attach it to.");
        }

        try
        {
            return await VerifyAsync(context, outputRoot, coveragePath, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is JsonException or WorkspaceLimitExceededException
            or WorkspacePathException or IOException or UnauthorizedAccessException)
        {
            return PhaseExecutionResult.Failure(
                $"This run's generation could not be read back for target verification ({FailureText.Describe(exception)}), " +
                "so the approved target was not read.");
        }
    }

    private async Task<PhaseExecutionResult> VerifyAsync(
        PhaseExecutionContext context,
        string outputRoot,
        string coveragePath,
        CancellationToken cancellationToken)
    {
        GenerationCoverageRecord? covered = JsonSerializer.Deserialize<GenerationCoverageRecord>(
            context.Workspace.ReadText(coveragePath, MaxTextBytes), s_json);

        if (covered is null || covered.OutputFiles.Count == 0)
        {
            return PhaseExecutionResult.Failure(
                $"The generation coverage at '{coveragePath}' could not be read as a coverage record, so which decisions this " +
                "output was generated under is unknown and the approved target was not read.");
        }

        // Hashed off disk before anything is claimed and again after everything is read. The output a
        // result speaks about has to be the same bytes throughout, or the result describes something that
        // changed underneath it.
        string before = Digest(context, outputRoot, covered.OutputFiles);

        EntryVerificationClaimDecision decision =
            await context.VerificationProvider!.ClaimAsync(before, cancellationToken).ConfigureAwait(false);

        if (decision.Claim is not { } claim)
        {
            return PhaseExecutionResult.Failure(
                "The server granted no claim over this run's generation, so no connection was opened to the approved target: " +
                string.Join(" ", decision.Denials));
        }

        if (!claim.ApprovedTarget.SameTargetAs(gateway!.Target))
        {
            return PhaseExecutionResult.Failure(
                $"This project's approved target profile names {claim.ApprovedTarget.Describe()} and this host's verifier is " +
                $"wired to {gateway.Target.Describe()}. Nothing was read: a verifier with a destination of its own is a second " +
                "target nobody approved.");
        }

        (TargetMapping? mapping, IReadOnlyList<FormsModule> modules, string? refusal) =
            ReadSourceModel(context, outputRoot);

        if (mapping is null)
        {
            return PhaseExecutionResult.Failure(
                refusal ?? "The source model this verification derives its expectations from could not be read.");
        }

        // Planned from the decisions the ledger holds now, carried in the claim. The run's own coverage
        // document says which properties it generated for; it does not get to say what they are still
        // decided as.
        EntryVerificationCoverage.VerificationPlan plan =
            EntryVerificationCoverage.Plan(mapping, modules, claim.Covered);

        EntryRuntimeVerificationRun run;
        try
        {
            run = await gateway.InspectAsync(claim.ApprovedTarget, plan.Expectations, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (UnauthorizedAccessException exception)
        {
            return PhaseExecutionResult.Failure(
                "The grant that authorizes reading the approved target did not hold at the moment this phase would have " +
                $"opened the connection, so nothing was read: {exception.Message}");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return PhaseExecutionResult.Failure(
                $"The approved target could not be read ({FailureText.Describe(exception)}). Nothing was observed.");
        }

        if (!run.ToolAvailable || run.TimedOut || run.SetupFailed || run.Failure is { Length: > 0 })
        {
            return PhaseExecutionResult.Failure(
                Scrub(run.Failure ?? "The approved target did not answer a usable read."));
        }

        string after = Digest(context, outputRoot, covered.OutputFiles);
        if (!string.Equals(before, after, StringComparison.OrdinalIgnoreCase))
        {
            return PhaseExecutionResult.Failure(
                "This run's generated output changed while the approved target was being read, so what was observed cannot be " +
                "said to be about the output that was claimed. Nothing was recorded.");
        }

        (IReadOnlyList<EntryVerificationCase> cases, IReadOnlyList<EntryVerificationGap> unanswered) =
            EntryVerificationCoverage.Join(plan.Expectations, run.Observations);

        EntryVerificationGap[] gaps = [.. plan.Gaps, .. unanswered];
        EntryVerificationPlannedCase[] planned = [.. plan.Expectations.Select(expectation => expectation.AsPlanned())];

        if (cases.Count > EntryVerificationCoverage.MaxCases ||
            gaps.Length > EntryVerificationCoverage.MaxCases ||
            planned.Length > EntryVerificationCoverage.MaxCases)
        {
            return PhaseExecutionResult.Failure(
                "This verification produced more cases or gaps than one record describes, so none was written rather than a " +
                "record that showed part of what ran.");
        }

        EntryVerificationCoverageRecord record = new(
            EntryVerificationCoverage.SchemaVersion,
            claim.LedgerId,
            claim.TenantId,
            claim.ProjectId,
            claim.RunId,
            claim.SourceSnapshotHash,
            outputRoot,
            claim.OutputSetSha256,
            EntryVerificationCoverage.VerifierId,
            claim.ApprovedTarget,
            DateTimeOffset.UtcNow,
            planned,
            [.. cases.Select(item => item with { Detail = Scrub(item.Detail), Actual = Scrub(item.Actual) })],
            [.. gaps.Select(gap => gap with { Reason = Scrub(gap.Reason) })]);

        string recordPath = $"{outputRoot}/{EntryVerificationCoverage.RecordPath}";
        context.Workspace.WriteText(recordPath, JsonSerializer.Serialize(record, s_json));

        ArtifactReference artifact = new(
            recordPath,
            ArtifactKind.ExecutableVerificationReport,
            "What the approved target held for each recorded disposition, read only, plus every gap this verification leaves. " +
            "It asserts nothing about the generated service, which was never started.");

        int failed = cases.Count(item => item.Outcome == DispositionVerificationStatus.Failed);

        context.Info(
            $"Read {cases.Count.ToString(CultureInfo.InvariantCulture)} case(s) from {claim.ApprovedTarget.Describe()}, each " +
            "bound to one recorded disposition. The read was performed by this host under a read-only connection; no generated " +
            "statement was executed and the generated application was given no target and no credential.");

        string summary = $"Target contract verification: {EntryVerificationCoverage.Describe(record)}";

        // A run that generated under a ledger, was granted a claim, and opened the approved target has to
        // come back with at least one executed case. Zero cases is not a clean verification: every case
        // this run planned turned into a gap, so nothing was demonstrated about any recorded decision and
        // the ledger would refuse the record outright. Reporting it as a success made the phase read as
        // "verified" and let the deployment below it publish on the strength of a read that proved
        // nothing — the false clean result this phase exists to prevent.
        if (cases.Count == 0)
        {
            return new PhaseExecutionResult(
                false,
                [artifact],
                [
                    summary,
                    .. gaps.Take(MaxReportedGaps).Select(gap => gap.EntryId is { Length: > 0 } entry
                        ? $"{entry} on '{gap.ObjectPath}' in '{gap.ModulePath}': {gap.Reason}"
                        : gap.Reason),
                ],
                $"No case was executed against {claim.ApprovedTarget.Describe()} for any recorded decision, so this run " +
                $"demonstrated nothing about the migrated target. {gaps.Length.ToString(CultureInfo.InvariantCulture)} " +
                "gap(s) are recorded above and every property they name stays unexecuted.");
        }

        // Passing cases are not a verification unless they are all of the cases the run planned. A block's
        // table existing while the probe of whether this run's identity may read it never came back leaves
        // the property half-asked, and a record the ledger will refuse for that reason must not read as a
        // clean phase first — that ordering is what let a deployment publish underneath an incomplete read.
        IReadOnlyList<string> incomplete = EntryVerificationCoverage.Audit(record);
        if (incomplete.Count > 0)
        {
            int missing = record.Gaps.Count(gap => gap.Kind == EntryVerificationGapKind.PlannedCaseUnanswered);

            return new PhaseExecutionResult(
                false,
                [artifact],
                [summary, .. incomplete.Take(MaxReportedGaps)],
                $"This verification did not account for every case it planned against {claim.ApprovedTarget.Describe()}, so no " +
                $"property in it is completely covered and none may be recorded as verified. {missing.ToString(CultureInfo.InvariantCulture)} " +
                "planned case(s) went unanswered.");
        }

        return failed == 0
            ? PhaseExecutionResult.Success([artifact], [summary])
            : new PhaseExecutionResult(
                false,
                [artifact],
                [
                    summary,
                    .. cases.Where(item => item.Outcome == DispositionVerificationStatus.Failed)
                        .Select(item =>
                            $"{item.TestId} on '{item.ObjectPath}' in '{item.ModulePath}': expected {item.Expected}, " +
                            $"observed {item.Actual ?? "nothing"}."),
                ],
                $"{failed.ToString(CultureInfo.InvariantCulture)} recorded decision(s) did not hold in the approved target.");
    }

    private static string Digest(PhaseExecutionContext context, string outputRoot, IReadOnlyList<string> files)
    {
        List<(string Path, string ContentSha256)> hashed = [];
        foreach (string relative in files)
        {
            hashed.Add((relative, context.Workspace.Sha256($"{outputRoot}/{relative}", MaxTextBytes)));
        }

        return GenerationCoverage.OutputSetDigest(hashed);
    }

    /// <summary>
    /// Reads the mapping and the normalized modules this verification derives its expectations from, or
    /// the reason it could not.
    /// </summary>
    private static (TargetMapping? Mapping, IReadOnlyList<FormsModule> Modules, string? Refusal) ReadSourceModel(
        PhaseExecutionContext context,
        string outputRoot)
    {
        string sourceRoot = WorkspacePath.Normalize(context.SourceRoot);
        string manifestPath = $"{sourceRoot}/{TargetMappingReader.ConventionalPath}";
        string irPath = $"{outputRoot}/intermediate/forms-ir.json";

        if (!context.Workspace.FileExists(manifestPath) || !context.Workspace.FileExists(irPath))
        {
            return (null, [], "This run generated under a ledger, and the declared target mapping or the normalized Forms " +
                "representation it was generated from is no longer readable in this workspace. Nothing was read, because " +
                "the expectations would otherwise have to come out of the generated output itself.");
        }

        try
        {
            FormsIntermediateRead read = FormsIntermediateReader.Read(
                context.Workspace.ReadText(irPath, FormsIntermediateReader.MaxDocumentBytes), sourceRoot);

            if (read.Modules is not { } modules)
            {
                return (null, [], $"The normalized Forms representation was refused, so no expectation was derived from it: {read.Error}");
            }

            List<OracleSchema> schemas = [];
            foreach (WorkspaceFile file in context.Workspace.EnumerateFiles(sourceRoot, MaxFiles))
            {
                if (OracleSourceFile.IsSqlText(file.RelativePath))
                {
                    schemas.Add(OracleSchemaParser.Parse(context.Workspace.ReadText(file.RelativePath, MaxTextBytes)));
                }
            }

            if (schemas.Count == 0)
            {
                return (null, [], "No parsed Oracle schema remains under this run's source root, so the target shape a carried " +
                    "property should have could not be re-derived from the source.");
            }

            TargetMappingRead mapping = TargetMappingReader.Read(
                context.Workspace.ReadText(manifestPath, TargetMappingReader.MaxManifestBytes),
                OracleSchema.Merge(schemas),
                modules);

            return mapping.Mapping is { } validated
                ? (validated, modules, null)
                : (null, [], "The declared target mapping no longer resolves against this run's source, so no expectation was " +
                    $"derived from it: {string.Join(" ", mapping.Rejections.Take(5))}");
        }
        catch (Exception exception) when (exception is WorkspaceLimitExceededException or WorkspacePathException or IOException or UnauthorizedAccessException)
        {
            return (null, [], $"The source model could not be read for verification ({FailureText.Describe(exception)}).");
        }
    }

    private static string Scrub(string? text) =>
        text is { Length: > 0 } && FleetGuardrails.ContainsPotentialSecret(text)
            ? "[Withheld because it may contain credential material.]"
            : text ?? string.Empty;
}
