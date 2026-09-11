// Copyright (c) Microsoft. All rights reserved.

using OracleFormsMigrationFleet.Fleet;

namespace OracleFormsMigrationFleet.Tests;

public class HumanApprovalGateTests
{
    private static MigrationPlan RunWith(HumanApproval approval) =>
        MigrationFleetOrchestrator.Run(Requests.Build(
            Requests.CompleteEvidence(WorkloadSignal.SelfContainedSchema), approval));

    [Fact]
    public void Pending_approval_blocks_acceptance()
    {
        MigrationPlan plan = RunWith(HumanApproval.Pending);

        Assert.False(plan.IsAccepted);
        Assert.Equal(MigrationStage.HumanApproval, plan.FinalStage);
        Assert.Equal(StageStatus.BlockedOnApproval, plan.FinalStatus);
        Assert.NotEmpty(plan.ConversionTasks);
        Assert.NotEmpty(plan.Blockers);
    }

    [Fact]
    public void Rejected_approval_blocks_acceptance()
    {
        MigrationPlan plan = RunWith(new HumanApproval
        {
            Decision = ApprovalDecision.Rejected,
            ApproverId = "bob@contoso.com",
            Notes = "Needs a downtime plan.",
        });

        Assert.False(plan.IsAccepted);
        Assert.Equal(StageStatus.Rejected, plan.FinalStatus);
        Assert.NotEmpty(plan.Blockers);
    }

    [Fact]
    public void Approval_without_approver_identity_is_rejected_at_intake()
    {
        MigrationPlan plan = RunWith(new HumanApproval { Decision = ApprovalDecision.Approved });

        Assert.False(plan.IsAccepted);
        Assert.Equal(MigrationStage.Intake, plan.FinalStage);
        Assert.Equal(StageStatus.BlockedOnEvidence, plan.FinalStatus);
    }

