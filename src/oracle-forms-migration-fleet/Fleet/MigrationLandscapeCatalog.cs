namespace OracleFormsMigrationFleet.Fleet;

/// <summary>Source-backed tool boundaries and exit strategies surfaced by the outer agent.</summary>
public static class MigrationLandscapeCatalog
{
    public static IReadOnlyList<EvidenceKind> RecommendedExitEvidence { get; } =
    [
        EvidenceKind.BusinessProcessCatalog,
        EvidenceKind.IntegrationInventory,
        EvidenceKind.AuthenticationTopology,
        EvidenceKind.DataProfile,
        EvidenceKind.TestBaseline,
        EvidenceKind.CutoverAndRollbackPlan,
        EvidenceKind.LicensingAndSupportPosition,
        EvidenceKind.UsageAndBusinessValue,
    ];

    public static IReadOnlyList<MigrationToolDefinition> Tools { get; } =
    [
        new("Oracle Forms 14.1.2 upgrade tooling", ToolAuthority.Oracle,
            "Stabilize or upgrade old Forms applications, including 6i and older sources, before a longer exit.",
            "An upgrade preserves the Forms architecture and Oracle dependencies; it is a risk-reduction option, not an Azure SQL conversion.",
            "https://docs.oracle.com/en/middleware/developer-tools/forms/14.1.2/upgrade-forms/index.html"),
        new("Oracle Forms XML Converter / Forms2XML and JDAPI", ToolAuthority.Oracle,
            "Extract FormModule, MenuModule, and ObjectLibrary structure and properties for inventory and dependency analysis.",
            "Requires authoritative source and compatible Forms tooling. XML metadata does not capture observed user workflows or prove behavioral equivalence.",
            "https://docs.oracle.com/en/middleware/developer-tools/forms/14.1.2/index.html"),
        new("SQL Server Migration Assistant (SSMA) for Oracle", ToolAuthority.Microsoft,
            "Assess and convert Oracle database schema and code, review type mappings, and migrate data to Azure SQL targets.",
            "Does not convert Oracle Forms UI/runtime behavior. Conversion reports require manual remediation and testing; SYS and SYSTEM schemas cannot be migrated.",
            "https://learn.microsoft.com/sql/ssma/oracle/migrating-oracle-databases-to-sql-server-oracletosql"),
        new("SSMA Tester for Oracle", ToolAuthority.Microsoft,
            "Compare converted database-object behavior and migrated data.",
            "Database comparison is not end-to-end Forms workflow, UX, security, concurrency, or performance validation.",
            "https://learn.microsoft.com/sql/ssma/oracle/testing-migrated-database-objects-oracletosql"),
        new("Azure Database Migration Service through SSMA", ToolAuthority.Microsoft,
            "Scale one-time Oracle data movement to Azure SQL Database or Azure SQL Managed Instance.",
            "Oracle source support is offline preview. It does not provide continuous synchronization for a low-downtime coexistence cutover.",
            "https://learn.microsoft.com/azure/dms/resource-scenario-status"),
        new("Azure Migrate and GitHub Copilot modernization", ToolAuthority.Microsoft,
            "Discover infrastructure and assess supported ASP.NET or Java application code for Azure targets.",
            "No official Oracle Forms code-conversion path is documented; do not infer Forms support from .NET or Java modernization features.",
            "https://learn.microsoft.com/azure/migrate/add-copilot-code-insights"),
        new("Oracle APEX modernization", ToolAuthority.Oracle,
            "Replace Forms with a web application while retaining an Oracle-centric platform and PL/SQL investment.",
            "This is not an exit to Azure SQL. Forms interaction patterns still require redesign and regression testing.",
            "https://blogs.oracle.com/apex/post/modernizing-oracle-forms-using-oracle-apex"),
        new("Oracle APEX Migration Workbench (desupported)", ToolAuthority.Oracle,
            "Historical context only; older APEX releases exposed an Application Migration workbench.",
            "Oracle desupported the Migration Workbench in APEX 21.1. Do not plan a current Forms migration around that workflow or its generated placeholders.",
            "https://docs.oracle.com/en/database/oracle/application-express/21.1/htmrn/"),
        new("KodeSage Oracle Forms migration", ToolAuthority.VendorClaim,
            "Vendor-claimed discovery, dependency mapping, complexity classification, phased planning, AI-assisted conversion, visual regression testing, and data-state comparison for APEX, Java, .NET, and web front ends.",
            "Automation by complexity, months-not-years timelines, cost/ROI, workflow extraction, vision testing, and production-support outcomes are vendor claims. Require a representative paid proof, reproducible dependency output, source ownership, generated-code review, test evidence, accessibility/security/performance validation, and export/exit terms.",
            "https://kodesage.ai/blog/oracle-forms-migration"),
        new("ORMIT OpenJava", ToolAuthority.VendorClaim,
            "Article-reported automated conversion of Oracle Forms toward Java with React or Angular front ends.",
            "The cited article states testing and UAT remain manual. Confirm current product capability directly with the vendor and prove generated-code maintainability, semantic fidelity, licensing, support, and difficult-module coverage.",
            "https://kodesage.ai/blog/oracle-forms-migration"),
        new("PITSS modernization products", ToolAuthority.VendorClaim,
            "Vendor-assisted inventory, analysis, upgrade, and Forms-to-APEX modernization.",
            "Automation and conversion coverage are vendor claims; validate against representative modules, shared libraries, integrations, and acceptance tests.",
            "https://pitss.com/oracle-forms-modernization/"),
        new("AuraPlayer", ToolAuthority.VendorClaim,
            "Expose or wrap existing Forms flows as services and mobile/web experiences during staged modernization.",
            "Wrapping can accelerate coexistence but retains the Forms runtime and Oracle coupling; it is not full retirement by itself.",
            "https://auraplayer.com/forms-modernization/"),
        new("GAPVelocity / Forms2Net", ToolAuthority.VendorClaim,
            "Vendor-claimed automated conversion of Oracle Forms to .NET and web UI stacks.",
            "Generated-code percentage is not acceptance quality. Require a paid proof using the hardest modules and assess maintainability, semantics, licensing, and vendor lock-in.",
            "https://www.gapvelocity.ai/oracle-forms-migration/"),
        new("Ispirer", ToolAuthority.VendorClaim,
            "Automate portions of Oracle schema, PL/SQL, and data conversion to Microsoft data platforms.",
            "Treat as database/code acceleration rather than Forms UI modernization; validate unsupported constructs and generated-code quality.",
            "https://www.ispirer.com/products/migration-to-azure"),
        new("Pretius Oracle Forms migration guidance", ToolAuthority.VendorClaim,
            "Vendor guidance on Forms exit options, with technical prompts about triggers, blocks, reports, and target-stack selection that are useful for risk enumeration.",
            "Timeline, cost, licensing, scale, and commercial claims are vendor claims requiring independent verification. The article is guidance and not execution proof, and it is not end-to-end conversion proof.",
            "https://pretius.com/blog/migrating-oracle-forms"),
        new("aoreshkov/oracle-forms-mcp", ToolAuthority.OpenSourceImplementation,
            "Apache-2.0 Kotlin/JVM parser and indexer over Forms modules; a candidate front end for inventory and dependency indexing.",
            "Requires JDK/JRE 21 or later and the licensed Oracle frmf2xml/frmcmp_batch utilities via ORACLE_HOME, or adjacent pre-converted XML/PLD input. It does not generate React or Java target code and does not migrate databases; it is not end-to-end conversion proof.",
            "https://github.com/aoreshkov/oracle-forms-mcp"),
        new("felipebz/ndapi", ToolAuthority.OpenSourceImplementation,
            "MIT-licensed .NET wrapper over Oracle Forms Open API; a candidate out-of-process extractor for FMB, MMB, OLB, and PLL design-time metadata and source text.",
            "Requires matching Oracle native libraries. Documented support is Forms 6.0.8.22.1 on Windows x86 and the stable 12c builds 12.2.1.3, 12.2.1.4, and 12.2.1.19 on Windows and Linux x64, with 14.1.2.0 present in current pre-release source only. The upstream solution contains no test project, and its CI compiles the wrapper without loading the Oracle native libraries, so nothing published there proves native Forms execution against any of those builds. It exposes mutating, save, compile, conversion, and database-connect APIs, so the fleet must use a read-only allowlisted worker with no credentials or network. ASP.NET Core and Blazor output is roadmap intent only: it does not generate ASP.NET, Blazor, React, or Java target code today, this fleet generates none from it, it does not cover 9i-11g directly, and it is not end-to-end conversion proof.",
            "https://github.com/felipebz/ndapi"),
        new("Ora2Pg", ToolAuthority.OpenSourceImplementation,
            "GPL-3.0 Oracle-to-PostgreSQL assessment, schema and data conversion, partial PL/SQL conversion, and a validation aid for converted objects.",
            "Unconverted constructs require manual remediation. It has no Oracle Forms UI capability and is not end-to-end conversion proof.",
            "https://github.com/darold/ora2pg"),
        new("franklingjr/oracle-forms-migration", ToolAuthority.OpenSourceImplementation,
            "MIT-licensed utility that reads Oracle Forms Object List Reports and produces an Oracle PL/SQL package for APEX-oriented work.",
            "Object List Report parsing is a limited fallback when Forms XML is unavailable. It does not produce React or Java target code, is not an Oracle database exit, and is not end-to-end conversion proof.",
            "https://github.com/franklingjr/oracle-forms-migration"),
        new("Cognition workshop HRMS/workflow estate", ToolAuthority.WorkshopReference,
            "Synthetic single-commit static source estate usable as static-analysis or pilot input only.",
            "Verified tree contains 5 Forms XML files, 2 PLLs expressed as SQL, 1 menu expressed as SQL, packages and triggers, and schema plus seed SQL. There is no .fmb, no runtime, no regression baseline, no setup runner, and no standalone LICENSE text; the README's MIT statement is not sufficient redistribution proof. It is not end-to-end conversion proof.",
            "https://github.com/Cognition-Partner-Workshops/ts-plsql-oracle-forms-hrms"),
        new("SierraSystems Oracle Forms reference application", ToolAuthority.WorkshopReference,
            "Old architecture reference showing .fmb modules, an Oracle schema with data, and Java/Quarkus plus React layers.",
            "The reference stays Oracle-backed rather than demonstrating a database exit, and no clear license was verified. Treat as illustrative architecture only; it is not reusable proof and not end-to-end conversion proof.",
            "https://github.com/SierraSystems/Oracle-Modernization"),
        new("armandoblanco/legacy-modernization-playbook Oracle Forms agent", ToolAuthority.WorkshopReference,
            "Pilot-first process guidance: phased sequencing, Forms-to-target mapping conventions, and parity checkpoints for an agent-driven Forms modernization.",
            "Process, mapping, and parity guidance only. It is not validated executable tooling, ships no runnable converter, and is not end-to-end conversion proof; every mapping still requires a customer-owned pilot with its own regression baseline.",
            "https://github.com/armandoblanco/legacy-modernization-playbook/blob/main/.github/agents/java/oracle-forms-migration.agent.md"),
        new("patrickmonaco/formstools", ToolAuthority.WorkshopReference,
            "Historical reference for the obsolete APEX Forms Migration loader workflow.",
            "The loader is obsolete and requires high-privilege database access. Do not plan a current migration around it; it is not end-to-end conversion proof.",
            "https://github.com/patrickmonaco/formstools"),
    ];

