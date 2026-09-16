// Copyright (c) Microsoft. All rights reserved.

using OracleFormsMigrationFleet.Fleet;
using System.Text.Json;

namespace OracleFormsMigrationFleet.Tests;

public class FleetToolsTests
{
    [Fact]
    public void Tool_catalog_can_be_created()
    {
        var tools = FleetTools.Create();

        Assert.Equal(5, tools.Count);
    }

    [Fact]
    public void Migration_landscape_separates_official_tools_from_vendor_claims()
    {
        MigrationLandscape landscape = FleetTools.DescribeMigrationLandscape();

        Assert.Contains(landscape.Tools, tool =>
            tool.Name.Contains("SSMA", StringComparison.Ordinal) &&
            tool.Authority == ToolAuthority.Microsoft &&
            tool.Limitations.Contains("Forms UI", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(landscape.Tools, tool => tool.Authority == ToolAuthority.VendorClaim);
        Assert.Contains(landscape.Tools, tool =>
            tool.Name.Contains("KodeSage", StringComparison.OrdinalIgnoreCase) &&
            tool.Authority == ToolAuthority.VendorClaim &&
            tool.Limitations.Contains("vendor claims", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(landscape.Tools, tool =>
            tool.Name.Contains("ORMIT", StringComparison.Ordinal) &&
            tool.Authority == ToolAuthority.VendorClaim &&
            tool.Limitations.Contains("UAT remain manual", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(landscape.Tools, tool =>
            tool.Name.Contains("Migration Workbench", StringComparison.Ordinal) &&
            tool.Authority == ToolAuthority.Oracle &&
            tool.Limitations.Contains("desupported", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(landscape.ExitStrategies, strategy => strategy.Name == "Retire and archive");
        Assert.Contains(landscape.LegacyRisks, risk => risk.Signal == WorkloadSignal.WebUtilOleOrJacob);
        Assert.Contains(landscape.LegacyRisks, risk => risk.Signal == WorkloadSignal.MultiRecordBlocks);
        Assert.Contains(landscape.LegacyRisks, risk => risk.Signal == WorkloadSignal.EnterQueryMode);
        Assert.Contains(landscape.LegacyRisks, risk => risk.Signal == WorkloadSignal.PostQueryLogic);
        Assert.NotEmpty(landscape.MandatoryHumanReviews);
    }

    [Theory]
    [InlineData("aoreshkov/oracle-forms-mcp", ToolAuthority.OpenSourceImplementation)]
    [InlineData("felipebz/ndapi", ToolAuthority.OpenSourceImplementation)]
    [InlineData("Ora2Pg", ToolAuthority.OpenSourceImplementation)]
    [InlineData("franklingjr/oracle-forms-migration", ToolAuthority.OpenSourceImplementation)]
    [InlineData("Cognition workshop", ToolAuthority.WorkshopReference)]
    [InlineData("SierraSystems", ToolAuthority.WorkshopReference)]
    [InlineData("patrickmonaco/formstools", ToolAuthority.WorkshopReference)]
    [InlineData("armandoblanco/legacy-modernization-playbook", ToolAuthority.WorkshopReference)]
    [InlineData("Pretius", ToolAuthority.VendorClaim)]
    public void Researched_reference_sources_carry_their_researched_authority(string name, ToolAuthority authority)
    {
        MigrationLandscape landscape = FleetTools.DescribeMigrationLandscape();

        MigrationToolDefinition tool = Assert.Single(
            landscape.Tools, t => t.Name.Contains(name, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(authority, tool.Authority);
        Assert.Contains("not end-to-end conversion proof", tool.Limitations, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The seven third-party sources supplied for this fleet, each with the authority it was researched under.</summary>
    [Theory]
    [InlineData("https://github.com/aoreshkov/oracle-forms-mcp", ToolAuthority.OpenSourceImplementation)]
    [InlineData("https://github.com/franklingjr/oracle-forms-migration", ToolAuthority.OpenSourceImplementation)]
    [InlineData("https://github.com/patrickmonaco/formstools", ToolAuthority.WorkshopReference)]
    [InlineData("https://github.com/Cognition-Partner-Workshops/ts-plsql-oracle-forms-hrms", ToolAuthority.WorkshopReference)]
    [InlineData("https://github.com/SierraSystems/Oracle-Modernization", ToolAuthority.WorkshopReference)]
    [InlineData("https://github.com/armandoblanco/legacy-modernization-playbook/blob/main/.github/agents/java/oracle-forms-migration.agent.md", ToolAuthority.WorkshopReference)]
    [InlineData("https://pretius.com/blog/migrating-oracle-forms", ToolAuthority.VendorClaim)]
    public void Every_supplied_source_is_catalogued_under_its_researched_authority(string sourceUrl, ToolAuthority authority)
    {
        MigrationToolDefinition tool = Assert.Single(
            FleetTools.DescribeMigrationLandscape().Tools,
            t => string.Equals(t.SourceUrl, sourceUrl, StringComparison.Ordinal));

        Assert.Equal(authority, tool.Authority);
    }

    [Fact]
    public void Every_catalogued_tool_cites_an_absolute_http_source_url()
    {
        Assert.All(FleetTools.DescribeMigrationLandscape().Tools, tool =>
        {
            Assert.True(
                Uri.TryCreate(tool.SourceUrl, UriKind.Absolute, out Uri? uri) &&
                (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps),
                $"{tool.Name} must cite an absolute http/https source URL but cited '{tool.SourceUrl}'.");
        });
    }

    [Fact]
    public void Armando_playbook_is_process_guidance_and_pretius_is_an_unverified_vendor_claim()
    {
        MigrationLandscape landscape = FleetTools.DescribeMigrationLandscape();

        MigrationToolDefinition playbook = Assert.Single(
            landscape.Tools, t => t.Name.Contains("armandoblanco", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("pilot-first", playbook.BestFor, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("not validated executable tooling", playbook.Limitations, StringComparison.OrdinalIgnoreCase);

        MigrationToolDefinition pretius = Assert.Single(
            landscape.Tools, t => t.Name.Contains("Pretius", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("independent verification", pretius.Limitations, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("not execution proof", pretius.Limitations, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Forms_mcp_entry_records_its_oracle_home_and_target_code_boundary()
    {
        MigrationToolDefinition tool = Assert.Single(
            FleetTools.DescribeMigrationLandscape().Tools,
            t => t.Name.Contains("oracle-forms-mcp", StringComparison.OrdinalIgnoreCase));

        Assert.Contains("ORACLE_HOME", tool.Limitations, StringComparison.Ordinal);
        Assert.Contains("XML/PLD", tool.Limitations, StringComparison.Ordinal);
        Assert.Contains("does not generate React or Java", tool.Limitations, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("does not migrate databases", tool.Limitations, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Ndapi_entry_records_native_process_and_target_generation_boundaries()
    {
        MigrationToolDefinition tool = Assert.Single(
            FleetTools.DescribeMigrationLandscape().Tools,
            candidate => candidate.Name.Contains("ndapi", StringComparison.OrdinalIgnoreCase));

        Assert.Equal(ToolAuthority.OpenSourceImplementation, tool.Authority);
        Assert.Contains("6.0.8.22.1", tool.Limitations, StringComparison.Ordinal);
        Assert.Contains("Windows x86", tool.Limitations, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Oracle native libraries", tool.Limitations, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("read-only allowlisted worker", tool.Limitations, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("does not generate ASP.NET", tool.Limitations, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("does not cover 9i-11g directly", tool.Limitations, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// "Selected 12.2.1.x releases" read as a whole patch line, so a reader could assume any 12c build was
    /// covered. Only three stable builds are documented, and nothing upstream proves even those run.
    /// </summary>
    [Theory]
    [InlineData("12.2.1.3")]
    [InlineData("12.2.1.4")]
    [InlineData("12.2.1.19")]
    public void Ndapi_entry_names_the_exact_stable_12c_builds_rather_than_a_patch_line(string build)
    {
        MigrationToolDefinition tool = Assert.Single(
            FleetTools.DescribeMigrationLandscape().Tools,
            candidate => candidate.Name.Contains("ndapi", StringComparison.OrdinalIgnoreCase));

        Assert.Contains(build, tool.Limitations, StringComparison.Ordinal);
        Assert.DoesNotContain("selected 12.2.1.x", tool.Limitations, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Ndapi_entry_records_that_nothing_upstream_proves_native_forms_execution()
    {
        MigrationToolDefinition tool = Assert.Single(
            FleetTools.DescribeMigrationLandscape().Tools,
            candidate => candidate.Name.Contains("ndapi", StringComparison.OrdinalIgnoreCase));

        Assert.Contains("no test project", tool.Limitations, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("CI", tool.Limitations, StringComparison.Ordinal);
        Assert.Contains("proves native Forms execution", tool.Limitations, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("14.1.2.0", tool.Limitations, StringComparison.Ordinal);
        Assert.Contains("pre-release source only", tool.Limitations, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("mutating, save, compile, conversion, and database-connect", tool.Limitations, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// ASP.NET Core and Blazor are on the upstream roadmap. Neither ndapi nor this fleet emits either one,
    /// so the entry has to say so rather than leaving a reader to infer a .NET target path exists today.
    /// </summary>
    [Fact]
    public void Ndapi_entry_marks_aspnet_core_and_blazor_output_as_roadmap_only()
    {
        MigrationToolDefinition tool = Assert.Single(
            FleetTools.DescribeMigrationLandscape().Tools,
            candidate => candidate.Name.Contains("ndapi", StringComparison.OrdinalIgnoreCase));

        Assert.Contains("ASP.NET Core and Blazor output is roadmap intent only", tool.Limitations, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("this fleet generates none from it", tool.Limitations, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("not end-to-end conversion proof", tool.Limitations, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Workshop_hrms_fixture_is_not_treated_as_licensed_or_runnable()
    {
        MigrationToolDefinition tool = Assert.Single(
            FleetTools.DescribeMigrationLandscape().Tools,
            t => t.Name.Contains("Cognition workshop", StringComparison.OrdinalIgnoreCase));

        Assert.Contains("no .fmb", tool.Limitations, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("no standalone LICENSE text", tool.Limitations, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("not sufficient redistribution proof", tool.Limitations, StringComparison.OrdinalIgnoreCase);
    }

        [Fact]
        public void Tool_serializer_rejects_numeric_enum_values()
        {
                const string json = """
                        {
                            "engagementId": "ENG-1",
                            "applicationName": "ORDERS",
                            "evidence": [{
                                "id": "EV-1",
                                "kind": 0,
                                "source": "inventory.csv",
                                "summary": "Inventory",
                                "signals": []
                            }],
                            "businessConstraints": [],
                            "approval": { "decision": "Pending" }
                        }
                        """;

                Assert.Throws<JsonException>(() =>
                        JsonSerializer.Deserialize<MigrationAssessmentRequest>(json, FleetTools.SerializerOptions));
        }

        [Fact]
        public void Tool_serializer_preserves_explicit_null_signals_for_intake_validation()
        {
                const string json = """
                        {
                            "engagementId": "ENG-1",
                            "applicationName": "ORDERS",
                            "evidence": [{
                                "id": "EV-1",
                                "kind": "FormsModuleInventory",
                                "source": "inventory.csv",
                                "summary": "Inventory",
                                "signals": null
                            }],
                            "businessConstraints": [],
                            "approval": { "decision": "Pending" }
                        }
                        """;

                MigrationAssessmentRequest request = JsonSerializer.Deserialize<MigrationAssessmentRequest>(
                        json, FleetTools.SerializerOptions)!;
                MigrationPlan plan = FleetTools.AssessOracleFormsMigration(request);

                Assert.Equal(MigrationStage.Intake, plan.FinalStage);
                Assert.Contains(plan.Stages[0].Findings,
                        finding => finding.Detail.Contains("Signals must be an array", StringComparison.Ordinal));
        }

            [Fact]
            public void Tool_serializer_rejects_unknown_enum_strings()
            {
                const string json = """
                    {
                      "engagementId": "ENG-1",
                      "applicationName": "ORDERS",
                      "evidence": [{
                        "id": "EV-1",
                        "kind": "NotAnEvidenceKind",
                        "source": "inventory.csv",
                        "summary": "Inventory",
                        "signals": []
                      }],
                      "businessConstraints": [],
                      "approval": { "decision": "Pending" }
                    }
                    """;

                Assert.Throws<JsonException>(() =>
                    JsonSerializer.Deserialize<MigrationAssessmentRequest>(json, FleetTools.SerializerOptions));
            }

            [Fact]
            public void Tool_serializer_allows_null_evidence_item_to_reach_intake_validation()
            {
                const string json = """
                    {
                      "engagementId": "ENG-1",
                      "applicationName": "ORDERS",
                      "evidence": [null],
                      "businessConstraints": [],
                      "approval": { "decision": "Pending" }
                    }
                    """;

                MigrationAssessmentRequest request = JsonSerializer.Deserialize<MigrationAssessmentRequest>(
                    json, FleetTools.SerializerOptions)!;
                MigrationPlan plan = FleetTools.AssessOracleFormsMigration(request);

                Assert.Equal(MigrationStage.Intake, plan.FinalStage);
                Assert.Contains(plan.Stages[0].Findings,
                    finding => finding.Detail.Contains("Evidence items cannot be null", StringComparison.Ordinal));
            }
}