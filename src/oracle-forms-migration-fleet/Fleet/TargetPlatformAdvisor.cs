// Copyright (c) Microsoft. All rights reserved.

namespace OracleFormsMigrationFleet.Fleet;

/// <summary>
/// Deterministic Azure SQL Database vs Azure SQL Managed Instance decision. Every criterion is
/// explicit, cites the evidence it came from, and is evaluated without any model or network call.
/// </summary>
public static class TargetPlatformAdvisor
{
    /// <summary>Signals that make Azure SQL Database unusable and therefore force Managed Instance.</summary>
    public static IReadOnlyDictionary<WorkloadSignal, string> HardManagedInstanceConstraints { get; } =
        new Dictionary<WorkloadSignal, string>
        {
            [WorkloadSignal.SqlAgentRequired] = "A validated requirement to preserve SQL Agent scheduling in the database requires Azure SQL Managed Instance.",
            [WorkloadSignal.ClrRequiredInDatabase] = "A validated requirement to preserve CLR execution in the database requires Azure SQL Managed Instance.",
            [WorkloadSignal.ServiceBrokerRequired] = "Service Broker is only available on Azure SQL Managed Instance.",
            [WorkloadSignal.InstanceLevelCollationRequired] = "Instance-level collation control requires Azure SQL Managed Instance.",
        };

    /// <summary>Signals that lean toward, but do not mandate, a platform.</summary>
    public static IReadOnlyDictionary<WorkloadSignal, (TargetPlatform Platform, string Rationale)> SoftIndicators { get; } =
        new Dictionary<WorkloadSignal, (TargetPlatform, string)>
        {
            [WorkloadSignal.ScheduledDatabaseJobs] = (TargetPlatform.AzureSqlManagedInstance,
                "Existing scheduled database jobs favor Managed Instance only when preserving SQL Agent semantics is preferable to redesigning scheduling outside the database."),
            [WorkloadSignal.ClrOrExternalAssemblies] = (TargetPlatform.AzureSqlManagedInstance,
                "Existing CLR or external assemblies favor Managed Instance only after confirming they cannot be re-hosted in an application or integration service."),
            [WorkloadSignal.CrossDatabaseQueries] = (TargetPlatform.AzureSqlManagedInstance,
                "Preserving broad same-instance cross-database query compatibility favors Managed Instance; Azure SQL Database requires constrained alternatives such as elastic query or application-level composition."),
            [WorkloadSignal.DistributedTransactions] = (TargetPlatform.AzureSqlManagedInstance,
                "Broad distributed-transaction compatibility favors Managed Instance; Azure SQL Database supports narrower elastic transaction scenarios that require separate validation."),
            [WorkloadSignal.VnetIsolationRequired] = (TargetPlatform.AzureSqlManagedInstance,
                "Managed Instance is VNet-native; Azure SQL Database needs private endpoints to match."),
            [WorkloadSignal.LargeDatabaseFootprint] = (TargetPlatform.AzureSqlManagedInstance,
                "Large consolidated footprints fit the Managed Instance instance-scoped storage model."),
            [WorkloadSignal.SelfContainedSchema] = (TargetPlatform.AzureSqlDatabase,
                "A self-contained schema has no instance-scoped dependencies."),
            [WorkloadSignal.PerDatabaseElasticScale] = (TargetPlatform.AzureSqlDatabase,
                "Per-database elastic scaling is a first-class Azure SQL Database capability."),
            [WorkloadSignal.ServerlessCostSensitivity] = (TargetPlatform.AzureSqlDatabase,
                "Serverless auto-pause is only offered by Azure SQL Database."),
        };

