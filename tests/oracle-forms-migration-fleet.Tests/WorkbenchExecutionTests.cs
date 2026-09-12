// Copyright (c) Microsoft. All rights reserved.

using System.IO.Compression;
using System.Text;
using OracleFormsMigrationFleet.Fleet;
using OracleFormsMigrationFleet.Hosting;

namespace OracleFormsMigrationFleet.Tests;

/// <summary>
/// Authorization and path safety for the execution endpoints. A caller may only reach a workspace
/// they own, only by identifier, and only inside the generated-output area.
/// </summary>
public class WorkbenchExecutionTests : IDisposable
{
    private const string Owner = "user-a";

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"ofm-exec-{Guid.NewGuid():N}");

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

    private static MigrationRunRequest Request(string sourceRoot = "forms", string outputRoot = "out/orders") => new()
    {
        EngagementId = "ENG-EXEC",
        ApplicationName = "ORDERS",
        RequestedMode = ExecutionMode.GenerateArtifacts,
        Target = new TargetStack { Database = DatabaseTarget.PostgreSql },
        SourceRoot = sourceRoot,
        OutputRoot = outputRoot,
    };

    private async Task<(SourceWorkspaceService Service, string WorkspaceId)> SeedAsync()
    {
        SourceWorkspaceService service = new(_root);

        using MemoryStream archive = new();
        using (ZipArchive zip = new(archive, ZipArchiveMode.Create, leaveOpen: true))
        {
            using Stream entry = zip.CreateEntry("forms/ORDERS.fmb").Open();
            entry.Write(Encoding.UTF8.GetBytes("binary-form"));
        }

        archive.Position = 0;

        string? workspaceId = null;
        await foreach (SourceProgress step in service.ExtractAsync(Owner, archive, "orders.zip", CancellationToken.None))
        {
            if (step.Level == "done")
            {
                workspaceId = step.Text;
            }
        }

        Assert.NotNull(workspaceId);
        return (service, workspaceId);
    }

    [Fact]
    public async Task Execution_rejects_a_workspace_owned_by_someone_else()
    {
        (SourceWorkspaceService service, string workspaceId) = await SeedAsync();
        using SourceWorkspaceService owned = service;

        Assert.False(WorkbenchExecution.TryPrepare(
            service, "user-b", workspaceId, Request(), out string root, out MigrationRunRequest? prepared, out int status, out string error));

        Assert.Equal(404, status);
        Assert.Equal(string.Empty, root);
        Assert.Null(prepared);
        Assert.NotEqual(string.Empty, error);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("00000000000000000000000000000000")]
    public async Task Execution_rejects_an_unknown_workspace(string? workspaceId)
    {
        (SourceWorkspaceService service, string _) = await SeedAsync();
        using SourceWorkspaceService owned = service;

        Assert.False(WorkbenchExecution.TryPrepare(
            service, Owner, workspaceId, Request(), out _, out MigrationRunRequest? prepared, out int status, out _));

        Assert.Equal(404, status);
        Assert.Null(prepared);
    }

    [Theory]
    [InlineData("../../etc")]
    [InlineData("/etc/passwd")]
    [InlineData(@"C:\Windows")]
    [InlineData("https://contoso.example/forms")]
    public async Task Execution_rejects_a_source_root_that_leaves_the_workspace(string sourceRoot)
    {
        (SourceWorkspaceService service, string workspaceId) = await SeedAsync();
        using SourceWorkspaceService owned = service;

        Assert.False(WorkbenchExecution.TryPrepare(
            service, Owner, workspaceId, Request(sourceRoot), out _, out MigrationRunRequest? prepared, out int status, out _));

        Assert.Equal(400, status);
        Assert.Null(prepared);
    }

    [Fact]
    public async Task Execution_writes_under_the_generated_output_directory_not_the_source_copy()
    {
        (SourceWorkspaceService service, string workspaceId) = await SeedAsync();
        using SourceWorkspaceService owned = service;

        Assert.True(WorkbenchExecution.TryPrepare(
            service, Owner, workspaceId, Request(), out string root, out MigrationRunRequest? prepared, out int status, out _));

        Assert.Equal(200, status);
        Assert.Equal($"{WorkbenchExecution.OutputRoot}/out/orders", prepared!.OutputRoot);
        Assert.Equal("forms", prepared.SourceRoot);
        Assert.True(Path.IsPathRooted(root));
    }

    [Fact]
    public async Task Artifact_preview_rejects_a_workspace_owned_by_someone_else()
    {
        (SourceWorkspaceService service, string workspaceId) = await SeedAsync();
        using SourceWorkspaceService owned = service;

        WriteArtifact(service, workspaceId, "report.md", "# Report");

        Assert.False(WorkbenchExecution.TryResolveArtifact(
            service, "user-b", workspaceId, $"{WorkbenchExecution.OutputRoot}/report.md", out string path, out int status, out _));

        Assert.Equal(404, status);
        Assert.Equal(string.Empty, path);
    }

    [Theory]
    [InlineData(".fleet-run/../../escape.md")]
    [InlineData("../escape.md")]
    [InlineData("/etc/passwd")]
    [InlineData(@"C:\Windows\win.ini")]
    [InlineData("forms/ORDERS.fmb")]
    [InlineData("")]
    public async Task Artifact_preview_rejects_paths_outside_the_generated_output(string path)
    {
        (SourceWorkspaceService service, string workspaceId) = await SeedAsync();
        using SourceWorkspaceService owned = service;

        Assert.False(WorkbenchExecution.TryResolveArtifact(
            service, Owner, workspaceId, path, out string absolute, out int status, out _));

        Assert.Equal(400, status);
        Assert.Equal(string.Empty, absolute);
    }

    [Fact]
    public async Task Artifact_preview_rejects_a_non_text_extension()
    {
        (SourceWorkspaceService service, string workspaceId) = await SeedAsync();
        using SourceWorkspaceService owned = service;

        WriteArtifact(service, workspaceId, "module.fmb", "binary");

        Assert.False(WorkbenchExecution.TryResolveArtifact(
            service, Owner, workspaceId, $"{WorkbenchExecution.OutputRoot}/module.fmb", out _, out int status, out _));

        Assert.Equal(415, status);
    }

    [Fact]
    public async Task Artifact_preview_serves_a_generated_text_artifact_and_reports_truncation()
    {
        (SourceWorkspaceService service, string workspaceId) = await SeedAsync();
        using SourceWorkspaceService owned = service;

        WriteArtifact(service, workspaceId, "analysis/REPORT.md", "# Inventory\nOne line.\n");

        Assert.True(WorkbenchExecution.TryResolveArtifact(
            service, Owner, workspaceId, $"{WorkbenchExecution.OutputRoot}/analysis/REPORT.md",
            out string absolute, out int status, out _));

        Assert.Equal(200, status);
        Assert.Equal("# Inventory\nOne line.\n", WorkbenchExecution.ReadPreview(absolute, out bool truncated));
        Assert.False(truncated);

        WriteArtifact(service, workspaceId, "analysis/BIG.md", new string('x', (int)WorkbenchExecution.MaxPreviewBytes + 64));
        Assert.True(WorkbenchExecution.TryResolveArtifact(
            service, Owner, workspaceId, $"{WorkbenchExecution.OutputRoot}/analysis/BIG.md", out string big, out _, out _));

        Assert.Equal((int)WorkbenchExecution.MaxPreviewBytes, WorkbenchExecution.ReadPreview(big, out bool capped).Length);
        Assert.True(capped);
    }

    [Fact]
    public async Task Resetting_the_output_leaves_the_source_copy_alone()
    {
        (SourceWorkspaceService service, string workspaceId) = await SeedAsync();
        using SourceWorkspaceService owned = service;

        string root = service.ResolveRoot(Owner, workspaceId)!;
        WriteArtifact(service, workspaceId, "stale.md", "previous run");

        WorkbenchExecution.ResetOutput(root);

        Assert.False(Directory.Exists(Path.Combine(root, WorkbenchExecution.OutputRoot)));
        Assert.True(File.Exists(Path.Combine(root, "forms", "ORDERS.fmb")));
    }

    private static void WriteArtifact(SourceWorkspaceService service, string workspaceId, string relativePath, string content)
    {
        string absolute = Path.Combine(
            service.ResolveRoot(Owner, workspaceId)!,
            WorkbenchExecution.OutputRoot,
            relativePath.Replace('/', Path.DirectorySeparatorChar));

        Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);
        File.WriteAllText(absolute, content);
    }
}