    [Fact]
    public void Approval_with_approver_identity_accepts_the_plan()
    {
        MigrationPlan plan = RunWith(Requests.Approved());

        Assert.True(plan.IsAccepted);
        Assert.Equal(MigrationStage.Completed, plan.FinalStage);
        Assert.Equal(StageStatus.Completed, plan.FinalStatus);
        Assert.Contains("Approved by 'alice@contoso.com'.", plan.Stages[^1].Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void Acceptance_is_impossible_without_reaching_the_gate()
    {
        MigrationPlan plan = MigrationFleetOrchestrator.Run(Requests.Build([], Requests.Approved()));

        Assert.False(plan.IsAccepted);
        Assert.DoesNotContain(plan.Stages, s => s.Stage == MigrationStage.HumanApproval);
    }
}

public class RequestValidationTests
{
    [Fact]
    public void Missing_identifiers_are_reported()
    {
        RequestValidationResult result = RequestValidator.Validate(
            Requests.Build(engagementId: "", applicationName: " "));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("EngagementId", StringComparison.Ordinal));
        Assert.Contains(result.Errors, e => e.Contains("ApplicationName", StringComparison.Ordinal));
    }

    [Fact]
    public void Duplicate_evidence_ids_are_rejected()
    {
        RequestValidationResult result = RequestValidator.Validate(Requests.Build(
        [
            Requests.Evidence("EV-1", EvidenceKind.FormsModuleInventory),
            Requests.Evidence("EV-1", EvidenceKind.PlSqlProgramUnit),
        ]));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("Duplicate evidence id", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("Server=db;User Id=sa;Password=hunter2")]
    [InlineData("api_key: abc123")]
    [InlineData("-----BEGIN RSA PRIVATE KEY-----")]
    public void Credential_material_in_evidence_is_rejected(string summary)
    {
        MigrationAssessmentRequest request = Requests.Build(
        [
            Requests.Evidence("EV-1", EvidenceKind.FormsModuleInventory) with { Summary = summary },
        ]);

        RequestValidationResult result = RequestValidator.Validate(request);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("credential material", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Ordinary_text_is_not_flagged_as_a_secret() =>
        Assert.False(FleetGuardrails.ContainsPotentialSecret(
            "The ORDERS form uses a password field bound to a database column."));

    [Fact]
    public void Valid_request_passes()
    {
        RequestValidationResult result = RequestValidator.Validate(
            Requests.Build(Requests.CompleteEvidence(WorkloadSignal.SelfContainedSchema), Requests.Approved()));

        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Null_request_returns_a_blocked_plan()
    {
        MigrationPlan plan = MigrationFleetOrchestrator.Run(null);

        Assert.Equal(MigrationStage.Intake, plan.FinalStage);
        Assert.Equal(StageStatus.BlockedOnEvidence, plan.FinalStatus);
        Assert.False(plan.IsAccepted);
    }

    [Fact]
    public void Null_record_members_are_rejected_without_throwing()
    {
        MigrationAssessmentRequest request = Requests.Build() with
        {
            Evidence = null!,
            BusinessConstraints = null!,
            Approval = null!,
        };

        MigrationPlan plan = MigrationFleetOrchestrator.Run(request);

        Assert.Equal(MigrationStage.Intake, plan.FinalStage);
        Assert.Equal(StageStatus.BlockedOnEvidence, plan.FinalStatus);
        Assert.Contains(plan.Stages[0].Findings, f => f.Detail.Contains("Evidence must be an array", StringComparison.Ordinal));
        Assert.Contains(plan.Stages[0].Findings, f => f.Detail.Contains("Approval is required", StringComparison.Ordinal));
    }

    [Fact]
    public void Null_signal_collection_is_rejected_without_throwing()
    {
        MigrationAssessmentRequest request = Requests.Build(
        [
            Requests.Evidence("EV-1", EvidenceKind.FormsModuleInventory) with { Signals = null! },
        ]);

        RequestValidationResult result = RequestValidator.Validate(request);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Contains("Signals must be an array", StringComparison.Ordinal));
    }

    [Fact]
    public void Undefined_enum_values_are_rejected()
    {
        MigrationAssessmentRequest request = Requests.Build(
        [
            Requests.Evidence("EV-1", (EvidenceKind)999, true, (WorkloadSignal)998),
        ],
            new HumanApproval { Decision = (ApprovalDecision)997 });

        RequestValidationResult result = RequestValidator.Validate(request);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Contains("unsupported Kind", StringComparison.Ordinal));
        Assert.Contains(result.Errors, error => error.Contains("unsupported signal", StringComparison.Ordinal));
        Assert.Contains(result.Errors, error => error.Contains("unsupported Decision", StringComparison.Ordinal));
    }

    [Fact]
    public void Credential_material_in_approval_notes_is_rejected()
    {
        MigrationAssessmentRequest request = Requests.Build(
            Requests.CompleteEvidence(WorkloadSignal.SelfContainedSchema),
            new HumanApproval { Decision = ApprovalDecision.Rejected, Notes = "password=hunter2" });

        RequestValidationResult result = RequestValidator.Validate(request);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("Approval details", StringComparison.Ordinal));
    }
}

public class FleetRoleCatalogTests
{
    [Fact]
    public void Every_role_has_a_definition()
    {
        foreach (FleetRole role in Enum.GetValues<FleetRole>())
        {
            FleetRoleDefinition definition = FleetRoleCatalog.Get(role);
            Assert.Equal(role, definition.Role);
            Assert.False(string.IsNullOrWhiteSpace(definition.Instructions));
            Assert.NotEmpty(definition.Guardrails);
        }
    }

    [Fact]
    public void Outer_agent_instructions_state_the_security_boundaries()
    {
        string instructions = FleetAgentInstructions.Build();

        Assert.All(FleetGuardrails.Boundaries, b => Assert.Contains(b, instructions, StringComparison.Ordinal));
        Assert.Contains("assess_oracle_forms_migration", instructions, StringComparison.Ordinal);
        Assert.Contains("ASSUMPTIONS", instructions, StringComparison.Ordinal);
        Assert.Contains("BLOCKERS", instructions, StringComparison.Ordinal);
    }

    [Fact]
    public void Outer_agent_instructions_never_allow_executable_sql()
    {
        string instructions = FleetAgentInstructions.Build();

        Assert.Contains("Never emit executable DDL, DML, or migration scripts", instructions, StringComparison.Ordinal);
        Assert.DoesNotContain("unless the corresponding source artifacts", instructions, StringComparison.Ordinal);
    }

    [Fact]
    public void Validation_reviewer_allows_explicit_planning_only_tasks()
    {
        FleetRoleDefinition reviewer = FleetRoleCatalog.Get(FleetRole.ValidationReviewer);

        Assert.Contains("Planning-only tasks", reviewer.Instructions, StringComparison.Ordinal);
        Assert.Contains("may have no evidence", reviewer.Instructions, StringComparison.Ordinal);
        Assert.DoesNotContain("if a conversion task lacks supporting evidence", reviewer.Instructions, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(FleetRole.DocumentationAuthor)]
    [InlineData(FleetRole.ApplicationCodeConverter)]
    public void Forms_consuming_roles_advertise_both_accepted_forms_source_kinds(FleetRole role)
    {
        FleetRoleDefinition definition = FleetRoleCatalog.Get(role);

        Assert.Contains(EvidenceKind.FormsModuleSource, definition.RequiredEvidence);
        Assert.Contains(EvidenceKind.FormsXmlExport, definition.RequiredEvidence);
        Assert.Contains(
            $"{nameof(EvidenceKind.FormsModuleSource)} or {nameof(EvidenceKind.FormsXmlExport)}",
            definition.Instructions,
            StringComparison.Ordinal);
    }
}
