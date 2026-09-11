// Copyright (c) Microsoft. All rights reserved.

using OracleFormsMigrationFleet.Fleet;
using System.Text.Json;

namespace OracleFormsMigrationFleet.Tests;

/// <summary>Validates the checked-in Foundry evaluation seed without model or network access.</summary>
public class EvalDatasetTests
{
    private const string DatasetFile = "EvalDatasets/oracle-forms-migration-fleet-eval-seed-v1.jsonl";

    private static readonly string[] RequiredContexts =
    [
        "evidence_gating",
        "target_platform",
        "landscape_authority",
        "forms_ui_semantics",
        "critical_risk",
        "secret_rejection",
        "non_execution",
    ];

    private static readonly string[] CompleteAssessmentEvidence =
    [
        nameof(EvidenceKind.FormsModuleInventory),
        nameof(EvidenceKind.FormsModuleSource),
        nameof(EvidenceKind.FormsXmlExport),
        nameof(EvidenceKind.PlSqlProgramUnit),
        nameof(EvidenceKind.DatabaseSchemaExport),
        nameof(EvidenceKind.WorkloadProfile),
        nameof(EvidenceKind.BusinessProcessCatalog),
        nameof(EvidenceKind.IntegrationInventory),
        nameof(EvidenceKind.AuthenticationTopology),
        nameof(EvidenceKind.DataProfile),
        nameof(EvidenceKind.TestBaseline),
        nameof(EvidenceKind.CutoverAndRollbackPlan),
        nameof(EvidenceKind.LicensingAndSupportPosition),
        nameof(EvidenceKind.UsageAndBusinessValue),
    ];

    private sealed record Row(
        string Query,
        string ExpectedBehavior,
        string Context,
        IReadOnlyList<string> Tools,
        IReadOnlyList<string> Signals,
        AssessmentSpec? Assessment);

    private sealed record AssessmentSpec(
        ApprovalDecision Approval,
        IReadOnlyDictionary<string, IReadOnlyList<WorkloadSignal>> SignalEvidence,
        ExpectedPlanSpec Expected);

    private sealed record ExpectedPlanSpec(
        MigrationStage FinalStage,
        StageStatus FinalStatus,
        bool IsAccepted,
        TargetPlatform Target,
        ConfidenceLevel Confidence,
        IReadOnlyList<string> TaskIds);

    private static IReadOnlyList<Row> Rows { get; } = Load();