    public static IReadOnlyList<ExitStrategyDefinition> ExitStrategies { get; } =
    [
        new("Retire and archive", "Low/no usage or retention-only workloads.",
            "Requires owner sign-off, defensible retention, searchable read-only records, legal hold, audit, and license decommissioning."),
        new("Replace with SaaS or packaged software", "Commodity processes with a credible fit-to-standard product.",
            "Prove critical workflows, integration, data export, identity, compliance, and total cost; avoid recreating every legacy customization."),
        new("Stabilize and upgrade", "Unsupported Forms versions need risk reduction before a later exit.",
            "Time-box the bridge and fund the exit roadmap; otherwise the upgrade becomes indefinite platform retention."),
        new("Encapsulate behind APIs", "A staged program needs to decouple consumers before replacing Forms or Oracle.",
            "Defines a boundary but retains legacy runtime risk and may create a distributed monolith without ownership and observability."),
        new("Strangler migration by business capability", "Large estates need incremental value delivery and bounded rollback.",
            "Requires system-of-record ownership, coexistence synchronization, reconciliation, compatibility contracts, and per-wave cutover."),
        new("Full replacement", "The legacy process and technology both require redesign and a bounded parallel program is feasible.",
            "Highest delivery and cutover risk; requires characterization tests, process owners, staged data rehearsals, performance proof, and rollback."),
    ];

