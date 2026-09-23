using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using OracleFormsMigrationFleet.Fleet;
using OracleFormsMigrationFleet.Fleet.Execution;
using OracleFormsMigrationFleet.Fleet.Execution.Adapters;

namespace OracleFormsMigrationFleet.Tests;

/// <summary>
/// Binding recorded dispositions to generation.
///
/// Every case runs the real normalization adapter, reads the representation it wrote, projects the real
/// ledger rows out of it, and offers the conversion adapter the authorization a server would issue over
/// those rows. Nothing here hand-writes an intermediate representation or a ledger entry, because the
/// property under test is that the decisions the operator actually recorded are the ones that gate the
/// generator.
/// </summary>
public sealed class GenerationCoverageBindingTests
{
    private const string Operator = "migration-operator@contoso.com";
    private const string SourceRoot = "legacy/forms";
    private const string OutputRoot = "out/pilot";
    private const string IrPath = $"{OutputRoot}/intermediate/forms-ir.json";
    private const string ManifestPath = $"{SourceRoot}/{TargetMappingReader.ConventionalPath}";
    private const string CoveragePath = $"{OutputRoot}/{GenerationCoverage.RecordPath}";
    private const string BackendDescriptor = $"{OutputRoot}/application/backend/GeneratedBackend.slnx";

    private const string Tenant = "8f1e2b42-6a1d-4a24-9ad2-6c1b6a8f3d71";
    private const string Project = "prj-dotnet-pilot";
    private const string Snapshot = "4f4f4f4f4f4f4f4f4f4f4f4f4f4f4f4f4f4f4f4f4f4f4f4f4f4f4f4f4f4f4f4f";

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

    private const string SecondFormsExport = """
        <?xml version="1.0" encoding="UTF-8"?>
        <Module xmlns="http://xmlns.oracle.com/Forms" version="12.2.1.4" FormsVersion="12.2.1.4">
          <FormModule Name="ORDER_ENQUIRY" Title="Order enquiry">
            <Trigger Name="WHEN-BUTTON-PRESSED" TriggerText="BEGIN GO_BLOCK('ORDER_BLOCK'); END;"/>
          </FormModule>
        </Module>
        """;

    private static readonly JsonSerializerOptions CoverageJson = new()
    {
        Converters = { new JsonStringEnumConverter() },
    };

    [Fact]
    public async Task Generation_refuses_when_this_phase_was_given_no_server_authority()
    {
        using Fixture fixture = await FixtureAsync();

        PhaseExecutionResult result = await fixture.GenerateAsync((IGenerationAuthorizationProvider?)null);

        Assert.False(result.Succeeded);
        Assert.Contains("no server authority", result.FailureReason!, StringComparison.Ordinal);
        Assert.False(fixture.Workspace.Exists(BackendDescriptor));
        Assert.False(fixture.Workspace.Exists(CoveragePath));
    }

    [Fact]
    public async Task Generation_refuses_when_nothing_was_recorded_about_the_source_it_rests_on()
    {
        using Fixture fixture = await FixtureAsync();

        StubProvider provider = new(GenerationAuthorizationDecision.Denied(
            "No recorded disposition covers this run."));

        PhaseExecutionResult result = await fixture.GenerateAsync(provider);

        Assert.False(result.Succeeded);
        Assert.Equal(1, provider.Calls);
        Assert.Contains("No recorded disposition covers this run", string.Join(" ", result.Findings), StringComparison.Ordinal);
        Assert.False(fixture.Workspace.Exists(BackendDescriptor));
        Assert.False(fixture.Workspace.Exists(CoveragePath));
    }