    [Fact]
    public void Dataset_has_unique_rows_with_actionable_expectations()
    {
        Assert.True(Rows.Count >= 15, $"Expected at least 15 rows, found {Rows.Count}.");
        Assert.Equal(Rows.Count, Rows.Select(row => row.Query).Distinct(StringComparer.Ordinal).Count());
        Assert.All(Rows, row =>
        {
            Assert.True(row.ExpectedBehavior.Length >= 40,
                $"Expected behavior is too vague for query '{row.Query}'.");
            Assert.DoesNotContain("responds correctly", row.ExpectedBehavior,
                StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public void Dataset_covers_every_required_context()
    {
        HashSet<string> present = new(Rows.Select(row => row.Context), StringComparer.Ordinal);
        Assert.All(RequiredContexts, context => Assert.Contains(context, present));
    }

    [Fact]
    public void Tool_tags_match_the_registered_fleet_tools()
    {
        HashSet<string> registered = new(
            FleetTools.Create().Select(tool => tool.Name),
            StringComparer.Ordinal);
        HashSet<string> tagged = new(Rows.SelectMany(row => row.Tools), StringComparer.Ordinal);

        Assert.Empty(tagged.Except(registered));
        Assert.Empty(registered.Except(tagged));
    }

    [Fact]
    public void Signal_tags_are_defined_workload_signals()
    {
        Assert.All(Rows.SelectMany(row => row.Signals), name =>
            Assert.True(
                Enum.TryParse(name, ignoreCase: false, out WorkloadSignal signal) && Enum.IsDefined(signal),
                $"'{name}' is not a defined {nameof(WorkloadSignal)}."));
    }

    [Fact]
    public void Dataset_covers_the_forms_grid_and_query_signals()
    {
        HashSet<string> tagged = new(
            Rows.Where(row => row.Context == "forms_ui_semantics").SelectMany(row => row.Signals),
            StringComparer.Ordinal);

        Assert.Contains(nameof(WorkloadSignal.MultiRecordBlocks), tagged);
        Assert.Contains(nameof(WorkloadSignal.EnterQueryMode), tagged);
        Assert.Contains(nameof(WorkloadSignal.PostQueryLogic), tagged);
    }

    [Fact]
    public void Dataset_covers_every_critical_legacy_risk()
    {
        HashSet<string> tagged = new(Rows.SelectMany(row => row.Signals), StringComparer.Ordinal);
        IEnumerable<string> criticalSignals = LegacyModernizationCatalog.Risks
            .Where(entry => entry.Value.Severity == Severity.Critical)
            .Select(entry => entry.Key.ToString());

        Assert.All(criticalSignals, signal => Assert.Contains(signal, tagged));
    }

    [Fact]
    public void Dataset_covers_hard_soft_and_undetermined_target_cases()
    {
        List<Row> platformRows = [.. Rows.Where(row => row.Context == "target_platform")];

        Assert.Contains(platformRows, row => row.Signals.Any(name =>
            Enum.TryParse(name, out WorkloadSignal signal) &&
            TargetPlatformAdvisor.HardManagedInstanceConstraints.ContainsKey(signal)));
        Assert.Contains(platformRows, row => row.Signals.Any(name =>
            Enum.TryParse(name, out WorkloadSignal signal) &&
            TargetPlatformAdvisor.SoftIndicators.TryGetValue(signal, out var indicator) &&
            indicator.Platform == TargetPlatform.AzureSqlDatabase));
        Assert.Contains(platformRows, row => row.ExpectedBehavior.Contains(
            nameof(TargetPlatform.Undetermined), StringComparison.Ordinal));
    }

    [Fact]
    public void Assessment_outcome_rows_supply_complete_evidence_and_named_signals()
    {
        List<Row> outcomeRows =
        [
            .. Rows.Where(row =>
                row.Context is "target_platform" or "forms_ui_semantics" or "critical_risk" ||
                row.ExpectedBehavior.Contains(nameof(MigrationStage.HumanApproval), StringComparison.Ordinal)),
        ];

        Assert.NotEmpty(outcomeRows);
        Assert.All(outcomeRows, row =>
        {
            Assert.NotNull(row.Assessment);
            Assert.All(CompleteAssessmentEvidence,
                evidenceKind => Assert.Contains(evidenceKind, row.Query, StringComparison.Ordinal));
            HashSet<string> assessmentSignals = new(
                row.Assessment.SignalEvidence.Values.SelectMany(signals => signals)
                    .Select(signal => signal.ToString()),
                StringComparer.Ordinal);
            Assert.All(row.Signals, signal => Assert.Contains(signal, assessmentSignals));
        });
    }

    [Fact]
    public void Assessment_metadata_matches_authoritative_evidence_and_fleet_outcomes()
    {
        foreach (Row row in Rows.Where(row => row.Assessment is not null))
        {
            AssessmentSpec assessment = row.Assessment!;
            List<EvidenceItem> evidence =
            [
                .. Requests.CompleteEvidence(),
                Requests.Evidence("EV-XML", EvidenceKind.FormsXmlExport),
                Requests.Evidence("EV-SHARED", EvidenceKind.SharedLibrarySource),
            ];

            foreach ((string evidenceId, IReadOnlyList<WorkloadSignal> signals) in assessment.SignalEvidence)
            {
                int evidenceIndex = evidence.FindIndex(item => item.Id == evidenceId);
                Assert.True(evidenceIndex >= 0, $"Unknown evidence id '{evidenceId}' in '{row.Query}'.");
                EvidenceItem item = evidence[evidenceIndex];
                Assert.Contains($"{item.Kind} {item.Id}", row.Query, StringComparison.Ordinal);
                string evidenceSegment = row.Query.Split(';', StringSplitOptions.TrimEntries)
                    .Single(segment => segment.Contains($"{item.Kind} {item.Id}", StringComparison.Ordinal));

                foreach (WorkloadSignal signal in signals)
                {
                    Assert.Contains(signal.ToString(), evidenceSegment, StringComparison.Ordinal);
                    if (LegacyModernizationCatalog.Risks.TryGetValue(signal, out LegacyRiskDefinition? risk))
                    {
                        Assert.Contains(item.Kind, risk.PreferredEvidence);
                    }

                    Assert.True(TargetPlatformAdvisor.IsAuthoritativeEvidence(signal, item.Kind));
                }

                evidence[evidenceIndex] = item with { Signals = signals };
            }

            HumanApproval approval = assessment.Approval == ApprovalDecision.Approved
                ? Requests.Approved()
                : new HumanApproval { Decision = assessment.Approval };
            MigrationPlan plan = MigrationFleetOrchestrator.Run(Requests.Build(evidence, approval));
            ExpectedPlanSpec expected = assessment.Expected;

            Assert.Equal(expected.FinalStage, plan.FinalStage);
            Assert.Equal(expected.FinalStatus, plan.FinalStatus);
            Assert.Equal(expected.IsAccepted, plan.IsAccepted);
            Assert.Equal(expected.Target, plan.Recommendation.Recommended);
            Assert.Equal(expected.Confidence, plan.Recommendation.Confidence);
            Assert.All(expected.TaskIds,
                taskId => Assert.Contains(plan.ConversionTasks, task => task.Id == taskId));
        }
    }

    [Fact]
    public void Landscape_rows_preserve_authority_labels()
    {
        List<Row> landscapeRows = [.. Rows.Where(row => row.Context == "landscape_authority")];

        Assert.Contains(landscapeRows, row => row.ExpectedBehavior.Contains(
            nameof(ToolAuthority.VendorClaim), StringComparison.Ordinal));
        Assert.Contains(landscapeRows, row => row.ExpectedBehavior.Contains(
            nameof(ToolAuthority.Microsoft), StringComparison.Ordinal));
        Assert.Contains(landscapeRows, row => row.ExpectedBehavior.Contains(
            nameof(ToolAuthority.Oracle), StringComparison.Ordinal));
        Assert.All(landscapeRows,
            row => Assert.Contains("describe_migration_landscape", row.Tools));
    }

    [Fact]
    public void Secret_rejection_row_trips_the_guardrail()
    {
        List<Row> secretRows = [.. Rows.Where(row => row.Context == "secret_rejection")];

        Assert.NotEmpty(secretRows);
        Assert.All(secretRows, row =>
        {
            Assert.True(FleetGuardrails.ContainsPotentialSecret(row.Query),
                $"Secret rejection query does not match the guardrail: '{row.Query}'.");
            Assert.Contains("reject", row.ExpectedBehavior, StringComparison.OrdinalIgnoreCase);
            Assert.Empty(row.Tools);
            Assert.Contains("before model inference", row.ExpectedBehavior, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public void Non_execution_rows_pin_the_stated_boundaries()
    {
        List<Row> rows = [.. Rows.Where(row => row.Context == "non_execution")];

        Assert.True(rows.Count >= 2);
        Assert.Contains(rows, row => row.ExpectedBehavior.Contains(
            "assessments and plans only", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(rows, row => row.ExpectedBehavior.Contains("DDL", StringComparison.Ordinal));
        Assert.Contains(rows, row => row.ExpectedBehavior.Contains(
            "human approval", StringComparison.OrdinalIgnoreCase));
    }

    private static List<Row> Load()
    {
        string path = Path.Combine(AppContext.BaseDirectory, DatasetFile);
        Assert.True(File.Exists(path), $"Evaluation dataset was not copied to test output: {path}");

        List<Row> rows = [];
        int lineNumber = 0;
        foreach (string line in File.ReadLines(path))
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            using JsonDocument document = JsonDocument.Parse(line);
            JsonElement root = document.RootElement;
            Assert.Equal(JsonValueKind.Object, root.ValueKind);
            rows.Add(new Row(
                Text(root, "query", lineNumber),
                Text(root, "expected_behavior", lineNumber),
                Text(root, "context", lineNumber),
                Array(root, "tools", lineNumber),
                Array(root, "signals", lineNumber),
                Assessment(root, lineNumber)));
        }

        return rows;
    }

    private static string Text(JsonElement root, string name, int lineNumber)
    {
        Assert.True(root.TryGetProperty(name, out JsonElement value),
            $"Line {lineNumber} is missing '{name}'.");
        Assert.Equal(JsonValueKind.String, value.ValueKind);
        string text = value.GetString()!;
        Assert.False(string.IsNullOrWhiteSpace(text),
            $"Line {lineNumber} has an empty '{name}'.");
        return text;
    }

    private static IReadOnlyList<string> Array(JsonElement root, string name, int lineNumber)
    {
        Assert.True(root.TryGetProperty(name, out JsonElement value),
            $"Line {lineNumber} is missing '{name}'.");
        Assert.Equal(JsonValueKind.Array, value.ValueKind);
        return [.. value.EnumerateArray().Select(item => item.GetString()!)];
    }

    private static AssessmentSpec? Assessment(JsonElement root, int lineNumber)
    {
        if (!root.TryGetProperty("assessment", out JsonElement value))
        {
            return null;
        }

        AssessmentSpec? assessment = value.Deserialize<AssessmentSpec>(FleetTools.SerializerOptions);
        Assert.NotNull(assessment);
        Assert.NotNull(assessment.SignalEvidence);
        Assert.NotNull(assessment.Expected);
        Assert.NotNull(assessment.Expected.TaskIds);
        return assessment;
    }
}