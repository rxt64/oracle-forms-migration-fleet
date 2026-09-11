// Copyright (c) Microsoft. All rights reserved.

using OracleFormsMigrationFleet.Fleet;

namespace OracleFormsMigrationFleet.Tests;

public class MigrationRunPlannerTests
{
    private static IReadOnlyList<EvidenceItem> GenerationEvidence() =>
    [
        Requests.Evidence("EV-INV", EvidenceKind.FormsModuleInventory),
        Requests.Evidence("EV-SRC", EvidenceKind.FormsModuleSource),
        Requests.Evidence("EV-XML", EvidenceKind.FormsXmlExport),
        Requests.Evidence("EV-PLSQL", EvidenceKind.PlSqlProgramUnit),
        Requests.Evidence("EV-SCHEMA", EvidenceKind.DatabaseSchemaExport),
        Requests.Evidence("EV-TEST", EvidenceKind.TestBaseline),
        Requests.Evidence("EV-DATA", EvidenceKind.DataProfile),
        Requests.Evidence("EV-PROCESS", EvidenceKind.BusinessProcessCatalog),
        Requests.Evidence("EV-CUTOVER", EvidenceKind.CutoverAndRollbackPlan),
    ];

    private static MigrationRunRequest Build(
        DatabaseTarget database = DatabaseTarget.AzureSqlDatabase,
        ExecutionMode mode = ExecutionMode.GenerateArtifacts,
        IReadOnlyList<EvidenceItem>? evidence = null,
        HumanApproval? executionApproval = null,
        HumanApproval? productionApproval = null,
        IReadOnlyList<MigrationAttestation>? attestations = null,
        string sourceRoot = "legacy/forms",
        string outputRoot = "out/orders") => new()
        {
            EngagementId = "ENG-001",
            ApplicationName = "ORDERS",
            RequestedMode = mode,
            Target = new TargetStack { Database = database },
            SourceRoot = sourceRoot,
            OutputRoot = outputRoot,
            Evidence = evidence ?? GenerationEvidence(),
            PlanApproval = Requests.Approved("plan-owner@contoso.com"),
            ExecutionApproval = executionApproval ?? HumanApproval.Pending,
            ProductionApproval = productionApproval ?? HumanApproval.Pending,
            Attestations = attestations ?? [],
        };

    private static MigrationAttestation Attest(AttestationKind kind, bool succeeded = true) => new()
    {
        Kind = kind,
        Succeeded = succeeded,
        AttestedBy = "adapter@contoso.com",
        Summary = $"{kind} recorded by the execution adapter.",
        Artifacts = [new ArtifactReference($"out/orders/reports/{kind}.json", ArtifactKind.ValidationReport, "Adapter evidence backing the attestation.")],
    };

    private static PhasePlan Phase(MigrationRunPlan plan, MigrationPhase phase) =>
        plan.Phases.Single(p => p.Phase == phase);

    [Fact]
    public void Generation_run_plans_concrete_react_java_and_database_artifacts()
    {
        MigrationRunPlan plan = MigrationRunPlanner.Plan(Build(DatabaseTarget.PostgreSql));

        Assert.Equal(ExecutionMode.GenerateArtifacts, plan.AuthorizedMode);

        PhasePlan code = Phase(plan, MigrationPhase.ApplicationCodeConversion);
        Assert.Equal(PhaseStatus.Planned, code.Status);
        Assert.Contains(code.ExpectedOutputs, a => a.Kind == ArtifactKind.FrontEndCode && a.Path == "out/orders/web/react/src");
        Assert.Contains(code.ExpectedOutputs, a =>
            a.Kind == ArtifactKind.BackEndCode && a.Path.StartsWith("out/orders/service/java-springboot", StringComparison.Ordinal));

        PhasePlan database = Phase(plan, MigrationPhase.DatabaseConversion);
        Assert.Contains(database.ExpectedOutputs, a =>
            a.Kind == ArtifactKind.DatabaseSchema && a.Path == "out/orders/database/postgresql/schema");
        Assert.All(plan.Phases.SelectMany(p => p.ExpectedOutputs),
            artifact => Assert.Null(WorkspacePath.Validate(artifact.Path, "artifact")));
    }