    /// <summary>
    /// The regression QA found: the fixture's own module declares a trigger beside the mapped block, and
    /// an authorization scoped to the anchor's descendants leaves that trigger undecided and unmentioned.
    /// </summary>
    [Fact]
    public async Task A_property_declared_beside_the_anchor_is_not_left_out_of_the_authorization()
    {
        using Fixture fixture = await FixtureAsync();

        // The fixture really does declare behaviour outside the anchored block, so the case is real.
        Assert.NotEmpty(fixture.Unanchored);
        Assert.Contains(fixture.Unanchored, entry => entry.ObjectPath.Contains("Trigger", StringComparison.Ordinal));

        // Exactly what the anchor-scoped authorization used to carry: every anchored property decided,
        // the sibling trigger absent rather than decided.
        GenerationAuthorization authorization = fixture.Authorize(
            fixture.Anchored.Select(entry => Decided(entry, DispositionDecision.Preserve, "DR-PRESERVE-DECLARED")),
            ledgerEntryCount: fixture.Scope.Count);

        PhaseExecutionResult result = await fixture.GenerateAsync(authorization);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Findings, finding =>
            finding.Contains("A partial reading of the ledger", StringComparison.Ordinal));
        Assert.False(fixture.Workspace.Exists(BackendDescriptor));
        Assert.False(fixture.Workspace.Exists(CoveragePath));
    }

    [Fact]
    public async Task An_undecided_property_outside_the_anchor_blocks_generation_like_any_other()
    {
        using Fixture fixture = await FixtureAsync();
        GenerationScopedDecision sibling = fixture.Unanchored[0];

        // Everything decided except one property the mapping never cites. Nothing about it being
        // unanchored makes it optional.
        GenerationAuthorization authorization = fixture.Authorize(fixture.Decided()
            .Select(entry => entry.EntryId == sibling.EntryId
                ? entry with { Decision = DispositionDecision.Unresolved, MappingRuleId = null, MappingRuleVersion = 0 }
                : entry));

        PhaseExecutionResult result = await fixture.GenerateAsync(authorization);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Findings, finding =>
            finding.Contains("has no recorded disposition", StringComparison.Ordinal) &&
            finding.Contains(sibling.ObjectPath, StringComparison.Ordinal));
        Assert.False(fixture.Workspace.Exists(BackendDescriptor));
    }

    /// <summary>
    /// Forms source this run normalized that the recorded decisions never enumerated. It is not covered
    /// by silence, and there is no path that records it as out of scope.
    /// </summary>
    [Fact]
    public async Task A_normalized_module_the_decisions_never_enumerated_blocks_generation()
    {
        using Fixture fixture = await FixtureAsync(secondModule: true);

        Assert.Equal(2, fixture.ModulePaths.Count);
        string uncited = fixture.ModulePaths.Single(path => path.EndsWith("ORDER_ENQUIRY.xml", StringComparison.Ordinal));

        // A ledger that only ever enumerated the mapped module: internally consistent, and silent about
        // the second one.
        GenerationScopedDecision[] mappedModuleOnly =
            [.. fixture.Decided().Where(entry => entry.ModulePath != uncited)];

        Assert.Contains(fixture.Scope, entry => entry.ModulePath == uncited);

        PhaseExecutionResult result = await fixture.GenerateAsync(fixture.Authorize(mappedModuleOnly));

        Assert.False(result.Succeeded);
        Assert.Contains(result.Findings, finding =>
            finding.Contains("the recorded decisions say nothing about it", StringComparison.Ordinal) &&
            finding.Contains(uncited, StringComparison.Ordinal));
        Assert.False(fixture.Workspace.Exists(BackendDescriptor));
    }

    [Fact]
    public async Task Generation_refuses_while_one_property_the_mapping_rests_on_has_no_decision()
    {
        using Fixture fixture = await FixtureAsync();

        // Every property but one is decided, which is exactly the case a count would round away.
        GenerationAuthorization authorization = fixture.Authorize(fixture.Decided()
            .Select((entry, index) => index == 0
                ? entry with { Decision = DispositionDecision.Unresolved, MappingRuleId = null, MappingRuleVersion = 0 }
                : entry));

        PhaseExecutionResult result = await fixture.GenerateAsync(authorization);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Findings, finding =>
            finding.Contains("has no recorded disposition", StringComparison.Ordinal) &&
            finding.Contains(fixture.Scope[0].PropertyName, StringComparison.Ordinal));
        Assert.False(fixture.Workspace.Exists(BackendDescriptor));
    }

    [Fact]
    public async Task A_deferred_property_blocks_generation_because_deferring_is_deciding_later()
    {
        using Fixture fixture = await FixtureAsync();

        GenerationAuthorization authorization = fixture.Authorize(fixture.Decided()
            .Select((entry, index) => index == 1
                ? Decided(entry, DispositionDecision.Defer, "DR-DEFER-NEEDS-EVIDENCE")
                : entry));

        PhaseExecutionResult result = await fixture.GenerateAsync(authorization);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Findings, finding => finding.Contains("was deferred", StringComparison.Ordinal));
        Assert.False(fixture.Workspace.Exists(BackendDescriptor));
    }

    [Fact]
    public async Task A_decision_citing_a_rule_that_does_not_authorize_it_blocks_generation()
    {
        using Fixture fixture = await FixtureAsync();

        // Retire recorded under the preserve rule. The decision field alone would read as decided.
        GenerationAuthorization authorization = fixture.Authorize(fixture.Decided()
            .Select((entry, index) => index == 2
                ? Decided(entry, DispositionDecision.Retire, "DR-PRESERVE-DECLARED")
                : entry));

        PhaseExecutionResult result = await fixture.GenerateAsync(authorization);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Findings, finding =>
            finding.Contains("does not authorize", StringComparison.Ordinal));
        Assert.False(fixture.Workspace.Exists(BackendDescriptor));
    }

    [Fact]
    public async Task An_unanchored_trigger_recorded_preserved_blocks_generation()
    {
        using Fixture fixture = await FixtureAsync();
        GenerationScopedDecision trigger = fixture.TriggerBody;

        GenerationAuthorization authorization = fixture.Authorize(fixture.Decided()
            .Select(entry => entry.EntryId == trigger.EntryId
                ? Decided(entry, DispositionDecision.Preserve, "DR-PRESERVE-DECLARED")
                : entry));

        PhaseExecutionResult result = await fixture.GenerateAsync(authorization);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Findings, finding =>
            finding.Contains("emits nothing derived from it", StringComparison.Ordinal) &&
            finding.Contains(trigger.ObjectPath, StringComparison.Ordinal) &&
            finding.Contains(trigger.PropertyName, StringComparison.Ordinal));
        Assert.False(fixture.Workspace.Exists(BackendDescriptor));
        Assert.False(fixture.Workspace.Exists(CoveragePath));
    }

    /// <summary>
    /// Calling the same untranslated body a transformation does not make one exist. The rule authorizes
    /// the disposition; it does not supply the target the disposition claims.
    /// </summary>
    [Fact]
    public async Task The_same_trigger_recorded_transformed_blocks_generation_too()
    {
        using Fixture fixture = await FixtureAsync();
        GenerationScopedDecision trigger = fixture.TriggerBody;

        GenerationAuthorization authorization = fixture.Authorize(fixture.Decided()
            .Select(entry => entry.EntryId == trigger.EntryId
                ? Decided(entry, DispositionDecision.Transform, "DR-TRANSFORM-EQUIVALENT")
                : entry));

        PhaseExecutionResult result = await fixture.GenerateAsync(authorization);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Findings, finding =>
            finding.Contains("emits nothing derived from it", StringComparison.Ordinal) &&
            finding.Contains(trigger.ObjectPath, StringComparison.Ordinal));
        Assert.False(fixture.Workspace.Exists(BackendDescriptor));
    }

    /// <summary>
    /// Retiring the same trigger is a decision an operator took, and generation proceeds under it without
    /// anything being emitted for the body or claimed about it.
    /// </summary>
    [Fact]
    public async Task The_same_trigger_retired_by_an_operator_carries_no_claim_that_it_was_implemented()
    {
        using Fixture fixture = await FixtureAsync();
        GenerationScopedDecision trigger = fixture.TriggerBody;

        PhaseExecutionResult result = await fixture.GenerateAsync(fixture.Authorize(fixture.Decided()));

        Assert.True(result.Succeeded, result.FailureReason);

        GenerationCoverageRecord record = fixture.Coverage();
        GenerationCoveredEntry covered = record.CoveredEntries.Single(entry => entry.EntryId == trigger.EntryId);
        Assert.Equal(DispositionDecision.Retire, covered.Decision);
        Assert.Equal("DR-RETIRE-NO-TARGET", covered.MappingRuleId);

        // The retired body reaches no emitted file, so nothing in the tier can be read as having
        // implemented it.
        Assert.All(record.OutputFiles, path =>
            Assert.DoesNotContain("EXECUTE_QUERY", fixture.Workspace.Read($"{OutputRoot}/{path}"), StringComparison.Ordinal));
    }

    /// <summary>
    /// The declared column binding of an item under the mapped block: the generator does re-express it,
    /// as a named column of the table the role resolved to.
    /// </summary>
    [Fact]
    public async Task The_column_an_item_under_the_mapped_block_declares_may_be_recorded_carried_forward()
    {
        using Fixture fixture = await FixtureAsync();

        GenerationScopedDecision binding = fixture.Scope.Single(entry =>
            entry.ObjectPath == fixture.ItemPath && entry.PropertyName == "ColumnName");
        Assert.True(fixture.Carries(binding));

        PhaseExecutionResult result = await fixture.GenerateAsync(fixture.Authorize(fixture.Decided()));

        Assert.True(result.Succeeded, result.FailureReason);
        Assert.Equal(
            DispositionDecision.Preserve,
            fixture.Coverage().CoveredEntries.Single(entry => entry.EntryId == binding.EntryId).Decision);
    }

    /// <summary>
    /// A property of that same item the generator never reads. Being declared beside a binding that is
    /// carried does not make it carried.
    /// </summary>
    [Fact]
    public async Task A_property_beside_that_binding_that_the_generator_never_reads_blocks_generation()
    {
        using Fixture fixture = await FixtureAsync();

        GenerationScopedDecision prompt = fixture.Scope.Single(entry =>
            entry.ObjectPath == fixture.ItemPath && entry.PropertyName == "Prompt");
        Assert.False(fixture.Carries(prompt));

        GenerationAuthorization authorization = fixture.Authorize(fixture.Decided()
            .Select(entry => entry.EntryId == prompt.EntryId
                ? Decided(entry, DispositionDecision.Preserve, "DR-PRESERVE-DECLARED")
                : entry));

        PhaseExecutionResult result = await fixture.GenerateAsync(authorization);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Findings, finding =>
            finding.Contains("emits nothing derived from it", StringComparison.Ordinal) &&
            finding.Contains("'Prompt'", StringComparison.Ordinal));
        Assert.False(fixture.Workspace.Exists(BackendDescriptor));
    }

    [Fact]
    public async Task Generation_refuses_when_the_mapping_changed_after_the_decisions_were_recorded()
    {
        using Fixture fixture = await FixtureAsync();
        GenerationAuthorization authorization = fixture.Authorize(fixture.Decided());

        // A manifest that still validates against the schema, but is not the one the decisions covered.
        fixture.Workspace.WriteFile(ManifestPath, fixture.Manifest.Replace(
            "\"fixtureLabel\": \"synthetic-fixture-meridian-orders\"",
            "\"fixtureLabel\": \"synthetic-fixture-meridian-orders-rebound\"",
            StringComparison.Ordinal));

        Assert.NotEqual(fixture.ManifestSha256, Sha256(fixture.Workspace.Absolute(ManifestPath)));

        PhaseExecutionResult result = await fixture.GenerateAsync(authorization);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Findings, finding =>
            finding.Contains("mapping that changed after it was covered", StringComparison.Ordinal));
        Assert.False(fixture.Workspace.Exists(BackendDescriptor));
    }

    [Fact]
    public async Task Generation_refuses_when_the_decisions_describe_a_different_normalized_source()
    {
        using Fixture fixture = await FixtureAsync();

        GenerationAuthorization authorization = fixture.Authorize(fixture.Decided()) with
        {
            IntermediateContentSha256 = new string('a', 64),
        };

        PhaseExecutionResult result = await fixture.GenerateAsync(authorization);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Findings, finding =>
            finding.Contains("They describe different source", StringComparison.Ordinal));
        Assert.False(fixture.Workspace.Exists(BackendDescriptor));
    }

    [Fact]
    public async Task An_authorization_issued_over_other_source_objects_does_not_cover_this_mapping()
    {
        using Fixture fixture = await FixtureAsync();

        GenerationAuthorization authorization = fixture.Authorize(
        [
            Decided(
                fixture.Scope[0] with { ObjectPath = "Module[1]/FormModule[1]/Block[2]" },
                DispositionDecision.Preserve,
                "DR-PRESERVE-DECLARED"),
        ]);

        PhaseExecutionResult result = await fixture.GenerateAsync(authorization);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Findings, finding =>
            finding.Contains("no recorded disposition covers it", StringComparison.Ordinal));
        Assert.False(fixture.Workspace.Exists(BackendDescriptor));
    }

    /// <summary>
    /// A file in the tier that this phase did not write. The record would otherwise describe an output
    /// set smaller than the output, and the extra file would ship unlisted.
    /// </summary>
    [Fact]
    public async Task A_file_in_the_tier_that_this_phase_did_not_write_stops_the_record()
    {
        using Fixture fixture = await FixtureAsync();
        fixture.Workspace.WriteFile($"{OutputRoot}/application/backend/Stray.cs", "// left behind");

        PhaseExecutionResult result = await fixture.GenerateAsync(fixture.Authorize(fixture.Decided()));

        Assert.False(result.Succeeded);
        Assert.Contains(result.Findings, finding =>
            finding.Contains("written by nothing in this phase", StringComparison.Ordinal) &&
            finding.Contains("backend/Stray.cs", StringComparison.Ordinal));
        Assert.False(fixture.Workspace.Exists(CoveragePath));
    }

    [Fact]
    public async Task A_covered_generation_emits_the_tier_and_records_which_decisions_covered_it()
    {
        using Fixture fixture = await FixtureAsync();
        GenerationAuthorization authorization = fixture.Authorize(fixture.Decided());

        PhaseExecutionResult result = await fixture.GenerateAsync(authorization);

        Assert.True(result.Succeeded, result.FailureReason);
        Assert.True(fixture.Workspace.Exists(BackendDescriptor));
        Assert.True(fixture.Workspace.Exists(CoveragePath));
        Assert.Contains(result.Artifacts, artifact => artifact.Path == CoveragePath);

        GenerationCoverageRecord record = fixture.Coverage();
        Assert.Equal("dled-binding-test", record.LedgerId);
        Assert.Equal(Tenant, record.TenantId);
        Assert.Equal(Project, record.ProjectId);
        Assert.Equal(fixture.IntermediateSha256, record.IntermediateContentSha256);
        Assert.Equal(fixture.ManifestSha256, record.MappingManifestSha256);
        Assert.Equal(Snapshot, record.SourceSnapshotHash);
        Assert.Equal(OutputRoot, record.OutputRoot);
        Assert.Equal(authorization.ScopeDigest, record.ScopeDigest);
        Assert.Equal(
            [.. authorization.Scope.Select(entry => entry.EntryId).Order(StringComparer.Ordinal)],
            [.. record.CoveredEntries.Select(entry => entry.EntryId)]);

        // The decision revisions and decisions the generation was covered at travel with it, so a later
        // change to any of them is visible to the server rather than hidden behind matching identifiers.
        Assert.All(record.CoveredEntries, entry => Assert.Equal(64, entry.DecisionRevision.Length));
        Assert.Contains(record.CoveredEntries, entry => entry.Decision == DispositionDecision.Retire);

        // Every file the phase wrote is listed, including the ones written after the code was emitted,
        // and the record never lists itself.
        Assert.Contains("application/CONVERSION_NOTES.md", record.OutputFiles);
        Assert.DoesNotContain(GenerationCoverage.RecordPath, record.OutputFiles);
        Assert.Equal(record.OutputFiles.Count, record.OutputFiles.Distinct(StringComparer.Ordinal).Count());

        // The digest describes the bytes on disk, recomputed here the way a server would.
        Assert.Equal(
            GenerationCoverage.OutputSetDigest(record.OutputFiles.Select(path =>
                (path, Sha256(fixture.Workspace.Absolute($"{OutputRoot}/{path}"))))),
            record.OutputSetSha256);

        // Editing one generated file breaks the binding, so the record cannot be reused as evidence of it.
        fixture.Workspace.WriteFile($"{OutputRoot}/{record.OutputFiles[0]}", "tampered");
        Assert.NotEqual(
            record.OutputSetSha256,
            GenerationCoverage.OutputSetDigest(record.OutputFiles.Select(path =>
                (path, Sha256(fixture.Workspace.Absolute($"{OutputRoot}/{path}"))))));
    }

    [Fact]
    public async Task A_covered_generation_claims_nothing_as_verified()
    {
        using Fixture fixture = await FixtureAsync();
        GenerationAuthorization authorization = fixture.Authorize(fixture.Decided());

        PhaseExecutionResult result = await fixture.GenerateAsync(authorization);

        Assert.True(result.Succeeded, result.FailureReason);

        // No attestation, and the coverage record carries no verification field to be mistaken for one.
        Assert.DoesNotContain("\"verif", fixture.Workspace.Read(CoveragePath), StringComparison.OrdinalIgnoreCase);
        Assert.Contains(result.Artifacts, artifact =>
            artifact.Path == CoveragePath && artifact.Description.Contains("nothing as verified", StringComparison.Ordinal));

        // The ledger rows a server would record from this are generated, never verified, and a ledger
        // holding only generated rows still refuses to be called complete.
        DispositionLedgerEntry[] recorded =
        [
            .. fixture.Entries.Select(entry =>
            {
                DispositionLedgerEntry decided = entry with
                {
                    Decision = DispositionDecision.Preserve,
                    Rationale = "Carried into the generated application unchanged.",
                    MappingRuleId = "DR-PRESERVE-DECLARED",
                    MappingRuleVersion = 1,
                };

                return decided with
                {
                    GeneratedRefs = [new DispositionGeneratedReference(
                        "run-1", $"{OutputRoot}/application", fixture.Coverage().OutputSetSha256, DateTimeOffset.UtcNow,
                        decided.DecisionRevision)],
                };
            }),
        ];

        DispositionLedgerCounts counts = DispositionLedgerRules.Count(recorded);
        Assert.Equal(counts.Discovered, counts.Generated);
        Assert.Equal(0, counts.Verified);

        DispositionCompletionState completion = DispositionLedgerRules.Completion(counts);
        Assert.False(completion.CanComplete);
        Assert.Contains(completion.Blockers, blocker =>
            blocker.Contains("not backed by a test that executed and passed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_java_path_is_unchanged_and_never_consults_an_authorization()
    {
        using Fixture fixture = await FixtureAsync();
        StubProvider provider = new(GenerationAuthorizationDecision.Denied("never asked"));

        PhaseExecutionResult result = await fixture.GenerateAsync(provider, BackEndStack.JavaSpringBoot);

        Assert.True(result.Succeeded, result.FailureReason);
        Assert.Equal(0, provider.Calls);
        Assert.True(fixture.Workspace.Exists($"{OutputRoot}/application/backend/pom.xml"));
        Assert.False(fixture.Workspace.Exists(CoveragePath));
    }

    private static GenerationScopedDecision Decided(GenerationScopedDecision entry, DispositionDecision decision, string rule) =>
        entry with { Decision = decision, MappingRuleId = rule, MappingRuleVersion = 1 };

    private static string Sha256(string absolutePath) =>
        Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(absolutePath)));

    private static MigrationRunRequest Request(BackEndStack stack) => new()
    {
        EngagementId = "ENG-BINDING",
        ApplicationName = "Meridian Order Entry",
        RequestedMode = ExecutionMode.GenerateArtifacts,
        Target = new TargetStack { BackEnd = stack, Database = DatabaseTarget.PostgreSql },
        OracleFormsVersion = "12c",
        OracleDatabaseVersion = "19c",
        SourceRoot = SourceRoot,
        OutputRoot = OutputRoot,
    };

    /// <summary>
    /// A normalized run with Forms source, a mapping that anchors on a retained source object, and every
    /// ledger row that source declares — not only the rows beneath the anchor.
    /// </summary>
    private static async Task<Fixture> FixtureAsync(bool secondModule = false)
    {
        TemporaryWorkspace workspace = new();
        workspace.WriteFile($"{SourceRoot}/ui/ORDER_ENTRY.xml", FormsExport);
        workspace.WriteFile($"{SourceRoot}/db/schema.sql", DotNetPilotFixtures.MeridianSchema);

        if (secondModule)
        {
            workspace.WriteFile($"{SourceRoot}/ui/ORDER_ENQUIRY.xml", SecondFormsExport);
        }

        MigrationRunRequest request = Request(BackEndStack.AspNetCore);
        PhasePlan normalizationPlan = MigrationRunPlanner.Plan(request).Phases
            .Single(phase => phase.Phase == MigrationPhase.SourceNormalization);

        PhaseExecutionResult normalized = await new SourceNormalizationAdapter().ExecuteAsync(
            new PhaseExecutionContext(workspace.Root, SourceRoot, OutputRoot, normalizationPlan, request, (_, _) => { }),
            CancellationToken.None);
        Assert.True(normalized.Succeeded, normalized.FailureReason);

        FormsIntermediateRead read = FormsIntermediateReader.Read(workspace.Read(IrPath), SourceRoot);
        Assert.NotNull(read.Modules);

        FormsModule module = read.Modules!.First(candidate => candidate.SourcePath!.EndsWith("ORDER_ENTRY.xml", StringComparison.Ordinal));
        FormsSourceFactSet facts = module.SourceFacts!;
        FormsSourceFact block = facts.Facts.First(fact => fact.LocalName == "Block");
        FormsSourceFact item = facts.Facts.First(fact => fact.LocalName == "Item");

        string manifest = DotNetPilotFixtures.MeridianManifest.Replace(
            "\"sources\": [",
            $$"""
              "sources": [
                          { "id": "frm-block", "kind": "FormsSourceObject", "path": "{{block.Id}}", "module": "{{module.SourcePath}}", "textDigest": "{{facts.TextDigest}}" },
              """,
            StringComparison.Ordinal)
            .Replace("\"sourceRefs\": [\"sch-head\"]", "\"sourceRefs\": [\"sch-head\", \"frm-block\"]", StringComparison.Ordinal);

        workspace.WriteFile(ManifestPath, manifest);

        (IReadOnlyList<DispositionLedgerEntry>? entries, string? rejection) = DispositionLedgerEntries.Project(
            "dled-binding-test", Tenant, Project, Snapshot, read.Modules!);
        Assert.Null(rejection);

        DispositionLedgerEntry[] ledger = [.. entries!.OrderBy(entry => entry.EntryId, StringComparer.Ordinal)];
        Assert.NotEmpty(ledger);

        return new Fixture(
            workspace,
            manifest,
            Sha256(workspace.Absolute(IrPath)),
            Sha256(workspace.Absolute(ManifestPath)),
            block.Id,
            item.Id,
            module.SourcePath!,
            [.. read.Modules!.Select(entry => entry.SourcePath!)],
            ledger,
            [
                .. ledger.Select(entry => new GenerationScopedDecision(
                    entry.EntryId,
                    entry.Identity.FilePath,
                    entry.Identity.ObjectPath,
                    entry.Identity.PropertyName,
                    DispositionDecision.Unresolved,
                    null,
                    0,
                    entry.DecisionRevision)),
            ]);
    }

    /// <summary>An authorization the phase is handed at the moment it asks, exactly as a host would.</summary>
    private sealed record StubProvider(GenerationAuthorizationDecision Decision) : IGenerationAuthorizationProvider
    {
        public int Calls { get; private set; }

        public Task<GenerationAuthorizationDecision> AuthorizeAsync(
            GenerationAuthorizationRequest request,
            CancellationToken cancellationToken)
        {
            Calls++;
            Request = request;
            return Task.FromResult(Decision);
        }

        public GenerationAuthorizationRequest? Request { get; private set; }
    }

    private sealed record Fixture(
        TemporaryWorkspace Workspace,
        string Manifest,
        string IntermediateSha256,
        string ManifestSha256,
        string AnchorPath,
        string ItemPath,
        string ModulePath,
        IReadOnlyList<string> ModulePaths,
        IReadOnlyList<DispositionLedgerEntry> Entries,
        IReadOnlyList<GenerationScopedDecision> Scope) : IDisposable
    {
        public void Dispose() => Workspace.Dispose();

        /// <summary>Every property the anchored block declares, and every property declared beside it.</summary>
        public IReadOnlyList<GenerationScopedDecision> Anchored =>
            [.. Scope.Where(entry => GenerationCoverage.Covers(AnchorPath, entry.ObjectPath))];

        public IReadOnlyList<GenerationScopedDecision> Unanchored =>
            [.. Scope.Where(entry => !GenerationCoverage.Covers(AnchorPath, entry.ObjectPath))];

        /// <summary>
        /// The one property in this fixture that is behaviour rather than structure: a trigger body the
        /// generator has no translation for, declared outside the mapped block.
        /// </summary>
        public GenerationScopedDecision TriggerBody =>
            Scope.Single(entry =>
                entry.ObjectPath.Contains("Trigger", StringComparison.Ordinal) &&
                entry.ModulePath == ModulePath &&
                entry.PropertyName == "TriggerText");

        /// <summary>
        /// The four properties this test asserts the .NET master/detail generator re-expresses, named one
        /// at a time rather than derived from where they sit: the mapped block's existence and the table
        /// it declares it queries, and the mapped item's existence and the column it declares it is bound
        /// to. Everything else this export declares reaches no emitted file.
        /// </summary>
        public bool Carries(GenerationScopedDecision entry) =>
            entry.ModulePath == ModulePath &&
            ((entry.ObjectPath == AnchorPath && entry.PropertyName is "#object" or "QueryDataSourceName") ||
                (entry.ObjectPath == ItemPath && entry.PropertyName is "#object" or "ColumnName"));

        /// <summary>
        /// Each property decided on its own terms: carried forward where the generator emits something
        /// derived from it, retired where it does not. Retiring is the operator's decision to drop the
        /// property, and it claims nothing about what was generated.
        /// </summary>
        public IReadOnlyList<GenerationScopedDecision> Decided() =>
        [
            .. Scope.Select(entry => Carries(entry)
                ? GenerationCoverageBindingTests.Decided(entry, DispositionDecision.Preserve, "DR-PRESERVE-DECLARED")
                : GenerationCoverageBindingTests.Decided(entry, DispositionDecision.Retire, "DR-RETIRE-NO-TARGET")),
        ];

        public GenerationAuthorization Authorize(IEnumerable<GenerationScopedDecision> scope) =>
            Authorize(scope, ledgerEntryCount: null);

        public GenerationAuthorization Authorize(IEnumerable<GenerationScopedDecision> scope, int? ledgerEntryCount)
        {
            GenerationScopedDecision[] materialized = [.. scope];

            return new GenerationAuthorization
            {
                LedgerId = "dled-binding-test",
                TenantId = Tenant,
                ProjectId = Project,
                RunId = "run-1",
                SourceSnapshotHash = Snapshot,
                IntermediateContentSha256 = IntermediateSha256,
                MappingManifestSha256 = ManifestSha256,
                IssuedUtc = DateTimeOffset.UtcNow,
                Scope = materialized,
                LedgerEntryCount = ledgerEntryCount ?? materialized.Length,
                ScopeDigest = GenerationCoverage.ScopeDigest(materialized),
            };
        }

        public GenerationCoverageRecord Coverage() =>
            JsonSerializer.Deserialize<GenerationCoverageRecord>(Workspace.Read(CoveragePath), CoverageJson)!;

        public Task<PhaseExecutionResult> GenerateAsync(
            GenerationAuthorization? authorization,
            BackEndStack stack = BackEndStack.AspNetCore) =>
            GenerateAsync(
                authorization is null ? null : new StubProvider(GenerationAuthorizationDecision.Granted(authorization)),
                stack);

        public Task<PhaseExecutionResult> GenerateAsync(
            IGenerationAuthorizationProvider? provider,
            BackEndStack stack = BackEndStack.AspNetCore)
        {
            MigrationRunRequest request = Request(stack);
            PhasePlan plan = MigrationRunPlanner.Plan(request).Phases
                .Single(phase => phase.Phase == MigrationPhase.ApplicationCodeConversion);

            return new ApplicationCodeConversionAdapter().ExecuteAsync(
                new PhaseExecutionContext(Workspace.Root, SourceRoot, OutputRoot, plan, request, (_, _) => { })
                {
                    CompletedPhases =
                    [
                        new PhaseOutcome(
                            MigrationPhase.SourceNormalization,
                            PhaseStatus.Planned,
                            PhaseExecutionState.Executed,
                            [],
                            [],
                            null),
                    ],
                    AuthorizationProvider = provider,
                },
                CancellationToken.None);
        }
    }
}
