// Copyright (c) Microsoft. All rights reserved.

using Npgsql;
using OracleFormsMigrationFleet.Fleet;
using OracleFormsMigrationFleet.Fleet.Execution;
using OracleFormsMigrationFleet.Hosting;

namespace OracleFormsMigrationFleet.Tests;

public sealed class GeneratedApplicationProcessIntegrationTests
{
    [Fact]
    [Trait("Category", "GeneratedApplicationIntegration")]
    public async Task Generated_test_sandbox_cannot_read_host_temp_or_reach_managed_identity()
    {
        if (!string.Equals(
            Environment.GetEnvironmentVariable("RUN_GENERATED_APP_INTEGRATION"),
            "true",
            StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        string sentinel = Path.Combine(Path.GetTempPath(), $"ofm-host-sentinel-{Guid.NewGuid():N}");
        await File.WriteAllTextAsync(sentinel, "not visible inside generated tests");
        try
        {
            ApplicationTestRun result = await new ProcessApplicationTestGateway()
                .VerifySandboxBoundaryAsync(sentinel, CancellationToken.None);
            Assert.True(result.ToolAvailable, result.Output);
            Assert.Equal(0, result.ExitCode);
        }
        finally
        {
            File.Delete(sentinel);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Category", "GeneratedApplicationIntegration")]
    public async Task Independent_generated_applications_execute_backend_and_frontend_tests(bool northstar)
    {
        if (!string.Equals(
            Environment.GetEnvironmentVariable("RUN_GENERATED_APP_INTEGRATION"),
            "true",
            StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        using TemporaryWorkspace workspace = new();
        OracleSchema schema = OracleSchemaParser.Parse(northstar ? OracleSamples.BankingSchema : OracleSamples.Schema);
        ApplicationConversion conversion = ApplicationCodeEmitter.Convert(
            schema,
            northstar ? "NORTHSTAR" : "ORDERS",
            DatabaseTarget.PostgreSql);
        foreach (GeneratedFile file in conversion.Files)
        {
            workspace.WriteFile($"generated/{file.Path}", file.Contents);
        }

        ProcessApplicationBuildGateway buildGateway = new();
        ApplicationBuildResult backendBuild = await buildGateway.BuildJavaAsync(
            workspace.Absolute("generated/backend"), CancellationToken.None);
        ApplicationBuildResult frontendBuild = await buildGateway.BuildReactAsync(
            workspace.Absolute("generated/frontend"), CancellationToken.None);
        Assert.True(backendBuild.Succeeded, backendBuild.Output);
        Assert.True(frontendBuild.Succeeded, frontendBuild.Output);

        ProcessApplicationTestGateway gateway = new();
        string backendReports = workspace.Absolute("reports/backend");
        string frontendReports = workspace.Absolute("reports/frontend");
        Directory.CreateDirectory(backendReports);
        Directory.CreateDirectory(frontendReports);
        DateTimeOffset backendStarted = DateTimeOffset.UtcNow;
        ApplicationTestRun backend = await gateway.RunBackendTestsAsync(
            workspace.Absolute("generated/backend"), backendReports, CancellationToken.None);
        ApplicationVerificationLegResult backendResult = JUnitReportReader.Classify(
            ApplicationVerificationLeg.BackendTests,
            backend,
            backendReports,
            backendStarted,
            DateTimeOffset.UtcNow);

        DateTimeOffset frontendStarted = DateTimeOffset.UtcNow;
        ApplicationTestRun frontend = await gateway.RunFrontendTestsAsync(
            workspace.Absolute("generated/frontend"), frontendReports, CancellationToken.None);
        ApplicationVerificationLegResult frontendResult = JUnitReportReader.Classify(
            ApplicationVerificationLeg.FrontendInteractionTests,
            frontend,
            frontendReports,
            frontendStarted,
            DateTimeOffset.UtcNow);

        Assert.True(backendResult.Succeeded, backend.Output);
        Assert.True(frontendResult.Succeeded, frontend.Output);
    }

    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task Generated_schema_executes_in_a_disposable_target_and_is_removed()
    {
        string? connectionString = Environment.GetEnvironmentVariable("PLATFORM_POSTGRES_INTEGRATION_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return;
        }

        await using TemporaryDirectory directory = new();
        string ddlPath = Path.Combine(directory.Path, "schema.sql");
        string reportDirectory = Path.Combine(directory.Path, "reports");
        await File.WriteAllTextAsync(ddlPath, "create table verified_target(id integer primary key);");
        int connections = 0;
        PostgresTargetApplicationVerificationGateway gateway = new(async cancellationToken =>
        {
            Interlocked.Increment(ref connections);
            NpgsqlConnection connection = new(connectionString);
            await connection.OpenAsync(cancellationToken);
            return connection;
        });

        ApplicationTestRun run = await gateway.VerifyAsync(ddlPath, reportDirectory, CancellationToken.None);

        Assert.Equal(0, run.ExitCode);
        Assert.Equal(2, connections);
        Assert.True(File.Exists(Path.Combine(reportDirectory, "target-database.xml")));
        await using NpgsqlConnection inspection = new(connectionString);
        await inspection.OpenAsync();
        await using NpgsqlCommand remaining = new(
            "select count(*) from information_schema.schemata where schema_name like 'ofm_verify_%'",
            inspection);
        Assert.Equal(0L, Convert.ToInt64(await remaining.ExecuteScalarAsync()));
    }

    [Fact]
    public async Task Target_connection_timeout_is_a_typed_result()
    {
        await using TemporaryDirectory directory = new();
        string ddlPath = Path.Combine(directory.Path, "schema.sql");
        await File.WriteAllTextAsync(ddlPath, "create table never_reached(id integer);");
        PostgresTargetApplicationVerificationGateway gateway = new(
            async cancellationToken =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                throw new InvalidOperationException("unreachable");
            },
            TimeSpan.FromMilliseconds(20));

        ApplicationTestRun run = await gateway.VerifyAsync(
            ddlPath,
            Path.Combine(directory.Path, "reports"),
            CancellationToken.None);

        Assert.True(run.TimedOut);
        Assert.NotEqual(0, run.ExitCode);
        Assert.True(File.Exists(Path.Combine(directory.Path, "reports", "target-database.xml")));
    }

    private sealed class TemporaryDirectory : IAsyncDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"ofm-verification-{Guid.NewGuid():N}");

        public TemporaryDirectory() => Directory.CreateDirectory(Path);

        public ValueTask DisposeAsync()
        {
            Directory.Delete(Path, recursive: true);
            return ValueTask.CompletedTask;
        }
    }
}