    [Theory]
    [InlineData(DatabaseTarget.SqlServer)]
    [InlineData(DatabaseTarget.AzureSqlDatabase)]
    [InlineData(DatabaseTarget.AzureSqlManagedInstance)]
    public void Sql_server_family_target_selects_ssma(DatabaseTarget target)
    {
        MigrationRunPlan plan = MigrationRunPlanner.Plan(Build(target));

        PhasePlan database = Phase(plan, MigrationPhase.DatabaseConversion);
        Assert.Contains(database.Tooling, tool => tool.Contains("SSMA", StringComparison.Ordinal));
        Assert.DoesNotContain(database.Tooling, tool => tool.Contains("Ora2Pg", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Postgresql_target_selects_ora2pg()
    {
        MigrationRunPlan plan = MigrationRunPlanner.Plan(Build(DatabaseTarget.PostgreSql));

        PhasePlan database = Phase(plan, MigrationPhase.DatabaseConversion);
        Assert.Contains(database.Tooling, tool => tool.Contains("Ora2Pg", StringComparison.Ordinal));
        Assert.DoesNotContain(database.Tooling, tool => tool.Contains("SSMA", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(DatabaseTarget.PostgreSql)]
    [InlineData(DatabaseTarget.AzureSqlManagedInstance)]
    public void Forms_conversion_is_never_delegated_to_ssma_or_ora2pg(DatabaseTarget target)
    {
        MigrationRunPlan plan = MigrationRunPlanner.Plan(Build(target));

        PhasePlan code = Phase(plan, MigrationPhase.ApplicationCodeConversion);
        Assert.DoesNotContain(code.Tooling, tool =>
            tool.StartsWith("SSMA", StringComparison.Ordinal) || tool.StartsWith("Ora2Pg", StringComparison.Ordinal));
        Assert.Contains(code.Tooling, tool => tool.Contains("Compiler-driven", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(code.Tooling, tool => tool.Contains("Fleet Forms-to-React/Java converter", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("APPLICATION_INVENTORY.md")]
    [InlineData("DATA_DICTIONARY.md")]
    [InlineData("DEPENDENCY_MAP.md")]
    [InlineData("TECHNICAL_DEBT_REPORT.md")]
    public void Source_analysis_emits_each_discovery_document(string fileName)
    {
        MigrationRunPlan plan = MigrationRunPlanner.Plan(Build());

        PhasePlan analysis = Phase(plan, MigrationPhase.SourceAnalysis);
        ArtifactReference artifact = Assert.Single(
            analysis.ExpectedOutputs, a => a.Path == $"out/orders/analysis/{fileName}");
        Assert.Equal(ArtifactKind.Documentation, artifact.Kind);
        Assert.Null(WorkspacePath.Validate(artifact.Path, "artifact"));
    }

    [Fact]
    public void Source_normalization_scopes_forms_mcp_to_parsing_not_target_code()
    {
        MigrationRunPlan plan = MigrationRunPlanner.Plan(Build());

        PhasePlan normalization = Phase(plan, MigrationPhase.SourceNormalization);
        Assert.Contains(normalization.Tooling, tool =>
            tool.Contains("oracle-forms-mcp", StringComparison.OrdinalIgnoreCase) &&
            tool.Contains("parser/indexer", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(normalization.Tooling, tool =>
            tool.Contains("ORACLE_HOME", StringComparison.Ordinal) &&
            tool.Contains("XML/PLD", StringComparison.Ordinal));
        Assert.Contains(normalization.Tooling, tool =>
            tool.Contains("not target-code generation", StringComparison.OrdinalIgnoreCase));

        PhasePlan code = Phase(plan, MigrationPhase.ApplicationCodeConversion);
        Assert.DoesNotContain(code.Tooling, tool => tool.Contains("oracle-forms-mcp", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Application_code_conversion_is_pilot_first_with_an_auditable_mapping_document()
    {
        MigrationRunPlan plan = MigrationRunPlanner.Plan(Build());

        PhasePlan code = Phase(plan, MigrationPhase.ApplicationCodeConversion);
        Assert.Contains("pilot", code.Objective, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(code.Tooling, tool => tool.Contains("pilot-first", StringComparison.OrdinalIgnoreCase));

        ArtifactReference mapping = Assert.Single(
            code.ExpectedOutputs, a => a.Path == "out/orders/reports/pilot-component-mapping.md");
        Assert.Equal(ArtifactKind.Documentation, mapping.Kind);
    }

    [Theory]
    [InlineData("/etc/forms")]
    [InlineData("C:/legacy/forms")]
    [InlineData("C:\\legacy\\forms")]
    [InlineData("../../legacy/forms")]
    [InlineData("legacy/../../forms")]
    [InlineData("https://contoso.example/forms")]
    public void Rooted_and_traversal_source_paths_are_rejected(string sourceRoot)
    {
        MigrationRunPlan plan = MigrationRunPlanner.Plan(Build(sourceRoot: sourceRoot));

        Assert.Equal(ExecutionMode.PlanOnly, plan.AuthorizedMode);
        Assert.Empty(plan.Phases);
        Assert.Contains(plan.Blockers, blocker => blocker.Contains("SourceRoot", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("/var/out")]
    [InlineData("../out")]
    public void Rooted_and_traversal_output_paths_are_rejected(string outputRoot)
    {
        MigrationRunPlan plan = MigrationRunPlanner.Plan(Build(outputRoot: outputRoot));

        Assert.Equal(ExecutionMode.PlanOnly, plan.AuthorizedMode);
        Assert.Contains(plan.Blockers, blocker => blocker.Contains("OutputRoot", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("mailto:attestation@example.com")]
    [InlineData("file:attestation.json")]
    [InlineData("https:attestation.json")]
    [InlineData("out/orders/reports/reconciliation.json:$DATA")]
    [InlineData("out/orders/reports/reconciliation.json:stream")]
    [InlineData("C:legacy/forms")]
    public void Scheme_drive_and_stream_like_paths_are_rejected(string path)
    {
        Assert.NotNull(WorkspacePath.Validate(path, "artifact"));
    }

    [Theory]
    [InlineData("legacy/forms")]
    [InlineData("out/orders/reports/reconciliation.json")]
    [InlineData("out\\orders\\reports\\reconciliation.json")]
    [InlineData("./out/orders")]
    [InlineData("out/orders/")]
    [InlineData("out/orders-v2/report (final).md")]
    public void Valid_workspace_relative_paths_are_accepted(string path)
    {
        Assert.Null(WorkspacePath.Validate(path, "artifact"));
    }

    [Fact]
    public void Generation_is_blocked_without_forms_source_plsql_schema_and_baseline()
    {
        MigrationRunPlan plan = MigrationRunPlanner.Plan(
            Build(evidence: [Requests.Evidence("EV-INV", EvidenceKind.FormsModuleInventory)]));

        Assert.Equal(ExecutionMode.PlanOnly, plan.AuthorizedMode);
        Assert.Equal(PhaseStatus.BlockedOnEvidence, Phase(plan, MigrationPhase.ApplicationCodeConversion).Status);
        Assert.Contains(plan.Blockers, b => b.Contains("FormsModuleSource or FormsXmlExport", StringComparison.Ordinal));
        Assert.Contains(plan.Blockers, b => b.Contains("PlSqlProgramUnit", StringComparison.Ordinal));
        Assert.Contains(plan.Blockers, b => b.Contains("DatabaseSchemaExport", StringComparison.Ordinal));
        Assert.Contains(plan.Blockers, b => b.Contains("TestBaseline", StringComparison.Ordinal));
    }

    [Fact]
    public void Sandbox_mutation_is_blocked_without_a_separate_execution_approval()
    {
        MigrationRunPlan plan = MigrationRunPlanner.Plan(Build(mode: ExecutionMode.SandboxMigration));

        Assert.Equal(ExecutionMode.GenerateArtifacts, plan.AuthorizedMode);

        PhasePlan sandbox = Phase(plan, MigrationPhase.SandboxDataMigration);
        Assert.Equal(MutationClass.SandboxDatabaseWrite, sandbox.Mutation);
        Assert.Equal(PhaseStatus.BlockedOnApproval, sandbox.Status);
        Assert.Contains(sandbox.Blockers, b => b.Contains("Plan approval does not authorize execution", StringComparison.Ordinal));
    }

    [Fact]
    public void Forms_xml_export_alone_authorizes_generation_and_every_forms_consuming_phase()
    {
        MigrationRunPlan plan = MigrationRunPlanner.Plan(Build(evidence:
        [
            .. GenerationEvidence().Where(item => item.Kind != EvidenceKind.FormsModuleSource),
        ]));

        Assert.Equal(ExecutionMode.GenerateArtifacts, plan.AuthorizedMode);
        Assert.Equal(PhaseStatus.Planned, Phase(plan, MigrationPhase.DocumentationGeneration).Status);
        Assert.Equal(PhaseStatus.Planned, Phase(plan, MigrationPhase.SourceNormalization).Status);
        Assert.Equal(PhaseStatus.Planned, Phase(plan, MigrationPhase.ApplicationCodeConversion).Status);
        Assert.Empty(plan.Blockers);
    }

    [Fact]
    public void Forms_source_alternative_does_not_waive_plsql_schema_or_baseline_requirements()
    {
        MigrationRunPlan plan = MigrationRunPlanner.Plan(Build(evidence:
        [
            Requests.Evidence("EV-XML", EvidenceKind.FormsXmlExport),
            Requests.Evidence("EV-PROCESS", EvidenceKind.BusinessProcessCatalog),
        ]));

        Assert.Equal(ExecutionMode.PlanOnly, plan.AuthorizedMode);
        Assert.Contains(plan.Blockers, b => b.Contains("PlSqlProgramUnit", StringComparison.Ordinal));
        Assert.Contains(plan.Blockers, b => b.Contains("DatabaseSchemaExport", StringComparison.Ordinal));
        Assert.Contains(plan.Blockers, b => b.Contains("TestBaseline", StringComparison.Ordinal));
        Assert.DoesNotContain(plan.Blockers, b => b.Contains("FormsModuleSource or FormsXmlExport", StringComparison.Ordinal));
    }

    [Fact]
    public void Sandbox_mutation_is_allowed_with_an_execution_approval()
    {
        MigrationRunPlan plan = MigrationRunPlanner.Plan(Build(
            mode: ExecutionMode.SandboxMigration,
            executionApproval: Requests.Approved("release-manager@contoso.com")));

        Assert.Equal(ExecutionMode.SandboxMigration, plan.AuthorizedMode);
        Assert.Equal(PhaseStatus.Planned, Phase(plan, MigrationPhase.SandboxDataMigration).Status);
        Assert.Equal(PhaseStatus.Planned, Phase(plan, MigrationPhase.DataReconciliation).Status);
        Assert.Equal(PhaseStatus.NotRequested, Phase(plan, MigrationPhase.ProductionCutover).Status);
    }

    [Fact]
    public void Production_cutover_is_blocked_without_reconciliation_and_acceptance_attestations()
    {
        MigrationRunPlan plan = MigrationRunPlanner.Plan(Build(
            mode: ExecutionMode.ProductionCutover,
            executionApproval: Requests.Approved("release-manager@contoso.com"),
            productionApproval: Requests.Approved("cab-chair@contoso.com"),
            attestations: [Attest(AttestationKind.SandboxMigrationCompleted)]));

        Assert.Equal(ExecutionMode.SandboxMigration, plan.AuthorizedMode);

        PhasePlan cutover = Phase(plan, MigrationPhase.ProductionCutover);
        Assert.Equal(MutationClass.ProductionWrite, cutover.Mutation);
        Assert.Equal(PhaseStatus.BlockedOnAttestation, cutover.Status);
        Assert.Contains(cutover.Blockers, b => b.Contains(nameof(AttestationKind.DataReconciliationPassed), StringComparison.Ordinal));
        Assert.Contains(cutover.Blockers, b => b.Contains(nameof(AttestationKind.HumanAcceptanceSigned), StringComparison.Ordinal));
    }

    [Fact]
    public void Production_cutover_is_blocked_when_an_attestation_reports_failure()
    {
        MigrationRunPlan plan = MigrationRunPlanner.Plan(Build(
            mode: ExecutionMode.ProductionCutover,
            executionApproval: Requests.Approved("release-manager@contoso.com"),
            productionApproval: Requests.Approved("cab-chair@contoso.com"),
            attestations:
            [
                Attest(AttestationKind.SandboxMigrationCompleted),
                Attest(AttestationKind.DataReconciliationPassed, succeeded: false),
                Attest(AttestationKind.HumanAcceptanceSigned),
            ]));

        Assert.Equal(ExecutionMode.SandboxMigration, plan.AuthorizedMode);
        Assert.Equal(PhaseStatus.BlockedOnAttestation, Phase(plan, MigrationPhase.ProductionCutover).Status);
    }

    [Fact]
    public void Production_cutover_is_blocked_when_an_attestation_cites_no_artifacts()
    {
        MigrationRunPlan plan = ProductionPlanWith(Attest(AttestationKind.DataReconciliationPassed) with { Artifacts = [] });

        Assert.Equal(ExecutionMode.SandboxMigration, plan.AuthorizedMode);

        PhasePlan cutover = Phase(plan, MigrationPhase.ProductionCutover);
        Assert.Equal(PhaseStatus.BlockedOnAttestation, cutover.Status);
        Assert.Contains(cutover.Blockers, b =>
            b.Contains(nameof(AttestationKind.DataReconciliationPassed), StringComparison.Ordinal) &&
            b.Contains("backing artifact", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("/etc/reports/reconciliation.json")]
    [InlineData("C:/reports/reconciliation.json")]
    [InlineData("C:\\reports\\reconciliation.json")]
    [InlineData("https://contoso.example/reports/reconciliation.json")]
    [InlineData("../../reports/reconciliation.json")]
    [InlineData("out/../../reports/reconciliation.json")]
    [InlineData("mailto:attestation@example.com")]
    [InlineData("file:attestation.json")]
    [InlineData("https:attestation.json")]
    [InlineData("out/orders/reports/reconciliation.json:$DATA")]
    public void Production_cutover_is_blocked_when_an_attestation_artifact_path_escapes_the_workspace(string path)
    {
        MigrationRunPlan plan = ProductionPlanWith(Attest(AttestationKind.HumanAcceptanceSigned) with
        {
            Artifacts = [new ArtifactReference(path, ArtifactKind.ValidationReport, "Acceptance record.")],
        });

        Assert.Equal(ExecutionMode.SandboxMigration, plan.AuthorizedMode);
        Assert.Equal(PhaseStatus.BlockedOnAttestation, Phase(plan, MigrationPhase.ProductionCutover).Status);
    }

    [Fact]
    public void Production_cutover_is_blocked_when_an_attestation_artifact_declares_an_undefined_kind()
    {
        MigrationRunPlan plan = ProductionPlanWith(Attest(AttestationKind.DataReconciliationPassed) with
        {
            Artifacts =
            [
                new ArtifactReference(
                    "out/orders/reports/reconciliation.json", (ArtifactKind)999, "Reconciliation report."),
            ],
        });

        Assert.Equal(ExecutionMode.SandboxMigration, plan.AuthorizedMode);

        PhasePlan cutover = Phase(plan, MigrationPhase.ProductionCutover);
        Assert.Equal(PhaseStatus.BlockedOnAttestation, cutover.Status);
        Assert.Contains(cutover.Blockers, b =>
            b.Contains(nameof(AttestationKind.DataReconciliationPassed), StringComparison.Ordinal));
    }

    [Fact]
    public void Production_cutover_is_blocked_when_every_cited_artifact_must_be_valid()
    {
        MigrationRunPlan plan = ProductionPlanWith(Attest(AttestationKind.SandboxMigrationCompleted) with
        {
            Artifacts =
            [
                new ArtifactReference("out/orders/reports/sandbox.json", ArtifactKind.ValidationReport, "Sandbox load results."),
                new ArtifactReference("/var/log/sandbox.json", ArtifactKind.ValidationReport, "Adapter host log."),
            ],
        });

        Assert.Equal(ExecutionMode.SandboxMigration, plan.AuthorizedMode);
        Assert.Equal(PhaseStatus.BlockedOnAttestation, Phase(plan, MigrationPhase.ProductionCutover).Status);
    }

    [Fact]
    public void Production_cutover_is_blocked_when_an_attestation_artifact_carries_credential_material()
    {
        MigrationRunPlan withCredentialPath = ProductionPlanWith(Attest(AttestationKind.DataReconciliationPassed) with
        {
            Artifacts = [new ArtifactReference("out/orders/reports/password=hunter2.json", ArtifactKind.ValidationReport, "Reconciliation report.")],
        });

        MigrationRunPlan withCredentialDescription = ProductionPlanWith(Attest(AttestationKind.DataReconciliationPassed) with
        {
            Artifacts = [new ArtifactReference("out/orders/reports/reconciliation.json", ArtifactKind.ValidationReport, "Signed with api_key: abc123.")],
        });

        Assert.Equal(ExecutionMode.SandboxMigration, withCredentialPath.AuthorizedMode);
        Assert.Equal(ExecutionMode.SandboxMigration, withCredentialDescription.AuthorizedMode);
    }

    [Fact]
    public void Production_cutover_is_blocked_when_the_attesting_identity_or_summary_carries_credential_material()
    {
        MigrationRunPlan withCredentialSigner = ProductionPlanWith(Attest(AttestationKind.HumanAcceptanceSigned) with
        {
            AttestedBy = "adapter@contoso.com password=hunter2",
        });

        MigrationRunPlan withCredentialSummary = ProductionPlanWith(Attest(AttestationKind.HumanAcceptanceSigned) with
        {
            Summary = "Signed off using client_secret=abc123.",
        });

        Assert.Equal(ExecutionMode.SandboxMigration, withCredentialSigner.AuthorizedMode);
        Assert.Equal(ExecutionMode.SandboxMigration, withCredentialSummary.AuthorizedMode);
    }

    /// <summary>Builds a fully approved production request where <paramref name="replacement"/> substitutes its own kind.</summary>
    private static MigrationRunPlan ProductionPlanWith(MigrationAttestation replacement) =>
        MigrationRunPlanner.Plan(Build(
            mode: ExecutionMode.ProductionCutover,
            executionApproval: Requests.Approved("release-manager@contoso.com"),
            productionApproval: Requests.Approved("cab-chair@contoso.com"),
            attestations:
            [
                .. new[]
                {
                    AttestationKind.SandboxMigrationCompleted,
                    AttestationKind.DataReconciliationPassed,
                    AttestationKind.HumanAcceptanceSigned,
                }.Select(kind => kind == replacement.Kind ? replacement : Attest(kind)),
            ]));

    [Fact]
    public void Production_cutover_is_blocked_without_its_own_approval()
    {
        MigrationRunPlan plan = MigrationRunPlanner.Plan(Build(
            mode: ExecutionMode.ProductionCutover,
            executionApproval: Requests.Approved("release-manager@contoso.com"),
            attestations:
            [
                Attest(AttestationKind.SandboxMigrationCompleted),
                Attest(AttestationKind.DataReconciliationPassed),
                Attest(AttestationKind.HumanAcceptanceSigned),
            ]));

        Assert.Equal(ExecutionMode.SandboxMigration, plan.AuthorizedMode);
        Assert.Equal(PhaseStatus.BlockedOnApproval, Phase(plan, MigrationPhase.ProductionCutover).Status);
    }

    [Fact]
    public void Production_cutover_requires_distinct_execution_and_production_approvers()
    {
        MigrationRunPlan plan = MigrationRunPlanner.Plan(Build(
            mode: ExecutionMode.ProductionCutover,
            executionApproval: Requests.Approved("release-manager@contoso.com"),
            productionApproval: Requests.Approved("RELEASE-MANAGER@contoso.com"),
            attestations:
            [
                Attest(AttestationKind.SandboxMigrationCompleted),
                Attest(AttestationKind.DataReconciliationPassed),
                Attest(AttestationKind.HumanAcceptanceSigned),
            ]));

        Assert.Equal(ExecutionMode.SandboxMigration, plan.AuthorizedMode);
        PhasePlan cutover = Phase(plan, MigrationPhase.ProductionCutover);
        Assert.Equal(PhaseStatus.BlockedOnApproval, cutover.Status);
        Assert.Contains(cutover.Blockers, blocker => blocker.Contains("distinct identities", StringComparison.Ordinal));
    }

    [Fact]
    public void Credential_material_in_approval_details_blocks_the_request()
    {
        MigrationRunPlan plan = MigrationRunPlanner.Plan(Build(
            mode: ExecutionMode.SandboxMigration,
            executionApproval: new HumanApproval
            {
                Decision = ApprovalDecision.Approved,
                ApproverId = "release-manager@contoso.com",
                Notes = "password=REDACTED_EXAMPLE_NOT_A_REAL_SECRET",
            }));

        Assert.Equal(ExecutionMode.PlanOnly, plan.AuthorizedMode);
        Assert.Empty(plan.Phases);
        Assert.Contains(plan.Blockers, blocker => blocker.Contains("credential material", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Credential_material_in_evidence_metadata_blocks_the_request()
    {
        EvidenceItem tainted = Requests.Evidence("EV-INV", EvidenceKind.FormsModuleInventory) with
        {
            Source = "password=REDACTED_EXAMPLE_NOT_A_REAL_SECRET",
        };

        MigrationRunPlan plan = MigrationRunPlanner.Plan(Build(
            evidence: [.. GenerationEvidence().Skip(1), tainted]));

        Assert.Equal(ExecutionMode.PlanOnly, plan.AuthorizedMode);
        Assert.Empty(plan.Phases);
        Assert.Contains(plan.Blockers, blocker => blocker.Contains("credential material", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Production_cutover_is_authorized_with_independent_approvals_and_attestations()
    {
        MigrationRunPlan plan = MigrationRunPlanner.Plan(Build(
            mode: ExecutionMode.ProductionCutover,
            executionApproval: Requests.Approved("release-manager@contoso.com"),
            productionApproval: Requests.Approved("cab-chair@contoso.com"),
            attestations:
            [
                Attest(AttestationKind.SandboxMigrationCompleted),
                Attest(AttestationKind.DataReconciliationPassed),
                Attest(AttestationKind.HumanAcceptanceSigned),
            ]));

        Assert.Equal(ExecutionMode.ProductionCutover, plan.AuthorizedMode);
        Assert.Equal(PhaseStatus.Planned, Phase(plan, MigrationPhase.ProductionCutover).Status);
        Assert.Empty(plan.Blockers);
    }

    [Fact]
    public void Plan_only_mode_never_authorizes_a_mutating_phase()
    {
        MigrationRunPlan plan = MigrationRunPlanner.Plan(Build(
            mode: ExecutionMode.PlanOnly,
            executionApproval: Requests.Approved("release-manager@contoso.com"),
            productionApproval: Requests.Approved("cab-chair@contoso.com")));

        Assert.Equal(ExecutionMode.PlanOnly, plan.AuthorizedMode);
        Assert.All(
            plan.Phases.Where(p => p.Mutation != MutationClass.None),
            p => Assert.Equal(PhaseStatus.NotRequested, p.Status));
    }

    [Fact]
    public void Every_lifecycle_phase_declares_ownership_and_mutation_class()
    {
        MigrationRunPlan plan = MigrationRunPlanner.Plan(Build());

        Assert.Equal(Enum.GetValues<MigrationPhase>().Length, plan.Phases.Count);
        Assert.All(plan.Phases, phase =>
        {
            Assert.True(Enum.IsDefined(phase.Owner));
            Assert.True(Enum.IsDefined(phase.Mutation));
            Assert.NotEmpty(phase.Objective);
            Assert.Equal(phase.RequiredMode >= ExecutionMode.SandboxMigration, phase.RequiresApproval);
        });
        Assert.Contains(plan.Disclaimers, d => d.Contains("did not run any process", StringComparison.Ordinal));
    }

    [Fact]
    public void Undetermined_database_target_is_rejected()
    {
        MigrationRunPlan plan = MigrationRunPlanner.Plan(Build(DatabaseTarget.Undetermined));

        Assert.Empty(plan.Phases);
        Assert.Contains(plan.Blockers, b => b.Contains("concrete database target", StringComparison.Ordinal));
    }
}