    private static IReadOnlyDictionary<WorkloadSignal, IReadOnlySet<EvidenceKind>> SignalEvidenceKinds { get; } =
        new Dictionary<WorkloadSignal, IReadOnlySet<EvidenceKind>>
        {
            [WorkloadSignal.SqlAgentRequired] = Set(EvidenceKind.ScheduledJobInventory, EvidenceKind.WorkloadProfile),
            [WorkloadSignal.ScheduledDatabaseJobs] = Set(EvidenceKind.ScheduledJobInventory, EvidenceKind.WorkloadProfile),
            [WorkloadSignal.ClrRequiredInDatabase] = Set(EvidenceKind.DatabaseSchemaExport, EvidenceKind.ExternalProcedureUsage, EvidenceKind.WorkloadProfile),
            [WorkloadSignal.ClrOrExternalAssemblies] = Set(EvidenceKind.DatabaseSchemaExport, EvidenceKind.ExternalProcedureUsage, EvidenceKind.WorkloadProfile),
            [WorkloadSignal.ServiceBrokerRequired] = Set(EvidenceKind.DatabaseSchemaExport, EvidenceKind.WorkloadProfile),
            [WorkloadSignal.InstanceLevelCollationRequired] = Set(EvidenceKind.DatabaseSchemaExport, EvidenceKind.WorkloadProfile),
            [WorkloadSignal.CrossDatabaseQueries] = Set(EvidenceKind.DatabaseSchemaExport, EvidenceKind.PlSqlProgramUnit, EvidenceKind.WorkloadProfile),
            [WorkloadSignal.DistributedTransactions] = Set(EvidenceKind.PlSqlProgramUnit, EvidenceKind.IntegrationInventory, EvidenceKind.WorkloadProfile),
            [WorkloadSignal.VnetIsolationRequired] = Set(EvidenceKind.NetworkTopology, EvidenceKind.ComplianceConstraint, EvidenceKind.WorkloadProfile),
            [WorkloadSignal.LargeDatabaseFootprint] = Set(EvidenceKind.DataProfile, EvidenceKind.WorkloadProfile),
            [WorkloadSignal.SelfContainedSchema] = Set(EvidenceKind.DatabaseSchemaExport, EvidenceKind.WorkloadProfile),
            [WorkloadSignal.PerDatabaseElasticScale] = Set(EvidenceKind.WorkloadProfile),
            [WorkloadSignal.ServerlessCostSensitivity] = Set(EvidenceKind.WorkloadProfile, EvidenceKind.UsageAndBusinessValue),
            [WorkloadSignal.DatabaseLinksInUse] = Set(EvidenceKind.DatabaseLinkUsage, EvidenceKind.DatabaseSchemaExport, EvidenceKind.PlSqlProgramUnit),
            [WorkloadSignal.FileSystemAccess] = Set(EvidenceKind.FormsModuleSource, EvidenceKind.FormsXmlExport, EvidenceKind.PlSqlProgramUnit, EvidenceKind.IntegrationInventory),
            [WorkloadSignal.ExternalProcedureCalls] = Set(EvidenceKind.ExternalProcedureUsage, EvidenceKind.DatabaseSchemaExport, EvidenceKind.PlSqlProgramUnit),
        };

    public static bool IsAuthoritativeEvidence(WorkloadSignal signal, EvidenceKind kind) =>
        !SignalEvidenceKinds.TryGetValue(signal, out IReadOnlySet<EvidenceKind>? allowedKinds) ||
        allowedKinds.Contains(kind);

    /// <summary>Signals that must be redesigned regardless of the target platform.</summary>
    public static IReadOnlyDictionary<WorkloadSignal, string> PlatformAgnosticBlockers { get; } =
        new Dictionary<WorkloadSignal, string>
        {
            [WorkloadSignal.DatabaseLinksInUse] = "Oracle database links do not map directly to either target; each remote dependency and replacement integration pattern must be validated.",
            [WorkloadSignal.FileSystemAccess] = "Host file-system access has no equivalent on either target and must be redesigned.",
            [WorkloadSignal.ExternalProcedureCalls] = "External procedure calls must be re-hosted outside the database on either target.",
        };

    public static PlatformRecommendation Recommend(IReadOnlyList<EvidenceItem> evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);

        List<PlatformCriterionResult> criteria = [];
        List<string> assumptions = [];
        List<string> blockers = [];

