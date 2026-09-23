// Copyright (c) Microsoft. All rights reserved.

using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using OracleFormsMigrationFleet.Fleet;
using OracleFormsMigrationFleet.Fleet.Execution;
using OracleFormsMigrationFleet.Fleet.Execution.Adapters;
using OracleFormsMigrationFleet.Hosting;

namespace OracleFormsMigrationFleet.Tests;

/// <summary>
/// Per-entry target verification, end to end over the real pipeline.
///
/// Every case here normalizes a real export, projects real ledger rows from it, records real decisions,
/// generates under a real server authorization, records that generation durably, runs the real read-only
/// verification adapter against a stubbed approved target, and offers the result to the real ledger
/// service. Nothing hand-writes a ledger row, a coverage record, or a test reference, because the property
/// under test is that a property reads back verified only when something was read from the approved
/// target about the decision that stands.
/// </summary>
public sealed class EntryRuntimeVerificationTests : IDisposable
{
    private const string Operator = "migration-operator@contoso.com";
    private const string Tenant = "8f1e2b42-6a1d-4a24-9ad2-6c1b6a8f3d71";
    private const string SourceRoot = "legacy/forms";
    private const string OutputRoot = ".fleet-run/out";
    private const string IrPath = $"{OutputRoot}/intermediate/forms-ir.json";
    private const string ManifestPath = $"{SourceRoot}/{TargetMappingReader.ConventionalPath}";
    private const string GenerationCoveragePath = $"{OutputRoot}/{GenerationCoverage.RecordPath}";
    private const string VerificationPath = $"{OutputRoot}/{EntryVerificationCoverage.RecordPath}";
    private const string RunId = "run-1";
    private const string ProfileId = "sandbox";

    private const string FormsExport = """
        <?xml version="1.0" encoding="UTF-8"?>
        <Module xmlns="http://xmlns.oracle.com/Forms" version="12.2.1.4" FormsVersion="12.2.1.4">
          <FormModule Name="ORDER_ENTRY" Title="Order entry">
            <Trigger Name="WHEN-NEW-FORM-INSTANCE" TriggerText="BEGIN EXECUTE_QUERY; END;"/>
            <Block Name="ORDER_BLOCK" QueryDataSourceName="MRD_ORDER_HEAD" RecordsDisplayCount="10">
              <Item Name="ORD_NO" ItemType="Text Item" DataType="Number" ColumnName="ORD_NO" Prompt="Order" Required="true"/>
            </Block>
          </FormModule>
        </Module>
        """;

