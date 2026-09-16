// Copyright (c) Microsoft. All rights reserved.

using OracleFormsMigrationFleet.Fleet;

namespace OracleFormsMigrationFleet.Tests;

/// <summary>Request builders for offline orchestration tests. No Azure or model access.</summary>
internal static class Requests
{
    public static EvidenceItem Evidence(
        string id,
        EvidenceKind kind,
        bool verified = true,
        params WorkloadSignal[] signals) => new()
        {
            Id = id,
            Kind = kind,
            Source = $"{id}.export",
            Summary = $"{kind} artifact for testing.",
            IsVerified = verified,
            Signals = signals,
        };

    /// <summary>Evidence sufficient to reach the human approval gate with a determinate recommendation.</summary>
    public static IReadOnlyList<EvidenceItem> CompleteEvidence(params WorkloadSignal[] signals) =>
    [
        Evidence("EV-INV", EvidenceKind.FormsModuleInventory),
        Evidence("EV-SRC", EvidenceKind.FormsModuleSource),
        Evidence("EV-PLSQL", EvidenceKind.PlSqlProgramUnit),
        Evidence("EV-SCHEMA", EvidenceKind.DatabaseSchemaExport),
        Evidence("EV-PROFILE", EvidenceKind.WorkloadProfile, true, signals),
        Evidence("EV-PROCESS", EvidenceKind.BusinessProcessCatalog),
        Evidence("EV-INTEGRATION", EvidenceKind.IntegrationInventory),
        Evidence("EV-AUTH", EvidenceKind.AuthenticationTopology),
        Evidence("EV-DATA", EvidenceKind.DataProfile),
        Evidence("EV-TEST", EvidenceKind.TestBaseline),
        Evidence("EV-CUTOVER", EvidenceKind.CutoverAndRollbackPlan),
        Evidence("EV-LICENSE", EvidenceKind.LicensingAndSupportPosition),
        Evidence("EV-VALUE", EvidenceKind.UsageAndBusinessValue),
    ];

    public static MigrationAssessmentRequest Build(
        IReadOnlyList<EvidenceItem>? evidence = null,
        HumanApproval? approval = null,
        string engagementId = "ENG-001",
        string applicationName = "ORDERS",
        string oracleFormsVersion = "12c",
        string oracleDatabaseVersion = "19c") => new()
        {
            EngagementId = engagementId,
            ApplicationName = applicationName,
            OracleFormsVersion = oracleFormsVersion,
            OracleDatabaseVersion = oracleDatabaseVersion,
            Evidence = evidence ?? [],
            Approval = approval ?? HumanApproval.Pending,
        };

    public static HumanApproval Approved(string approver = "alice@contoso.com") =>
        new() { Decision = ApprovalDecision.Approved, ApproverId = approver };
}