        Dictionary<WorkloadSignal, List<string>> signalSources = [];
        foreach (EvidenceItem item in evidence)
        {
            foreach (WorkloadSignal signal in item.Signals)
            {
                if (!item.IsVerified &&
                    (HardManagedInstanceConstraints.ContainsKey(signal) ||
                     SoftIndicators.ContainsKey(signal) ||
                     PlatformAgnosticBlockers.ContainsKey(signal)))
                {
                    assumptions.Add($"Ignored signal '{signal}' from evidence '{item.Id}' because the artifact is not verified.");
                    continue;
                }

                if (!IsAuthoritativeEvidence(signal, item.Kind))
                {
                    assumptions.Add($"Ignored signal '{signal}' from evidence '{item.Id}' because {item.Kind} is not an authoritative artifact kind for that platform criterion.");
                    continue;
                }

                if (!signalSources.TryGetValue(signal, out List<string>? ids))
                {
                    ids = [];
                    signalSources[signal] = ids;
                }

                ids.Add(item.Id);
            }
        }

        foreach ((WorkloadSignal signal, string rationale) in HardManagedInstanceConstraints)
        {
            if (signalSources.TryGetValue(signal, out List<string>? ids))
            {
                criteria.Add(new PlatformCriterionResult(
                    signal.ToString(), TargetPlatform.AzureSqlManagedInstance, IsHardConstraint: true, rationale,
                    [.. ids.Distinct(StringComparer.Ordinal)]));
            }
        }

        foreach ((WorkloadSignal signal, (TargetPlatform platform, string rationale)) in SoftIndicators)
        {
            if (signalSources.TryGetValue(signal, out List<string>? ids))
            {
                criteria.Add(new PlatformCriterionResult(
                    signal.ToString(), platform, IsHardConstraint: false, rationale,
                    [.. ids.Distinct(StringComparer.Ordinal)]));
            }
        }

        foreach ((WorkloadSignal signal, string rationale) in PlatformAgnosticBlockers)
        {
            if (signalSources.TryGetValue(signal, out List<string>? ids))
            {
                blockers.Add($"{rationale} (evidence: {string.Join(", ", ids.Distinct(StringComparer.Ordinal))})");
            }
        }

        bool hasWorkloadProfile = evidence.Any(e => e.Kind == EvidenceKind.WorkloadProfile && e.IsVerified);
        if (!hasWorkloadProfile)
        {
            blockers.Add($"No {nameof(EvidenceKind.WorkloadProfile)} evidence supplied; platform sizing and scale criteria could not be evaluated.");
        }

        if (criteria.Count == 0)
        {
            blockers.Add("No workload signals were extracted from the supplied evidence; the target platform cannot be determined.");
            return new PlatformRecommendation(
                TargetPlatform.Undetermined, ConfidenceLevel.Insufficient, criteria, assumptions, blockers);
        }

        bool hasHardConstraint = criteria.Any(c => c.IsHardConstraint);
        int managedInstanceVotes = criteria.Count(c => !c.IsHardConstraint && c.Indicates == TargetPlatform.AzureSqlManagedInstance);
        int sqlDatabaseVotes = criteria.Count(c => !c.IsHardConstraint && c.Indicates == TargetPlatform.AzureSqlDatabase);

        TargetPlatform recommended;
        ConfidenceLevel confidence;

        if (hasHardConstraint)
        {
            recommended = TargetPlatform.AzureSqlManagedInstance;
            confidence = ConfidenceLevel.High;
            if (sqlDatabaseVotes > 0)
            {
                assumptions.Add("Azure SQL Database indicators were overridden by at least one hard Managed Instance constraint.");
            }
        }
        else if (managedInstanceVotes == sqlDatabaseVotes)
        {
            recommended = TargetPlatform.Undetermined;
            confidence = ConfidenceLevel.Low;
            blockers.Add("Soft indicators are evenly split between the two targets; more workload evidence is required.");
        }
        else
        {
            recommended = managedInstanceVotes > sqlDatabaseVotes
                ? TargetPlatform.AzureSqlManagedInstance
                : TargetPlatform.AzureSqlDatabase;
            confidence = ConfidenceLevel.Medium;
        }

        if (!hasWorkloadProfile && confidence > ConfidenceLevel.Low)
        {
            confidence = ConfidenceLevel.Low;
            assumptions.Add("Confidence was reduced because no workload profile evidence was supplied.");
        }

        return new PlatformRecommendation(recommended, confidence, criteria, assumptions, blockers);
    }

    private static IReadOnlySet<EvidenceKind> Set(params EvidenceKind[] kinds) =>
        new HashSet<EvidenceKind>(kinds);
}