    private static readonly JsonSerializerOptions RecordJson = new()
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "ofmf-entry-verification", Guid.NewGuid().ToString("n"));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    [Fact]
    public void A_declared_target_type_is_compared_the_way_the_server_spells_it()
    {
        Assert.Equal("character varying(30)", EntryVerificationCoverage.CanonicalType("varchar(30)"));
        Assert.Equal("character(1)", EntryVerificationCoverage.CanonicalType("char(1)"));
        Assert.Equal("numeric(12,2)", EntryVerificationCoverage.CanonicalType("numeric(12, 2)"));
        Assert.Equal("timestamp without time zone", EntryVerificationCoverage.CanonicalType("timestamp"));
        Assert.Equal("bigint", EntryVerificationCoverage.CanonicalType("int8"));

        // A narrowed length stays a difference rather than being rounded away by the normalisation.
        Assert.NotEqual(
            EntryVerificationCoverage.CanonicalType("varchar(30)"),
            EntryVerificationCoverage.CanonicalType("varchar(10)"));
    }

    [Fact]
    public void A_name_this_verifier_will_not_ask_a_catalog_about_is_refused()
    {
        Assert.True(EntryVerificationCoverage.IsVerifiableIdentifier("mrd_order_head"));
        Assert.False(EntryVerificationCoverage.IsVerifiableIdentifier("mrd\"; drop table x --"));
        Assert.False(EntryVerificationCoverage.IsVerifiableIdentifier("MRD_ORDER_HEAD"));
        Assert.False(EntryVerificationCoverage.IsVerifiableIdentifier(null));
        Assert.False(EntryVerificationCoverage.IsVerifiableIdentifier(string.Empty));
    }

    [Fact]
    public void A_planned_case_the_verifier_never_answered_becomes_a_gap_and_not_a_pass()
    {
        EntryVerificationExpectation expectation = new(
            "entry-a", "rev-a", "legacy/forms/ui/ORDER_ENTRY.xml", "obj/1", "#object",
            EntryVerificationCaseKind.TargetTableExists, "mrd_order_head", null, "mrd_order_head");

        (IReadOnlyList<EntryVerificationCase> cases, IReadOnlyList<EntryVerificationGap> gaps) =
            EntryVerificationCoverage.Join([expectation], []);

        Assert.Empty(cases);
        EntryVerificationGap gap = Assert.Single(gaps);
        Assert.Equal("entry-a", gap.EntryId);
        Assert.Contains("no executed result", gap.Reason, StringComparison.Ordinal);

        // An unanswered probe is a shortfall against the plan, not a statement that the run never asked.
        // The two are separated because only the first may block a result from being recorded.
        Assert.Equal(EntryVerificationGapKind.PlannedCaseUnanswered, gap.Kind);
        Assert.Equal(expectation.TestId, gap.PlannedTestId);
    }

    /// <summary>
    /// The distinction the whole repair rests on. A property this generator never re-expresses is a gap
    /// the run planned to leave, so a structurally valid property is free to record alongside it. A probe
    /// that was planned and never answered is a different thing and must block.
    /// </summary>
    [Fact]
    public async Task A_property_this_run_never_probed_is_a_stated_gap_and_not_a_shortfall_against_the_plan()
    {
        Harness harness = await HarnessAsync();

        EntryVerificationCoverage.VerificationPlan plan =
            EntryVerificationCoverage.Plan(harness.Mapping, harness.Modules, harness.Coverage().CoveredEntries);

        Assert.NotEmpty(plan.Gaps);
        Assert.All(plan.Gaps, gap =>
        {
            Assert.Equal(EntryVerificationGapKind.NotProbed, gap.Kind);
            Assert.Null(gap.PlannedTestId);
        });
    }

    [Fact]
    public void An_unexecuted_observation_is_a_gap_and_never_a_recorded_result()
    {
        EntryVerificationExpectation expectation = new(
            "entry-a", "rev-a", "legacy/forms/ui/ORDER_ENTRY.xml", "obj/1", "#object",
            EntryVerificationCaseKind.TargetTableExists, "mrd_order_head", null, "mrd_order_head");

        (IReadOnlyList<EntryVerificationCase> cases, IReadOnlyList<EntryVerificationGap> gaps) =
            EntryVerificationCoverage.Join(
                [expectation],
                [new EntryVerificationObservation(
                    "entry-a", expectation.TestId, DispositionVerificationStatus.NotExecuted, null, "nothing ran")]);

        Assert.Empty(cases);
        Assert.Contains(gaps, gap => gap.Reason.Contains("nothing ran", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_retired_property_is_never_planned_as_a_case_that_could_pass()
    {
        Harness harness = await HarnessAsync();
        GenerationCoverageRecord covered = harness.Coverage();

        EntryVerificationCoverage.VerificationPlan plan =
            EntryVerificationCoverage.Plan(harness.Mapping, harness.Modules, covered.CoveredEntries);

        Assert.NotEmpty(plan.Expectations);

        HashSet<string> retired =
        [
            .. covered.CoveredEntries
                .Where(entry => entry.Decision == DispositionDecision.Retire)
                .Select(entry => entry.EntryId),
        ];

        Assert.NotEmpty(retired);
        Assert.DoesNotContain(plan.Expectations, expectation => retired.Contains(expectation.EntryId));

        // Every case names a decision the operator actually recorded as carried forward.
        Assert.All(plan.Expectations, expectation => Assert.Contains(
            covered.CoveredEntries,
            entry => entry.EntryId == expectation.EntryId &&
                entry.Decision is DispositionDecision.Preserve or DispositionDecision.Transform &&
                entry.DecisionRevision == expectation.DecisionRevision));

        // The absence of a runtime service is stated, not left for a reader to infer from passing cases.
        Assert.Contains(plan.Gaps, gap => gap.Reason == EntryVerificationCoverage.RuntimeServiceGap);
    }

    [Fact]
    public async Task A_run_that_generated_under_no_ledger_reads_no_database_at_all()
    {
        Harness harness = await HarnessAsync();
        File.Delete(harness.Absolute(GenerationCoveragePath));
        StubVerifier verifier = harness.Verifier();

        PhaseExecutionResult result = await harness.VerifyAsync(verifier);

        // No coverage record means no recorded decision a result could be about, so the approved target is
        // never opened and nothing is written.
        Assert.True(result.Succeeded, result.FailureReason);
        Assert.Null(verifier.Seen);
        Assert.False(File.Exists(harness.Absolute(VerificationPath)));
        Assert.Contains(result.Findings, finding =>
            finding.Contains("generated under no disposition ledger", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_run_whose_sandbox_migration_did_not_execute_reads_no_database_at_all()
    {
        Harness harness = await HarnessAsync();
        StubVerifier verifier = harness.Verifier();

        PhaseExecutionResult result = await harness.VerifyAsync(
            verifier, migrationState: PhaseExecutionState.SkippedByPlanner);

        // Without the migration there is no migrated target to read, and the only way to make one would be
        // for this process to run the generated DDL itself.
        Assert.False(result.Succeeded);
        Assert.Null(verifier.Seen);
        Assert.Contains("sandbox data migration did not complete", result.FailureReason!, StringComparison.Ordinal);
        Assert.False(File.Exists(harness.Absolute(VerificationPath)));
    }

    [Fact]
    public async Task A_ledger_bound_run_with_no_trusted_verifier_fails_rather_than_passing()
    {
        Harness harness = await HarnessAsync();

        PhaseExecutionResult result = await harness.VerifyAsync(gateway: null);

        Assert.False(result.Succeeded);
        Assert.Contains("no read-only target verification gateway", result.FailureReason!, StringComparison.Ordinal);
        Assert.False(File.Exists(harness.Absolute(VerificationPath)));
    }

    /// <summary>
    /// The check that stands between a run and a customer database. Without a generation the server has
    /// recorded, the phase has nothing a result could attach to, so it must not connect at all.
    /// </summary>
    [Fact]
    public async Task A_generation_the_server_never_recorded_is_never_probed_against_a_database()
    {
        Harness harness = await HarnessAsync(recordGeneration: false);
        StubVerifier verifier = harness.Verifier();

        PhaseExecutionResult result = await harness.VerifyAsync(verifier);

        Assert.False(result.Succeeded);
        Assert.Null(verifier.Seen);
        Assert.Contains("granted no claim", result.FailureReason!, StringComparison.Ordinal);
        Assert.False(File.Exists(harness.Absolute(VerificationPath)));
    }

    /// <summary>
    /// A worker a newer claim displaced is refused before it reads, not after. The earlier design only
    /// caught it at recording time, by which point a customer database had already been opened.
    /// </summary>
    [Fact]
    public async Task A_displaced_worker_is_refused_before_the_target_is_read()
    {
        Harness harness = await HarnessAsync();
        StubVerifier verifier = harness.Verifier();

        PhaseExecutionResult result = await harness.VerifyAsync(verifier, claimFence: harness.Fence + 1);

        Assert.False(result.Succeeded);
        Assert.Null(verifier.Seen);
        Assert.Contains("no longer held by the worker", result.FailureReason!, StringComparison.Ordinal);
    }

    /// <summary>
    /// The verifier answers about the destination the project approved, and about no other. A host wired
    /// somewhere else is a second target nobody approved, so the phase refuses rather than reading it.
    /// </summary>
    [Fact]
    public async Task A_verifier_wired_to_a_target_the_profile_does_not_name_reads_nothing()
    {
        Harness harness = await HarnessAsync();
        StubVerifier elsewhere = new(harness.ApprovedTarget with
        {
            EndpointHost = "pg-somewhere-else.postgres.database.azure.com",
        });

        PhaseExecutionResult result = await harness.VerifyAsync(elsewhere);

        Assert.False(result.Succeeded);
        Assert.Null(elsewhere.Seen);
        Assert.Contains("approved target profile names", result.FailureReason!, StringComparison.Ordinal);
        Assert.False(File.Exists(harness.Absolute(VerificationPath)));
    }

    [Fact]
    public async Task A_run_not_bound_to_the_exact_accepted_profile_reads_nothing()
    {
        Harness harness = await HarnessAsync(bindExactTargetProfile: false);
        StubVerifier verifier = harness.Verifier();

        PhaseExecutionResult result = await harness.VerifyAsync(verifier);

        Assert.False(result.Succeeded);
        Assert.Null(verifier.Seen);
        Assert.Contains("exact profile this run was accepted against", result.FailureReason!, StringComparison.Ordinal);
        Assert.False(File.Exists(harness.Absolute(VerificationPath)));
    }

    /// <summary>
    /// Nothing generated reaches the target. The phase asks a catalog about names it derived from the
    /// source, so the generated DDL is not an input to it and its absence changes nothing.
    /// </summary>
    [Fact]
    public async Task No_generated_statement_is_an_input_to_the_verification()
    {
        Harness harness = await HarnessAsync();
        StubVerifier verifier = harness.Verifier();
        PhaseExecutionResult result = await harness.VerifyAsync(verifier);

        Assert.True(result.Succeeded, result.FailureReason);
        Assert.NotNull(verifier.Seen);
        Assert.NotEmpty(verifier.Seen!);

        // Every case names an object and, at most, a column. There is nowhere in the contract for a
        // statement to travel, and the interface has no parameter that could carry one.
        Assert.All(verifier.Seen!, expectation =>
        {
            Assert.True(EntryVerificationCoverage.IsVerifiableIdentifier(expectation.Table));
            Assert.True(expectation.Column is null || EntryVerificationCoverage.IsVerifiableIdentifier(expectation.Column));
        });
    }

    [Fact]
    public async Task A_case_that_executed_and_passed_makes_exactly_its_own_property_verified()
    {
        Harness harness = await HarnessAsync();
        await harness.VerifyAsync(harness.Verifier());

        PlatformResult<DispositionLedgerVerification> recorded = await harness.RecordVerificationAsync();
        Assert.True(recorded.Succeeded, recorded.Error);
        Assert.True(recorded.Value!.CasesRecorded > 0);
        Assert.Equal(recorded.Value.CasesRecorded, recorded.Value.CasesPassed);
        Assert.True(recorded.Value.GapsDeclared > 0);

        IReadOnlyList<DispositionLedgerEntry> entries = await harness.EntriesAsync();
        HashSet<string> verified =
        [
            .. entries.Where(entry => entry.Verification == DispositionVerificationStatus.Passed).Select(entry => entry.EntryId),
        ];

        Assert.NotEmpty(verified);

        // Only the properties a case was executed for. The other rows generated in the same run, under the
        // same artifact set, stay unexecuted: one artifact set is not one result per property.
        EntryVerificationCoverageRecord record = harness.Verification();
        Assert.Equal(
            record.Cases.Select(item => item.EntryId).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal),
            verified.Order(StringComparer.Ordinal));

        Assert.Contains(entries, entry => entry.Verification == DispositionVerificationStatus.NotExecuted);
    }

    [Fact]
    public async Task A_failed_case_persists_and_keeps_the_ledger_incomplete()
    {
        Harness harness = await HarnessAsync();
        await harness.VerifyAsync(harness.Verifier(failEvery: true));

        PlatformResult<DispositionLedgerVerification> recorded = await harness.RecordVerificationAsync();
        Assert.True(recorded.Succeeded, recorded.Error);
        Assert.Equal(0, recorded.Value!.CasesPassed);
        Assert.True(recorded.Value.CasesFailed > 0);

        IReadOnlyList<DispositionLedgerEntry> entries = await harness.EntriesAsync();
        Assert.Contains(entries, entry => entry.Verification == DispositionVerificationStatus.Failed);

        DispositionCompletionState completion =
            DispositionLedgerRules.Completion(DispositionLedgerRules.Count(entries));
        Assert.False(completion.CanComplete);
        Assert.Contains(completion.Blockers, blocker =>
            blocker.Contains("executed test that failed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_result_naming_a_disposition_the_operator_has_since_moved_off_is_refused()
    {
        Harness harness = await HarnessAsync();
        await harness.VerifyAsync(harness.Verifier());

        // The operator re-decides one of the verified properties. The result was proved about the earlier
        // decision, so it is no longer a statement about the row as it stands.
        EntryVerificationCoverageRecord record = harness.Verification();
        string moved = record.Cases[0].EntryId;
        await harness.RedecideAsync(moved);

        PlatformResult<DispositionLedgerVerification> recorded = await harness.RecordVerificationAsync();

        Assert.False(recorded.Succeeded);
        Assert.Equal(409, recorded.Status);
        Assert.Contains("decision nobody holds any more", recorded.Error!, StringComparison.Ordinal);
        Assert.DoesNotContain(
            await harness.EntriesAsync(),
            entry => entry.Verification == DispositionVerificationStatus.Passed);
    }

    [Fact]
    public async Task A_result_executed_against_output_that_has_since_changed_is_refused()
    {
        Harness harness = await HarnessAsync();
        await harness.VerifyAsync(harness.Verifier());

        // One emitted byte changes. The digest the cases name stops being what the run's output digests
        // to, so they were executed against output this run no longer holds.
        string emitted = harness.Absolute($"{OutputRoot}/application/mapping-manifest.json");
        await File.AppendAllTextAsync(emitted, "\n");

        PlatformResult<DispositionLedgerVerification> recorded = await harness.RecordVerificationAsync();

        Assert.False(recorded.Succeeded);
        Assert.Equal(409, recorded.Status);
        Assert.Contains("different generated content", recorded.Error!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_worker_a_newer_claim_displaced_records_nothing()
    {
        Harness harness = await HarnessAsync();
        await harness.VerifyAsync(harness.Verifier());

        PlatformResult<DispositionLedgerVerification> recorded =
            await harness.RecordVerificationAsync(new MigrationRunOwnership(harness.Fence + 1, TimeSpan.FromMinutes(5)));

        Assert.False(recorded.Succeeded);
        Assert.Equal(409, recorded.Status);
        Assert.Contains("no longer held by the worker", recorded.Error!, StringComparison.Ordinal);
        Assert.DoesNotContain(
            await harness.EntriesAsync(),
            entry => entry.Verification == DispositionVerificationStatus.Passed);
    }

    [Fact]
    public async Task A_record_that_no_longer_matches_its_retained_digest_is_refused()
    {
        Harness harness = await HarnessAsync();
        await harness.VerifyAsync(harness.Verifier());

        IReadOnlyList<MigrationRunArtifact> manifest = await harness.ManifestAsync();
        await File.AppendAllTextAsync(harness.Absolute(VerificationPath), " ");

        PlatformResult<DispositionLedgerVerification> recorded = await harness.RecordVerificationAsync(manifest: manifest);

        Assert.False(recorded.Succeeded);
        Assert.Equal(409, recorded.Status);
        Assert.Contains("no longer matches", recorded.Error!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_case_offered_for_a_retired_property_is_refused_outright()
    {
        Harness harness = await HarnessAsync();
        await harness.VerifyAsync(harness.Verifier());

        // A record that claims a case for a property the operator retired. The row is real, the digest is
        // real, and the case is still refused: a retirement read back as a pass is the false clean result
        // this whole path exists to prevent.
        EntryVerificationCoverageRecord record = harness.Verification();
        DispositionLedgerEntry retired = (await harness.EntriesAsync())
            .First(entry => entry.Decision == DispositionDecision.Retire);

        EntryVerificationCase borrowed = record.Cases[0] with
        {
            EntryId = retired.EntryId,
            DecisionRevision = retired.DecisionRevision,
            ModulePath = retired.Identity.FilePath,
            ObjectPath = retired.Identity.ObjectPath,
            PropertyName = retired.Identity.PropertyName,
        };

        await harness.RewriteVerificationAsync(record with { Cases = [.. record.Cases, borrowed] });

        PlatformResult<DispositionLedgerVerification> recorded = await harness.RecordVerificationAsync();

        Assert.False(recorded.Succeeded);
        Assert.Equal(409, recorded.Status);
        Assert.Contains("not a decision to carry it into the target", recorded.Error!, StringComparison.Ordinal);
        Assert.DoesNotContain(
            await harness.EntriesAsync(),
            entry => entry.Verification == DispositionVerificationStatus.Passed);
    }

    [Fact]
    public async Task A_case_whose_identity_does_not_match_the_row_it_claims_is_refused()
    {
        Harness harness = await HarnessAsync();
        await harness.VerifyAsync(harness.Verifier());

        EntryVerificationCoverageRecord record = harness.Verification();
        await harness.RewriteVerificationAsync(record with
        {
            Cases = [record.Cases[0] with { ObjectPath = "some/other/object" }],
        });

        PlatformResult<DispositionLedgerVerification> recorded = await harness.RecordVerificationAsync();

        Assert.False(recorded.Succeeded);
        Assert.Contains("two different properties", recorded.Error!, StringComparison.Ordinal);
    }

    /// <summary>
    /// One verification is one fact about one output set, so a record whose last case will not bind leaves
    /// the first case unrecorded too. The per-entry loop this replaced could enter half of them.
    /// </summary>
    [Fact]
    public async Task One_unbindable_case_leaves_every_other_case_in_the_record_unrecorded()
    {
        Harness harness = await HarnessAsync();
        await harness.VerifyAsync(harness.Verifier());

        EntryVerificationCoverageRecord record = harness.Verification();
        Assert.True(record.Cases.Select(item => item.EntryId).Distinct(StringComparer.Ordinal).Count() > 1);

        // Everything stands except the very last case, which names a disposition of its property that was
        // never recorded.
        await harness.RewriteVerificationAsync(record with
        {
            Cases =
            [
                .. record.Cases.Take(record.Cases.Count - 1),
                record.Cases[^1] with { DecisionRevision = new string('9', 64) },
            ],
        });

        PlatformResult<DispositionLedgerVerification> recorded = await harness.RecordVerificationAsync();

        Assert.False(recorded.Succeeded);
        Assert.DoesNotContain(
            await harness.EntriesAsync(),
            entry => entry.Verification != DispositionVerificationStatus.NotExecuted);
    }

    /// <summary>
    /// Where a result was read matters as much as what it said. A record naming a destination the
    /// project's immutable profile does not is not evidence about the approved target.
    /// </summary>
    [Fact]
    public async Task A_result_read_from_a_target_the_profile_does_not_name_is_refused()
    {
        Harness harness = await HarnessAsync();
        await harness.VerifyAsync(harness.Verifier());

        EntryVerificationCoverageRecord record = harness.Verification();
        await harness.RewriteVerificationAsync(record with
        {
            Target = record.Target with { DatabaseName = "some-other-database" },
        });

        PlatformResult<DispositionLedgerVerification> recorded = await harness.RecordVerificationAsync();

        Assert.False(recorded.Succeeded);
        Assert.Equal(409, recorded.Status);
        Assert.Contains("approved target profile does not name", recorded.Error!, StringComparison.Ordinal);
        Assert.DoesNotContain(
            await harness.EntriesAsync(),
            entry => entry.Verification == DispositionVerificationStatus.Passed);
    }

    [Fact]
    public async Task A_run_with_no_executed_verification_record_records_nothing()
    {
        Harness harness = await HarnessAsync();

        // The aggregate report exists and the generation is recorded; neither of those is a result about a
        // property, so the ledger is offered nothing.
        PlatformResult<DispositionLedgerVerification> recorded =
            await harness.RecordVerificationAsync(manifest: await harness.ManifestAsync());

        Assert.False(recorded.Succeeded);
        Assert.Contains("retained no per-entry verification record", recorded.Error!, StringComparison.Ordinal);
    }

    /// <summary>
    /// The refusal that used to be missing. A run that generated under a ledger, was granted a claim, and
    /// opened the approved target has to come back with at least one executed case. Zero cases means every
    /// planned case turned into a gap, so nothing was demonstrated about any recorded decision — and the
    /// ledger would refuse the record anyway, which under the old ordering only happened after the
    /// deployment phase had already published.
    /// </summary>
    [Fact]
    public async Task A_read_that_executed_no_case_at_all_fails_the_phase_rather_than_passing_it()
    {
        Harness harness = await HarnessAsync();
        SilentVerifier silent = new(harness.ApprovedTarget);

        PhaseExecutionResult result = await harness.VerifyAsync(silent);

        Assert.False(result.Succeeded);
        Assert.NotNull(silent.Seen);
        Assert.NotEmpty(silent.Seen!);
        Assert.Contains("No case was executed", result.FailureReason!, StringComparison.Ordinal);

        // The record is still written, because the gaps it states are the outcome and have to stay visible.
        Assert.True(File.Exists(harness.Absolute(VerificationPath)));
        EntryVerificationCoverageRecord record = harness.Verification();
        Assert.Empty(record.Cases);
        Assert.NotEmpty(record.Gaps);
    }

    /// <summary>
    /// The same zero-case record offered to the ledger. Both gates hold independently: the phase refuses,
    /// and the ledger refuses, so nothing reads back verified on the strength of a read that proved nothing.
    /// </summary>
    [Fact]
    public async Task A_zero_case_record_is_refused_by_the_ledger_and_leaves_every_property_unexecuted()
    {
        Harness harness = await HarnessAsync();
        await harness.VerifyAsync(new SilentVerifier(harness.ApprovedTarget));

        PlatformResult<DispositionLedgerVerification> recorded = await harness.RecordVerificationAsync();

        Assert.False(recorded.Succeeded);
        Assert.Equal(409, recorded.Status);
        Assert.Contains("describes no executed case", recorded.Error!, StringComparison.Ordinal);
        Assert.All(
            await harness.EntriesAsync(),
            entry => Assert.Equal(DispositionVerificationStatus.NotExecuted, entry.Verification));
    }

    /// <summary>
    /// A verifier reached through a grant that no longer holds throws rather than answering. The phase
    /// reports the refusal as its own and writes no record, so nothing is offered to the ledger.
    /// </summary>
    [Fact]
    public async Task A_read_refused_by_the_grant_fails_the_phase_and_writes_no_record()
    {
        Harness harness = await HarnessAsync();

        PhaseExecutionResult result = await harness.VerifyAsync(
            new RefusingVerifier(harness.ApprovedTarget, "The grant for this run was revoked."));

        Assert.False(result.Succeeded);
        Assert.Contains("did not hold at the moment", result.FailureReason!, StringComparison.Ordinal);
        Assert.Contains("revoked", result.FailureReason!, StringComparison.Ordinal);
        Assert.False(File.Exists(harness.Absolute(VerificationPath)));
    }

    /// <summary>
    /// The repair. A block's table is asked two things — that it exists, and that this run's identity may
    /// read it — and only the first comes back. Under the earlier design the phase saw no failed case and
    /// reported success, and the ledger saw one passing result and moved the property to verified, so a
    /// property whose table the migrated application may not be able to query read back as proved and the
    /// deployment below it published.
    /// </summary>
    [Fact]
    public async Task One_of_two_planned_probes_on_a_property_going_unanswered_fails_the_phase()
    {
        Harness harness = await HarnessAsync();
        PartialVerifier partial = new(harness.ApprovedTarget, EntryVerificationCaseKind.TargetTableReadable);

        PhaseExecutionResult result = await harness.VerifyAsync(partial);

        Assert.NotNull(partial.Withheld);
        Assert.False(result.Succeeded);
        Assert.Contains("did not account for every case it planned", result.FailureReason!, StringComparison.Ordinal);

        // The record is still written: the unanswered probe is the outcome and has to stay visible. The
        // sibling probe passed, which is exactly the shape that used to read as a clean verification.
        // A case identity is the pair, because two properties on one block share a table and so a TestId.
        EntryVerificationCoverageRecord record = harness.Verification();
        Assert.Contains(record.Cases, item =>
            item.EntryId == partial.WithheldEntryId &&
            item.Kind == EntryVerificationCaseKind.TargetTableExists &&
            item.Outcome == DispositionVerificationStatus.Passed);
        Assert.DoesNotContain(record.Cases, item =>
            item.EntryId == partial.WithheldEntryId && item.TestId == partial.Withheld);
        Assert.Contains(record.Gaps, gap =>
            gap.Kind == EntryVerificationGapKind.PlannedCaseUnanswered &&
            gap.EntryId == partial.WithheldEntryId &&
            gap.PlannedTestId == partial.Withheld);
        Assert.Contains(record.Planned, item =>
            item.EntryId == partial.WithheldEntryId && item.TestId == partial.Withheld);
    }

    /// <summary>
    /// The same partly answered record offered to the ledger. Both gates hold independently, and no
    /// property — not even one whose own probes all came back — reads verified, because one verification
    /// is one fact about one output set.
    /// </summary>
    [Fact]
    public async Task A_partly_answered_record_records_no_verified_disposition_at_all()
    {
        Harness harness = await HarnessAsync();
        await harness.VerifyAsync(new PartialVerifier(harness.ApprovedTarget, EntryVerificationCaseKind.TargetTableReadable));

        PlatformResult<DispositionLedgerVerification> recorded = await harness.RecordVerificationAsync();

        Assert.False(recorded.Succeeded);
        Assert.Equal(409, recorded.Status);
        Assert.Contains("every case it planned", recorded.Error!, StringComparison.Ordinal);
        Assert.All(
            await harness.EntriesAsync(),
            entry => Assert.Equal(DispositionVerificationStatus.NotExecuted, entry.Verification));
    }

    /// <summary>
    /// A record made to look complete by deleting the evidence of what went missing: the result is gone
    /// and so is the gap that stated it, leaving only passing cases. The plan it stated is what catches
    /// it, which is why the plan is written down before anything answers.
    /// </summary>
    [Fact]
    public async Task A_record_that_deletes_its_own_unanswered_probe_to_look_complete_is_refused()
    {
        Harness harness = await HarnessAsync();
        await harness.VerifyAsync(harness.Verifier());

        EntryVerificationCoverageRecord record = harness.Verification();
        EntryVerificationCase dropped = record.Cases.First(item => item.Kind == EntryVerificationCaseKind.TargetTableReadable);

        await harness.RewriteVerificationAsync(record with
        {
            Cases = [.. record.Cases.Where(item => item != dropped)],
        });

        PlatformResult<DispositionLedgerVerification> recorded = await harness.RecordVerificationAsync();

        Assert.False(recorded.Succeeded);
        Assert.Equal(409, recorded.Status);
        Assert.Contains("neither reports a result", recorded.Error!, StringComparison.Ordinal);
        Assert.All(
            await harness.EntriesAsync(),
            entry => Assert.Equal(DispositionVerificationStatus.NotExecuted, entry.Verification));
    }

    /// <summary>
    /// The same forgery carried one step further: the plan is trimmed to match what is left, so the record
    /// is internally consistent. The probe pairings this run always plans together are what is left to
    /// catch it, and a table asked whether it exists but never whether it can be read is not a plan this
    /// build produces.
    /// </summary>
    [Fact]
    public async Task A_record_that_shrinks_its_own_plan_to_match_what_ran_is_refused()
    {
        Harness harness = await HarnessAsync();
        await harness.VerifyAsync(harness.Verifier());

        EntryVerificationCoverageRecord record = harness.Verification();
        EntryVerificationCase dropped = record.Cases.First(item => item.Kind == EntryVerificationCaseKind.TargetTableReadable);

        await harness.RewriteVerificationAsync(record with
        {
            Cases = [.. record.Cases.Where(item => item != dropped)],
            Planned =
            [
                .. record.Planned.Where(item =>
                    item.EntryId != dropped.EntryId || item.TestId != dropped.TestId),
            ],
        });

        PlatformResult<DispositionLedgerVerification> recorded = await harness.RecordVerificationAsync();

        Assert.False(recorded.Succeeded);
        Assert.Equal(409, recorded.Status);
        Assert.Contains("this run always plans together", recorded.Error!, StringComparison.Ordinal);
        Assert.All(
            await harness.EntriesAsync(),
            entry => Assert.Equal(DispositionVerificationStatus.NotExecuted, entry.Verification));
    }

    /// <summary>
    /// The forgery the record's own plan cannot catch, because the record it leaves behind is one this
    /// build legitimately produces. A column's shape probe is removed from the plan and from the results
    /// together; what remains is a column asked only whether it exists, which is exactly the plan a
    /// source that resolves no type for the column yields. The record therefore passes its own audit, and
    /// the property would read back verified with the shape it was migrated at never having been looked
    /// at.
    ///
    /// The plan the source actually requires is what catches it: rebuilt from the normalized
    /// representation this ledger was projected from, the mapping the generation was produced against,
    /// and the decisions that stand. That source resolves a type for this column, so the shape probe is
    /// required and its absence is a shortfall, whatever the record says it planned.
    /// </summary>
    [Fact]
    public async Task A_record_that_drops_a_shape_probe_the_source_requires_from_its_plan_and_its_results_is_refused()
    {
        Harness harness = await HarnessAsync();
        await harness.VerifyAsync(harness.Verifier());

        EntryVerificationCoverageRecord record = harness.Verification();
        EntryVerificationCase dropped = record.Cases.First(item => item.Kind == EntryVerificationCaseKind.TargetColumnShape);

        EntryVerificationCoverageRecord forged = record with
        {
            Cases = [.. record.Cases.Where(item => item != dropped)],
            Planned =
            [
                .. record.Planned.Where(item =>
                    item.EntryId != dropped.EntryId || item.TestId != dropped.TestId),
            ],
        };

        // The forgery is internally consistent: every case it states it planned came back, and a column
        // probed only for existence is a family this build produces when the source has no type to ask
        // about. Nothing inside the record distinguishes it from an honest one.
        Assert.Empty(EntryVerificationCoverage.Audit(forged));

        await harness.RewriteVerificationAsync(forged);

        PlatformResult<DispositionLedgerVerification> recorded = await harness.RecordVerificationAsync();

        Assert.False(recorded.Succeeded);
        Assert.Equal(409, recorded.Status);
        Assert.Contains("The source requires case", recorded.Error!, StringComparison.Ordinal);
        Assert.Contains(dropped.TestId, recorded.Error!, StringComparison.Ordinal);

        Assert.All(
            await harness.EntriesAsync(),
            entry =>
            {
                Assert.Equal(DispositionVerificationStatus.NotExecuted, entry.Verification);
                Assert.Empty(entry.TestRefs);
            });
    }

    /// <summary>
    /// A column the source resolves no type for is asked only whether it exists, and that is a complete
    /// plan rather than a trimmed one. The rebuilt plan derives the same single probe from the same
    /// source, so an honest existence-only family still reconciles — the check above must not make an
    /// unresolvable shape indistinguishable from a deleted one.
    /// </summary>
    [Fact]
    public async Task A_column_the_source_resolves_no_shape_for_is_planned_for_existence_alone()
    {
        Harness harness = await HarnessAsync();
        await harness.VerifyAsync(harness.Verifier());

        EntryVerificationCoverageRecord record = harness.Verification();
        EntryVerificationPlannedCase shaped =
            record.Planned.First(item => item.Kind == EntryVerificationCaseKind.TargetColumnShape);

        IReadOnlyList<GenerationCoveredEntry> covered =
        [
            .. (await harness.EntriesAsync())
                .Where(entry => entry.Decision is DispositionDecision.Preserve or DispositionDecision.Transform)
                .Select(entry => new GenerationCoveredEntry(
                    entry.EntryId, entry.DecisionRevision, entry.Decision, entry.MappingRuleId, entry.MappingRuleVersion)),
        ];

        TargetMapping unresolved = harness.Mapping with
        {
            Carried = [.. harness.Mapping.Carried.Select(property => property with { TargetType = null })],
        };

        EntryVerificationCoverage.VerificationPlan plan =
            EntryVerificationCoverage.Plan(unresolved, harness.Modules, covered);

        Assert.DoesNotContain(plan.Expectations, expectation => expectation.Kind == EntryVerificationCaseKind.TargetColumnShape);
        Assert.Contains(plan.Expectations, expectation =>
            expectation.Kind == EntryVerificationCaseKind.TargetColumnExists && expectation.EntryId == shaped.EntryId);

        // The pairing rule reads that family as complete, which is what keeps an unresolvable shape a
        // stated absence rather than a missing probe.
        Assert.Empty(EntryVerificationCoverage.Audit(record with
        {
            Planned = [.. plan.Expectations.Select(expectation => expectation.AsPlanned())],
            Cases = [.. record.Cases.Where(item => item.Kind != EntryVerificationCaseKind.TargetColumnShape)],
            Gaps = [.. record.Gaps.Where(gap => gap.Kind == EntryVerificationGapKind.NotProbed)],
        }));
    }

    /// <summary>
    /// The opposite forgery: a result for a probe the run never asked. It answers a question nothing
    /// derived from the source, so it is not evidence about any decision.
    /// </summary>
    [Fact]
    public async Task A_result_for_a_probe_the_run_never_planned_is_refused()
    {
        Harness harness = await HarnessAsync();
        await harness.VerifyAsync(harness.Verifier());

        EntryVerificationCoverageRecord record = harness.Verification();
        await harness.RewriteVerificationAsync(record with
        {
            Cases = [.. record.Cases, record.Cases[0] with { TestId = "TargetTableExists:some_other_table" }],
        });

        PlatformResult<DispositionLedgerVerification> recorded = await harness.RecordVerificationAsync();

        Assert.False(recorded.Succeeded);
        Assert.Equal(409, recorded.Status);
        Assert.Contains("never planned", recorded.Error!, StringComparison.Ordinal);
    }

    /// <summary>
    /// Completeness is measured against the plan and against nothing else. A run that answered every probe
    /// it planned records normally, even though it states many properties it never probed at all — a
    /// retirement, a name no catalog is asked about, and the standing absence of any executed application
    /// behaviour. Treating those as shortfalls would make a correct verification impossible to record.
    /// </summary>
    [Fact]
    public async Task Gaps_the_run_never_planned_a_probe_for_do_not_block_recording()
    {
        Harness harness = await HarnessAsync();
        await harness.VerifyAsync(harness.Verifier());

        EntryVerificationCoverageRecord record = harness.Verification();
        Assert.Contains(record.Gaps, gap => gap.Kind == EntryVerificationGapKind.NotProbed);
        Assert.DoesNotContain(record.Gaps, gap => gap.Kind == EntryVerificationGapKind.PlannedCaseUnanswered);
        Assert.Equal(record.Planned.Count, record.Cases.Count);
        Assert.Empty(EntryVerificationCoverage.Audit(record));

        PlatformResult<DispositionLedgerVerification> recorded = await harness.RecordVerificationAsync();

        Assert.True(recorded.Succeeded, recorded.Error);
        Assert.True(recorded.Value!.GapsDeclared > 0);
        Assert.Contains(
            await harness.EntriesAsync(),
            entry => entry.Verification == DispositionVerificationStatus.Passed);
    }

    /// <summary>Answers every planned case but one, so one property keeps a passing sibling probe.</summary>
    private sealed class PartialVerifier(EntryVerificationTargetBinding target, EntryVerificationCaseKind withhold)
        : IEntryRuntimeVerificationGateway
    {
        public EntryVerificationTargetBinding Target => target;

        public string? Withheld { get; private set; }

        public string? WithheldEntryId { get; private set; }

        public Task<EntryRuntimeVerificationRun> InspectAsync(
            EntryVerificationTargetBinding approved,
            IReadOnlyList<EntryVerificationExpectation> expectations,
            CancellationToken cancellationToken)
        {
            EntryVerificationExpectation skipped = expectations.First(expectation => expectation.Kind == withhold);
            Withheld = skipped.TestId;
            WithheldEntryId = skipped.EntryId;

            return Task.FromResult(new EntryRuntimeVerificationRun(
                "stub target",
                ToolAvailable: true,
                TimedOut: false,
                SetupFailed: false,
                Failure: null,
                [
                    .. expectations
                        .Where(expectation =>
                            expectation.EntryId != skipped.EntryId || expectation.TestId != skipped.TestId)
                        .Select(expectation => new EntryVerificationObservation(
                            expectation.EntryId,
                            expectation.TestId,
                            DispositionVerificationStatus.Passed,
                            expectation.Expected,
                            "Observed in the approved target.")),
                ]));
        }
    }

    /// <summary>A verifier that is reached, answers nothing, and so leaves every planned case a gap.</summary>
    private sealed class SilentVerifier(EntryVerificationTargetBinding target) : IEntryRuntimeVerificationGateway
    {
        public EntryVerificationTargetBinding Target => target;

        public IReadOnlyList<EntryVerificationExpectation>? Seen { get; private set; }

        public Task<EntryRuntimeVerificationRun> InspectAsync(
            EntryVerificationTargetBinding approved,
            IReadOnlyList<EntryVerificationExpectation> expectations,
            CancellationToken cancellationToken)
        {
            Seen = expectations;
            return Task.FromResult(new EntryRuntimeVerificationRun(
                "stub target", ToolAvailable: true, TimedOut: false, SetupFailed: false, Failure: null, []));
        }
    }

    /// <summary>Stands in for the authorizing wrapper the host puts in front of a real verifier.</summary>
    private sealed class RefusingVerifier(EntryVerificationTargetBinding target, string reason)
        : IEntryRuntimeVerificationGateway
    {
        public EntryVerificationTargetBinding Target => target;

        public Task<EntryRuntimeVerificationRun> InspectAsync(
            EntryVerificationTargetBinding approved,
            IReadOnlyList<EntryVerificationExpectation> expectations,
            CancellationToken cancellationToken) => throw new UnauthorizedAccessException(reason);
    }

    private sealed class StubVerifier(EntryVerificationTargetBinding target, bool failEvery = false)
        : IEntryRuntimeVerificationGateway
    {
        public EntryVerificationTargetBinding Target => target;

        public IReadOnlyList<EntryVerificationExpectation>? Seen { get; private set; }

        public EntryVerificationTargetBinding? Approved { get; private set; }

        public Task<EntryRuntimeVerificationRun> InspectAsync(
            EntryVerificationTargetBinding approved,
            IReadOnlyList<EntryVerificationExpectation> expectations,
            CancellationToken cancellationToken)
        {
            Approved = approved;
            Seen = expectations;

            if (!approved.SameTargetAs(target))
            {
                return Task.FromResult(new EntryRuntimeVerificationRun(
                    "stub target", ToolAvailable: true, TimedOut: false, SetupFailed: true,
                    "The approved target is not the one this verifier is wired to.", []));
            }

            return Task.FromResult(new EntryRuntimeVerificationRun(
                "stub target",
                ToolAvailable: true,
                TimedOut: false,
                SetupFailed: false,
                Failure: null,
                [
                    .. expectations.Select(expectation => new EntryVerificationObservation(
                        expectation.EntryId,
                        expectation.TestId,
                        failEvery ? DispositionVerificationStatus.Failed : DispositionVerificationStatus.Passed,
                        failEvery ? "absent" : expectation.Expected,
                        failEvery ? "The target did not hold it." : "Observed in the approved target.")),
                ]));
        }
    }

    private sealed record Harness(
        string WorkspaceRoot,
        FilePlatformStateStore Store,
        FileMigrationRunStore Runs,
        DispositionLedgerService Ledgers,
        WorkbenchActor Actor,
        string ProjectId,
        string LedgerId,
        long Fence,
        EntryVerificationTargetBinding ApprovedTarget,
        TargetMapping Mapping,
        IReadOnlyList<FormsModule> Modules)
    {
        public string Absolute(string relativePath) =>
            Path.Combine(WorkspaceRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));

        public GenerationCoverageRecord Coverage() =>
            JsonSerializer.Deserialize<GenerationCoverageRecord>(
                File.ReadAllText(Absolute(GenerationCoveragePath)), RecordJson)!;

        public EntryVerificationCoverageRecord Verification() =>
            JsonSerializer.Deserialize<EntryVerificationCoverageRecord>(
                File.ReadAllText(Absolute(VerificationPath)), RecordJson)!;

        public Task RewriteVerificationAsync(EntryVerificationCoverageRecord record) =>
            File.WriteAllTextAsync(Absolute(VerificationPath), JsonSerializer.Serialize(record, RecordJson));

        public StubVerifier Verifier(bool failEvery = false) => new(ApprovedTarget, failEvery);

        public Task<IReadOnlyList<DispositionLedgerEntry>> EntriesAsync() =>
            Store.DispositionLedgerEntriesAsync(Tenant, LedgerId, CancellationToken.None);

        /// <summary>
        /// Runs the phase exactly as the executor does: after a sandbox data migration that executed in
        /// this run, holding the claim this worker made, with the server as the only source of the
        /// generation it may speak about.
        /// </summary>
        public Task<PhaseExecutionResult> VerifyAsync(
            IEntryRuntimeVerificationGateway? gateway,
            long? claimFence = null,
            PhaseExecutionState migrationState = PhaseExecutionState.Executed)
        {
            MigrationRunRequest request = Request();
            PhasePlan plan = MigrationRunPlanner.Plan(request).Phases
                .Single(phase => phase.Phase == MigrationPhase.TargetContractVerification);

            return new TargetContractVerificationAdapter(gateway).ExecuteAsync(
                new PhaseExecutionContext(WorkspaceRoot, SourceRoot, OutputRoot, plan, request, (_, _) => { })
                {
                    CompletedPhases =
                    [
                        new PhaseOutcome(
                            MigrationPhase.SandboxDataMigration, PhaseStatus.Planned, migrationState, [], [], null),
                    ],
                    VerificationProvider = new LedgerEntryVerificationProvider(
                        Ledgers, Tenant, LedgerId, RunId,
                        claimFence is null
                            ? null
                            : new MigrationRunOwnership(claimFence.Value, TimeSpan.FromMinutes(5))),
                },
                CancellationToken.None);
        }

        public async Task<IReadOnlyList<MigrationRunArtifact>> ManifestAsync()
        {
            List<MigrationRunArtifact> artifacts = [];

            foreach (string path in new[] { IrPath, GenerationCoveragePath, VerificationPath })
            {
                string absolute = Absolute(path);
                if (!File.Exists(absolute))
                {
                    continue;
                }

                byte[] bytes = await File.ReadAllBytesAsync(absolute);
                artifacts.Add(new MigrationRunArtifact(
                    RunId, path, "ValidationReport", "Run artifact", bytes.Length,
                    Convert.ToHexStringLower(SHA256.HashData(bytes))));
            }

            return artifacts;
        }

        /// <summary>
        /// Records as an operator would, after the run reached a terminal state. A still-running worker
        /// supplies its claim instead; that path is exercised by the displaced-owner case, which is the
        /// only one the claim changes the answer for.
        /// </summary>
        public async Task<PlatformResult<DispositionLedgerVerification>> RecordVerificationAsync(
            MigrationRunOwnership? ownership = null,
            IReadOnlyList<MigrationRunArtifact>? manifest = null) =>
            await Ledgers.RecordVerificationFromRunAsync(
                Tenant,
                LedgerId,
                RunId,
                manifest ?? await ManifestAsync(),
                ownership,
                CancellationToken.None);

        public async Task RedecideAsync(string entryId)
        {
            DispositionLedgerEntry entry = (await EntriesAsync())
                .Single(candidate => candidate.EntryId == entryId);

            PlatformResult<DispositionLedgerEntry> decided = await Ledgers.DecideAsync(
                Actor,
                LedgerId,
                entryId,
                new DispositionDecisionInput(
                    DispositionDecision.Transform,
                    "Reconsidered: the declared value is carried across as its documented equivalent.",
                    "DR-TRANSFORM-EQUIVALENT",
                    1,
                    entry.Version),
                CancellationToken.None);

            Assert.True(decided.Succeeded, decided.Error);
        }

        public static MigrationRunRequest Request() => new()
        {
            EngagementId = "ENG-VERIFY",
            ApplicationName = "Meridian Order Entry",
            RequestedMode = ExecutionMode.GenerateArtifacts,
            Target = new TargetStack { BackEnd = BackEndStack.AspNetCore, Database = DatabaseTarget.PostgreSql },
            OracleFormsVersion = "12c",
            OracleDatabaseVersion = "19c",
            SourceRoot = SourceRoot,
            OutputRoot = OutputRoot,
        };
    }

    /// <summary>
    /// A normalized run, a ledger projected from what it read, every property decided, the application
    /// tier generated under a server authorization issued over those decisions, and — unless a case is
    /// about what happens without it — that generation recorded durably against the ledger.
    /// </summary>
    private async Task<Harness> HarnessAsync(bool recordGeneration = true, bool bindExactTargetProfile = true)
    {
        string workspacesRoot = Path.Combine(_root, "workspaces");
        string workspaceId = Guid.NewGuid().ToString("N");
        string workspaceRoot = Path.Combine(workspacesRoot, workspaceId);
        Directory.CreateDirectory(workspaceRoot);

        Write(workspaceRoot, $"{SourceRoot}/ui/ORDER_ENTRY.xml", FormsExport);
        Write(workspaceRoot, $"{SourceRoot}/db/schema.sql", DotNetPilotFixtures.MeridianSchema);

        MigrationRunRequest request = Harness.Request();
        PhaseExecutionResult normalized = await new SourceNormalizationAdapter().ExecuteAsync(
            Context(workspaceRoot, request, MigrationPhase.SourceNormalization),
            CancellationToken.None);
        Assert.True(normalized.Succeeded, normalized.FailureReason);

        FormsIntermediateRead read = FormsIntermediateReader.Read(
            await File.ReadAllTextAsync(Path.Combine(workspaceRoot, IrPath.Replace('/', Path.DirectorySeparatorChar))),
            SourceRoot);
        Assert.NotNull(read.Modules);

        FormsModule module = read.Modules!.Single(candidate =>
            candidate.SourcePath!.EndsWith("ORDER_ENTRY.xml", StringComparison.Ordinal));
        FormsSourceFactSet facts = module.SourceFacts!;
        FormsSourceFact block = facts.Facts.First(fact => fact.LocalName == "Block");

        Write(workspaceRoot, ManifestPath, DotNetPilotFixtures.MeridianManifest
            .Replace(
                "\"sources\": [",
                $$"""
                  "sources": [
                              { "id": "frm-block", "kind": "FormsSourceObject", "path": "{{block.Id}}", "module": "{{module.SourcePath}}", "textDigest": "{{facts.TextDigest}}" },
                  """,
                StringComparison.Ordinal)
            .Replace("\"sourceRefs\": [\"sch-head\"]", "\"sourceRefs\": [\"sch-head\", \"frm-block\"]", StringComparison.Ordinal));

        FilePlatformStateStore store = new(Path.Combine(_root, "platform-state.json"));
        await store.InitializeAsync(CancellationToken.None);
        FileMigrationRunStore runs = new(Path.Combine(_root, "runs.json"));
        PlatformAccessService platform = new(store, sandbox: null);
        SourceWorkspaceService workspaces = new(workspacesRoot);
        WorkbenchActor actor = WorkbenchActor.ForTenant(Tenant, Operator, [WorkbenchRoles.MigrationOperator]);

        PlatformResult<PlatformProject> project =
            await platform.CreateProjectAsync(actor, "ORDER_ENTRY migration", CancellationToken.None);
        Assert.True(project.Succeeded, project.Error);

        // The immutable destination the run is accepted against. The verifier is held to exactly these
        // coordinates, so a result read from anywhere else is not evidence about this project's target.
        PlatformTargetProfile declared = new()
        {
            TargetProfileId = ProfileId,
            ProjectId = project.Value!.ProjectId,
            TenantId = Tenant,
            Version = 1,
            AzureTenantId = Tenant,
            SubscriptionId = "00000000-0000-0000-0000-000000000001",
            ResourceGroup = "rg-ofm-sandbox",
            ResourceId = "/subscriptions/00000000-0000-0000-0000-000000000001/rg-ofm-sandbox",
            Region = "eastus2",
            EndpointHost = "pg-ofm-sandbox.postgres.database.azure.com",
            DatabaseName = "postgres",
            SchemaName = "public",
            ExecutionIdentity = "ofm-workbench",
            EnvironmentName = "Sandbox",
            StackDatabase = nameof(DatabaseTarget.PostgreSql),
            StackFrontEnd = "React",
            StackBackEnd = nameof(BackEndStack.AspNetCore),
            CanonicalHash = string.Empty,
            CreatedUtc = DateTimeOffset.UtcNow,
        };
        declared = declared with { CanonicalHash = PlatformTargetProfiles.Hash(declared) };
        Assert.NotNull(await store.CreateTargetProfileAsync(declared, CancellationToken.None));

        EntryVerificationTargetBinding approvedTarget = new(
            declared.EndpointHost, declared.DatabaseName, declared.SchemaName, declared.ExecutionIdentity);

        DispositionLedgerService ledgers = new(store, platform, runs, workspaces);
        string snapshot = new('4', 64);

        byte[] ir = await File.ReadAllBytesAsync(
            Path.Combine(workspaceRoot, IrPath.Replace('/', Path.DirectorySeparatorChar)));
        long fence = await CompleteRunAsync(
            runs,
            project.Value.ProjectId,
            workspaceId,
            snapshot,
            bindExactTargetProfile ? declared.CanonicalHash : new string('c', 64),
            [new MigrationRunArtifact(
                RunId, IrPath, "NormalizedSource", "Normalized Forms representation",
                ir.Length, Convert.ToHexStringLower(SHA256.HashData(ir)))]);

        PlatformResult<DispositionLedgerView> ingested =
            await ledgers.IngestFromRunAsync(actor, project.Value.ProjectId, RunId, CancellationToken.None);
        Assert.True(ingested.Succeeded, ingested.Error);

        string ledgerId = ingested.Value!.Summary.LedgerId;

        // Decided one property at a time: carried forward where the generator re-expresses it, retired
        // where it does not. Retiring is the operator's decision, and it claims nothing about output.
        foreach (DispositionLedgerEntry entry in await store.DispositionLedgerEntriesAsync(Tenant, ledgerId, CancellationToken.None))
        {
            bool carried = entry.Identity.ObjectPath.Contains("Block", StringComparison.Ordinal) &&
                entry.Identity.PropertyName is "#object" or "QueryDataSourceName" or "ColumnName";

            PlatformResult<DispositionLedgerEntry> decided = await ledgers.DecideAsync(
                actor,
                ledgerId,
                entry.EntryId,
                carried
                    ? new DispositionDecisionInput(
                        DispositionDecision.Preserve,
                        "The declared value is carried into the generated application unchanged.",
                        "DR-PRESERVE-DECLARED", 1, entry.Version)
                    : new DispositionDecisionInput(
                        DispositionDecision.Retire,
                        "Oracle Forms runtime machinery with no counterpart in the target stack.",
                        "DR-RETIRE-NO-TARGET", 1, entry.Version),
                CancellationToken.None);

            Assert.True(decided.Succeeded, decided.Error);
        }

        PhaseExecutionResult generated = await new ApplicationCodeConversionAdapter().ExecuteAsync(
            Context(workspaceRoot, request, MigrationPhase.ApplicationCodeConversion) with
            {
                CompletedPhases =
                [
                    new PhaseOutcome(
                        MigrationPhase.SourceNormalization, PhaseStatus.Planned, PhaseExecutionState.Executed, [], [], null),
                ],
                AuthorizationProvider = new LedgerGenerationAuthorizationProvider(ledgers, actor, ledgerId, RunId),
            },
            CancellationToken.None);
        Assert.True(generated.Succeeded, generated.FailureReason + " " + string.Join(" | ", generated.Findings));

        byte[] coverage = await File.ReadAllBytesAsync(
            Path.Combine(workspaceRoot, GenerationCoveragePath.Replace('/', Path.DirectorySeparatorChar)));

        if (recordGeneration)
        {
            PlatformResult<DispositionLedgerGeneration> recorded = await ledgers.RecordGenerationFromRunAsync(
                Tenant,
                ledgerId,
                RunId,
                [
                    new MigrationRunArtifact(
                        RunId, IrPath, "NormalizedSource", "Normalized Forms representation",
                        ir.Length, Convert.ToHexStringLower(SHA256.HashData(ir))),
                    new MigrationRunArtifact(
                        RunId, GenerationCoveragePath, "ValidationReport", "Generation coverage",
                        coverage.Length, Convert.ToHexStringLower(SHA256.HashData(coverage))),
                ],
                CancellationToken.None);
            Assert.True(recorded.Succeeded, recorded.Error);
        }

        List<OracleSchema> schemas =
        [
            OracleSchemaParser.Parse(await File.ReadAllTextAsync(
                Path.Combine(workspaceRoot, $"{SourceRoot}/db/schema.sql".Replace('/', Path.DirectorySeparatorChar)))),
        ];

        TargetMappingRead mapping = TargetMappingReader.Read(
            await File.ReadAllTextAsync(Path.Combine(workspaceRoot, ManifestPath.Replace('/', Path.DirectorySeparatorChar))),
            OracleSchema.Merge(schemas),
            read.Modules!);
        Assert.NotNull(mapping.Mapping);

        return new Harness(
            workspaceRoot, store, runs, ledgers, actor, project.Value.ProjectId, ledgerId, fence,
            approvedTarget, mapping.Mapping!, read.Modules!);
    }

    private static PhaseExecutionContext Context(
        string workspaceRoot,
        MigrationRunRequest request,
        MigrationPhase phase) =>
        new(
            workspaceRoot,
            SourceRoot,
            OutputRoot,
            MigrationRunPlanner.Plan(request).Phases.Single(entry => entry.Phase == phase),
            request,
            (_, _) => { });

    private static void Write(string root, string relativePath, string content)
    {
        string absolute = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);
        File.WriteAllText(absolute, content);
    }

    private static async Task<long> CompleteRunAsync(
        FileMigrationRunStore runs,
        string projectId,
        string workspaceId,
        string snapshotHash,
        string targetProfileHash,
        IReadOnlyList<MigrationRunArtifact> artifacts)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;

        await runs.EnqueueAsync(
            new MigrationRunRecord
            {
                RunId = RunId,
                TenantId = Tenant,
                ProjectId = projectId,
                ActorObjectId = Operator,
                WorkspaceId = workspaceId,
                WorkspaceNodeId = "node-a",
                WorkspaceOwnerId = $"{Tenant}:{Operator}/{projectId}",
                SourceSnapshotHash = snapshotHash,
                PlanInputHash = new string('b', 64),
                TargetProfileId = "sandbox",
                TargetProfileVersion = 1,
                TargetProfileHash = targetProfileHash,
                Request = Harness.Request(),
                EnqueuedUtc = now,
            },
            CancellationToken.None);

        MigrationRunClaim claim = (await runs.ClaimAsync(
            "node-a", "worker-a", now, TimeSpan.FromMinutes(5), CancellationToken.None))!;
        Assert.True(await runs.MarkRunningAsync(RunId, claim.FenceToken, now, CancellationToken.None));
        Assert.True(await runs.CompleteAsync(
            RunId,
            claim.FenceToken,
            MigrationRunState.Succeeded,
            now.AddSeconds(5),
            null,
            null,
            artifacts,
            "done",
            new ProgressSignal(
                ProgressOperations.MigrationRun,
                ProgressActions.RunCompleted,
                ProgressState.Completed,
                "Test run",
                "Terminal",
                "Review"),
            CancellationToken.None));

        return claim.FenceToken;
    }
}
