using OracleFormsMigrationFleet.Fleet;
using OracleFormsMigrationFleet.Fleet.Execution;
using OracleFormsMigrationFleet.Fleet.Execution.Adapters;

namespace OracleFormsMigrationFleet.Tests;

/// <summary>
/// The conversion phase's stack selection. The Java path is the default and is unchanged; the ASP.NET Core
/// path exists only when an operator supplied a mapping manifest the parsed schema confirmed.
/// </summary>
public sealed class DotNetApplicationConversionAdapterTests
{
    private const string SourceRoot = "legacy";

    private const string OutputRoot = "out/pilot";

    private static MigrationRunRequest Request(BackEndStack stack) => new()
    {
        EngagementId = "ENG-DOTNET-PILOT",
        ApplicationName = "Meridian Order Entry",
        RequestedMode = ExecutionMode.GenerateArtifacts,
        Target = new TargetStack { BackEnd = stack, Database = DatabaseTarget.PostgreSql },
        SourceRoot = SourceRoot,
        OutputRoot = OutputRoot,
    };

    private static TemporaryWorkspace Seeded(string? manifest)
    {
        TemporaryWorkspace workspace = new();
        workspace.WriteFile($"{SourceRoot}/db/schema.sql", DotNetPilotFixtures.MeridianSchema);

        if (manifest is not null)
        {
            workspace.WriteFile($"{SourceRoot}/{TargetMappingReader.ConventionalPath}", manifest);
        }

        return workspace;
    }

    private static Task<PhaseExecutionResult> RunAsync(TemporaryWorkspace workspace, BackEndStack stack)
    {
        MigrationRunRequest request = Request(stack);
        PhasePlan plan = MigrationRunPlanner.Plan(request).Phases
            .Single(phase => phase.Phase == MigrationPhase.ApplicationCodeConversion);

        return new ApplicationCodeConversionAdapter().ExecuteAsync(
            new PhaseExecutionContext(workspace.Root, SourceRoot, OutputRoot, plan, request, (_, _) => { }),
            CancellationToken.None);
    }

    [Fact]
    public async Task The_dotnet_tier_is_written_when_the_manifest_validates()
    {
        using TemporaryWorkspace workspace = Seeded(DotNetPilotFixtures.MeridianManifest);

        PhaseExecutionResult result = await RunAsync(workspace, BackEndStack.AspNetCore);

        Assert.True(result.Succeeded, result.FailureReason);
        Assert.True(workspace.Exists($"{OutputRoot}/application/backend/GeneratedBackend.slnx"));
        Assert.True(workspace.Exists($"{OutputRoot}/application/backend/Api/ApiHost.cs"));
        Assert.True(workspace.Exists($"{OutputRoot}/application/backend/Api.Tests/AcceptanceTests.cs"));
        Assert.True(workspace.Exists($"{OutputRoot}/application/frontend/src/App.tsx"));
        Assert.True(workspace.Exists($"{OutputRoot}/application/database/routines.sql"));
        Assert.True(workspace.Exists($"{OutputRoot}/application/mapping-manifest.json"));

        // No Java descriptor: this is a stack selection, not an addition beside the existing one.
        Assert.False(workspace.Exists($"{OutputRoot}/application/backend/pom.xml"));

        // Generation produces artifacts and deliberately no attestation.
        Assert.Contains(result.Artifacts, artifact => artifact.Kind == ArtifactKind.DatabaseSchema);
        Assert.Contains(result.Artifacts, artifact => artifact.Kind == ArtifactKind.TestSuite);
    }

    [Fact]
    public async Task The_java_tier_is_still_what_a_request_that_named_no_stack_gets()
    {
        using TemporaryWorkspace workspace = Seeded(manifest: null);

        PhaseExecutionResult result = await RunAsync(workspace, BackEndStack.JavaSpringBoot);

        Assert.True(result.Succeeded, result.FailureReason);
        Assert.True(workspace.Exists($"{OutputRoot}/application/backend/pom.xml"));
        Assert.False(workspace.Exists($"{OutputRoot}/application/backend/GeneratedBackend.slnx"));
    }

    [Fact]
    public async Task A_dotnet_run_without_a_manifest_refuses_rather_than_inferring_the_roles()
    {
        using TemporaryWorkspace workspace = Seeded(manifest: null);

        PhaseExecutionResult result = await RunAsync(workspace, BackEndStack.AspNetCore);

        Assert.False(result.Succeeded);
        Assert.Contains(TargetMappingReader.ConventionalPath, result.FailureReason!, StringComparison.Ordinal);
        Assert.False(workspace.Exists($"{OutputRoot}/application/backend/GeneratedBackend.slnx"));
    }

    [Fact]
    public async Task A_refused_manifest_stops_the_phase_and_reports_every_reason()
    {
        using TemporaryWorkspace workspace = Seeded(DotNetPilotFixtures.KestrelManifest);

        // The Kestrel manifest is valid, but it describes a different estate than the seeded schema.
        PhaseExecutionResult result = await RunAsync(workspace, BackEndStack.AspNetCore);

        Assert.False(result.Succeeded);
        Assert.Contains("refused", result.FailureReason!, StringComparison.Ordinal);
        Assert.Contains(result.Findings, finding =>
            finding.Contains("DISPATCH_CONSIGNMENT_HEADER", StringComparison.Ordinal));
        Assert.False(workspace.Exists($"{OutputRoot}/application/backend/GeneratedBackend.slnx"));
        Assert.False(workspace.Exists($"{OutputRoot}/application/database/schema.sql"));
    }

    [Fact]
    public async Task The_conversion_notes_state_the_roles_and_what_stays_unverified()
    {
        using TemporaryWorkspace workspace = Seeded(DotNetPilotFixtures.MeridianManifest);

        await RunAsync(workspace, BackEndStack.AspNetCore);
        string notes = File.ReadAllText(workspace.Absolute($"{OutputRoot}/application/CONVERSION_NOTES.md"));

        Assert.Contains(DotNetPilotFixtures.MeridianLabel, notes, StringComparison.Ordinal);
        Assert.Contains("MasterHeader", notes, StringComparison.Ordinal);
        Assert.Contains("`MRD_ORDER_HEAD`", notes, StringComparison.Ordinal);
        Assert.Contains("concurrency-control", notes, StringComparison.Ordinal);
        Assert.Contains("nothing here was compared against a running source system", notes, StringComparison.Ordinal);
    }
}
