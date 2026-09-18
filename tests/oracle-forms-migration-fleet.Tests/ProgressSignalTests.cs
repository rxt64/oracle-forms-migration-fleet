// Copyright (c) Microsoft. All rights reserved.

using System.IO.Compression;
using System.Text;
using OracleFormsMigrationFleet.Fleet;
using OracleFormsMigrationFleet.Fleet.Execution;
using OracleFormsMigrationFleet.Hosting;

namespace OracleFormsMigrationFleet.Tests;

public class ProgressSignalTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"ofm-progress-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            foreach (string file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(_root, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task Source_acquisition_reports_typed_measured_facts_and_a_terminal_outcome()
    {
        using SourceWorkspaceService service = new(_root);
        using MemoryStream archive = new();
        using (ZipArchive zip = new(archive, ZipArchiveMode.Create, leaveOpen: true))
        {
            using Stream form = zip.CreateEntry("forms/ORDERS.fmb").Open();
            form.Write(Encoding.UTF8.GetBytes("binary-form"));
        }

        archive.Position = 0;
        List<SourceProgress> progress = [];
        await foreach (SourceProgress item in service.ExtractAsync("owner", archive, "orders.zip", CancellationToken.None))
        {
            progress.Add(item);
        }

        SourceProgress counted = Assert.Single(progress, item =>
            item.Signal?.Action == ProgressActions.ArtifactCounted &&
            item.Signal.ArtifactKind == "FormsModuleSource");
        Assert.Equal(ProgressOperations.SourceAcquisition, counted.Signal!.Operation);
        Assert.Equal(ProgressState.Running, counted.Signal.State);
        Assert.Equal("FormsModuleSource", counted.Signal.ArtifactKind);
        Assert.Equal(1, counted.Signal.ArtifactCount);

        SourceProgress terminal = Assert.Single(progress, item => item.Signal?.Action == ProgressActions.SourceReady);
        Assert.Equal(ProgressState.Completed, terminal.Signal!.State);
        Assert.NotEqual(string.Empty, terminal.Signal.Purpose);
        Assert.NotEqual(string.Empty, terminal.Signal.Observed);
        Assert.NotEqual(string.Empty, terminal.Signal.NextAction);
    }

    [Fact]
    public async Task Executor_reports_structural_phase_events_without_parsing_adapter_text()
    {
        Directory.CreateDirectory(_root);
        MigrationRunRequest request = new()
        {
            EngagementId = "ENG-PROGRESS",
            ApplicationName = "ORDERS",
            RequestedMode = ExecutionMode.PlanOnly,
            Target = new TargetStack { Database = DatabaseTarget.PostgreSql },
            SourceRoot = "forms",
            OutputRoot = "out",
        };

        MigrationExecutionResult result = await new MigrationExecutor(_root, [])
            .ExecuteAsync(request, "operator");

        ExecutionProgress authorized = Assert.Single(
            result.Progress,
            item => item.Signal?.Action == ProgressActions.PlanAuthorized);
        Assert.Equal(ProgressOperations.MigrationRun, authorized.Signal!.Operation);
        Assert.Equal(ProgressState.Running, authorized.Signal.State);

        Assert.Contains(result.Progress, item =>
            item.Signal?.Action == ProgressActions.PhaseSkipped &&
            item.Signal.State == ProgressState.Running);
    }

    [Fact]
    public void Wire_frames_are_ordered_timestamped_and_keep_legacy_fields()
    {
        WorkbenchProgressSequence sequence = new();
        DateTimeOffset now = new(2026, 9, 18, 12, 0, 0, TimeSpan.Zero);
        ProgressSignal signal = new(
            ProgressOperations.SourceAcquisition,
            ProgressActions.ArtifactCounted,
            ProgressState.Running,
            "Read source.",
            "Found Forms modules.",
            "Continue indexing.",
            "FormsModuleSource",
            25);

        Dictionary<string, object?> first = WorkbenchProgressFrame.Create(
            sequence, "found", "25 x FormsModuleSource", signal, clock: () => now);
        Dictionary<string, object?> second = WorkbenchProgressFrame.Create(
            sequence, "keepalive", "Still running.", signal with { State = ProgressState.Waiting }, clock: () => now.AddSeconds(1));

        Assert.Equal("found", first["level"]);
        Assert.Equal("25 x FormsModuleSource", first["text"]);
        Assert.Equal(1, first["sequence"]);
        Assert.Equal(2, second["sequence"]);
        Assert.Equal(now.ToString("O"), first["timestampUtc"]);
        Assert.Equal("Running", first["state"]);
        Assert.Equal("Waiting", second["state"]);
        Assert.Equal("FormsModuleSource", first["artifactKind"]);
        Assert.Equal(25, first["artifactCount"]);
    }

    [Fact]
    public void Run_outcome_requires_at_least_one_executed_phase_and_no_authorized_gap()
    {
        Assert.True(WorkbenchRunOutcome.Failed([]));
        Assert.True(WorkbenchRunOutcome.Failed([
            new PhaseOutcome(MigrationPhase.SourceAnalysis, PhaseStatus.Planned, PhaseExecutionState.AdapterNotImplemented, [], [], "missing"),
        ]));
        Assert.True(WorkbenchRunOutcome.Failed([
            new PhaseOutcome(MigrationPhase.SourceAnalysis, PhaseStatus.Planned, PhaseExecutionState.Failed, [], [], "failed"),
        ]));

        Assert.False(WorkbenchRunOutcome.Failed([
            new PhaseOutcome(MigrationPhase.SourceAnalysis, PhaseStatus.Planned, PhaseExecutionState.Executed, [], [], null),
            new PhaseOutcome(MigrationPhase.ProductionCutover, PhaseStatus.BlockedOnApproval, PhaseExecutionState.SkippedByPlanner, [], [], "blocked"),
        ]));
    }
}