    public static IReadOnlyList<string> MandatoryHumanReviews { get; } =
    [
        "Business owners must decide which workflows to retire, simplify, replace, or preserve; source code cannot establish business value.",
        "Security must approve identity, authorization, VPD/RLS parity, secrets, desktop/device integration, and segregation of duties.",
        "Data owners must approve type mappings, null/NLS semantics, reconciliation tolerances, retention, cutover, and rollback.",
        "Users must validate workflow and output fidelity; generated code and object conversion percentages are not acceptance evidence.",
        "Architecture owners must version global design rules and form-level exceptions for navigation, transaction boundaries, locking, security, and client-versus-service logic before applying automated conversion at scale.",
        "Delivery owners must cluster forms, shared libraries, reports, and database objects by dependency, then define independently testable migration waves with coexistence ownership and rollback.",
        "Recorded expert workflows and vision-based comparisons can supplement source analysis, but do not replace reproducible functional, data, accessibility, security, concurrency, performance, and user-acceptance evidence.",
        "Operations must prove observability, support, backup/restore, disaster recovery, batch scheduling, capacity, and licensing before production approval.",
    ];
}

public enum ToolAuthority
{
    Oracle,
    Microsoft,
    VendorClaim,
    OpenSourceImplementation,
    WorkshopReference,
}

public sealed record MigrationToolDefinition(
    string Name,
    ToolAuthority Authority,
    string BestFor,
    string Limitations,
    string SourceUrl);

public sealed record ExitStrategyDefinition(string Name, string WhenToUse, string Caveats);

public sealed record MigrationLandscape(
    IReadOnlyList<MigrationToolDefinition> Tools,
    IReadOnlyList<ExitStrategyDefinition> ExitStrategies,
    IReadOnlyList<LegacyRiskDescription> LegacyRisks,
    IReadOnlyList<string> MandatoryHumanReviews);

public sealed record LegacyRiskDescription(
    WorkloadSignal Signal,
    Severity Severity,
    string Detail,
    IReadOnlyList<EvidenceKind> PreferredEvidence);