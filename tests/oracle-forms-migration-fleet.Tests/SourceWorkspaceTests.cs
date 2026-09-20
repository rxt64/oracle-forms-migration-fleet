using System.IO.Compression;
using System.Text;
using OracleFormsMigrationFleet.Hosting;

namespace OracleFormsMigrationFleet.Tests;

public class SourceWorkspaceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"ofm-tests-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (!Directory.Exists(_root))
        {
            return;
        }

        foreach (string file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        Directory.Delete(_root, recursive: true);
        GC.SuppressFinalize(this);
    }

    [Theory]
    [InlineData("https://github.com/contoso/orders")]
    [InlineData("https://dev.azure.com/contoso/Payments/_git/orders")]
    [InlineData("https://gitlab.com/contoso/orders")]
    [InlineData("https://bitbucket.org/contoso/orders")]
    [InlineData("https://contoso.visualstudio.com/Payments/_git/orders")]
    public void Accepts_supported_public_repository_hosts(string value)
    {
        Assert.True(SourceWorkspaceService.TryParseRepositoryUrl(value, out Uri? repository, out string error));
        Assert.Equal(string.Empty, error);
        Assert.NotNull(repository);
    }

    [Theory]
    [InlineData("http://github.com/contoso/orders")]
    [InlineData("https://evil.example.com/contoso/orders")]
    [InlineData("https://token:x@github.com/contoso/orders")]
    [InlineData("git@github.com:contoso/orders.git")]
    [InlineData("file:///c:/windows")]
    [InlineData("--upload-pack=touch /tmp/pwn")]
    [InlineData("")]
    [InlineData("   ")]
    public void Rejects_unsupported_or_hostile_repository_addresses(string value)
    {
        Assert.False(SourceWorkspaceService.TryParseRepositoryUrl(value, out Uri? repository, out string error));
        Assert.Null(repository);
        Assert.NotEqual(string.Empty, error);
    }

    [Fact]
    public async Task Rejects_archive_entries_that_escape_the_workspace()
    {
        using var service = new SourceWorkspaceService(_root);
        using MemoryStream archive = BuildArchive(("../escaped.txt", "owned"));

        List<SourceProgress> progress = await Collect(service.ExtractAsync("user-a", archive, "evil.zip", CancellationToken.None));

        Assert.Contains(progress, step => step.Level == "error");
        Assert.DoesNotContain(progress, step => step.Level == "done");
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(_root)!, "escaped.txt")));
    }

    [Fact]
    public async Task Extracts_an_archive_and_classifies_its_artifacts()
    {
        using var service = new SourceWorkspaceService(_root);
        using MemoryStream archive = BuildArchive(
            ("orders/legacy/forms/ORD_ENTRY.fmb", "form"),
            ("orders/legacy/forms/ORD_LIST.fmb", "form"),
            ("orders/legacy/forms/ORD_MENU.mmb", "menu"),
            ("orders/db/schema_ddl.sql", "create table"),
            ("orders/db/ORD_PKG.pkb", "package body"),
            ("orders/readme.md", "notes"));

        List<SourceProgress> progress = await Collect(service.ExtractAsync("user-a", archive, "orders.zip", CancellationToken.None));

        SourceProgress done = Assert.Single(progress, step => step.Level == "done");
        SourceWorkspaceSummary summary = Assert.IsType<SourceWorkspaceSummary>(service.Get("user-a", done.Text));

        Assert.Equal(6, summary.FileCount);
        Assert.Equal("orders/legacy/forms", summary.SourceRoot);
        Assert.Equal(2, KindCount(summary, "FormsModuleSource"));
        Assert.Equal(2, KindCount(summary, "FormsModuleInventory"));
        Assert.Equal(1, KindCount(summary, "MenuModuleSource"));
        Assert.Equal(1, KindCount(summary, "DatabaseSchemaExport"));
        Assert.Equal(1, KindCount(summary, "PlSqlProgramUnit"));
        Assert.DoesNotContain(summary.Artifacts, artifact => artifact.Kind.Contains("Markdown", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Supplied_workbench_run_state_is_removed_before_the_workspace_is_available()
    {
        using var service = new SourceWorkspaceService(_root);
        using MemoryStream archive = BuildArchive(
            ("forms/ORDERS.fmb", "form"),
            (".fleet-run/out/database/postgresql/schema/program-unit-repairs.sql", "untrusted repair"));

        List<SourceProgress> progress = await Collect(service.ExtractAsync("user-a", archive, "orders.zip", CancellationToken.None));

        string workspaceId = Assert.Single(progress, step => step.Level == "done").Text;
        string root = service.ResolveRoot("user-a", workspaceId)!;
        Assert.False(Directory.Exists(Path.Combine(root, WorkbenchExecution.OutputRoot)));
        Assert.True(File.Exists(Path.Combine(root, "forms", "ORDERS.fmb")));
    }

    [Fact]
    public async Task Build_descriptors_beside_forms_code_are_not_counted_as_xml_exports()
    {
        using var service = new SourceWorkspaceService(_root);
        using MemoryStream archive = BuildArchive(
            ("demoapp/HACKTIVITY.fmb", "form"),
            ("demoapp/login.sql", "create or replace function"),
            ("OracleFormsTester/build.xml", "<project name=\"tester\" />"),
            ("OracleFormsSerializer/build.xml", "<project name=\"serializer\" />"));

        List<SourceProgress> progress = await Collect(service.ExtractAsync("user-a", archive, "sample.zip", CancellationToken.None));

        SourceProgress done = Assert.Single(progress, step => step.Level == "done");
        SourceWorkspaceSummary summary = Assert.IsType<SourceWorkspaceSummary>(service.Get("user-a", done.Text));

        Assert.Equal(0, KindCount(summary, "FormsXmlExport"));
        Assert.Equal(1, KindCount(summary, "FormsModuleSource"));
        Assert.Equal(1, KindCount(summary, "PlSqlProgramUnit"));
    }

    [Fact]
    public async Task Xml_beside_forms_modules_still_counts_as_an_export()
    {
        using var service = new SourceWorkspaceService(_root);
        using MemoryStream archive = BuildArchive(
            ("legacy/forms/ORD_ENTRY.xml", "<Module />"),
            ("exports/ORDERS_form.xml", "<Module />"));

        List<SourceProgress> progress = await Collect(service.ExtractAsync("user-a", archive, "exports.zip", CancellationToken.None));

        SourceProgress done = Assert.Single(progress, step => step.Level == "done");
        SourceWorkspaceSummary summary = Assert.IsType<SourceWorkspaceSummary>(service.Get("user-a", done.Text));

        Assert.Equal(2, KindCount(summary, "FormsXmlExport"));
    }

    [Fact]
    public async Task Workspaces_are_not_readable_by_another_signed_in_user()
    {
        using var service = new SourceWorkspaceService(_root);
        using MemoryStream archive = BuildArchive(("forms/ORD.fmb", "form"));

        List<SourceProgress> progress = await Collect(service.ExtractAsync("user-a", archive, "orders.zip", CancellationToken.None));
        string workspaceId = Assert.Single(progress, step => step.Level == "done").Text;

        Assert.NotNull(service.Get("user-a", workspaceId));
        Assert.Null(service.Get("user-b", workspaceId));
        Assert.False(service.Release("user-b", workspaceId));
        Assert.NotNull(service.Get("user-a", workspaceId));
        Assert.True(service.Release("user-a", workspaceId));
        Assert.Null(service.Get("user-a", workspaceId));
    }

    [Fact]
    public async Task Retained_workspaces_cannot_be_released_until_the_worker_is_done()
    {
        using var service = new SourceWorkspaceService(_root);
        using MemoryStream archive = BuildArchive(("forms/ORD.fmb", "form"));
        string workspaceId = Assert.Single(
            await Collect(service.ExtractAsync("user-a", archive, "orders.zip", CancellationToken.None)),
            step => step.Level == "done").Text;

        using (service.Retain(workspaceId))
        {
            Assert.False(service.Release("user-a", workspaceId));
            Assert.NotNull(service.Get("user-a", workspaceId));
        }

        Assert.True(service.Release("user-a", workspaceId));
    }

    [Fact]
    public async Task Copied_files_are_locked_read_only()
    {
        using var service = new SourceWorkspaceService(_root);
        using MemoryStream archive = BuildArchive(("forms/ORD.fmb", "form"));

        await Collect(service.ExtractAsync("user-a", archive, "orders.zip", CancellationToken.None));

        string copied = Assert.Single(Directory.EnumerateFiles(_root, "ORD.fmb", SearchOption.AllDirectories));
        Assert.True(File.GetAttributes(copied).HasFlag(FileAttributes.ReadOnly));
    }

    [Fact]
    public async Task Reports_an_error_for_a_file_that_is_not_a_zip()
    {
        using var service = new SourceWorkspaceService(_root);
        using var notAZip = new MemoryStream(Encoding.UTF8.GetBytes("this is plain text"));

        List<SourceProgress> progress = await Collect(service.ExtractAsync("user-a", notAZip, "notes.txt", CancellationToken.None));

        Assert.Contains(progress, step => step.Level == "error");
        Assert.DoesNotContain(progress, step => step.Level == "done");
    }

    private static int KindCount(SourceWorkspaceSummary summary, string kind) =>
        summary.Artifacts.SingleOrDefault(artifact => artifact.Kind == kind)?.Count ?? 0;

    private static async Task<List<SourceProgress>> Collect(IAsyncEnumerable<SourceProgress> source)
    {
        List<SourceProgress> collected = [];
        await foreach (SourceProgress step in source)
        {
            collected.Add(step);
        }

        return collected;
    }

    private static MemoryStream BuildArchive(params (string Path, string Content)[] entries)
    {
        var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach ((string path, string content) in entries)
            {
                ZipArchiveEntry entry = archive.CreateEntry(path);
                using var writer = new StreamWriter(entry.Open());
                writer.Write(content);
            }
        }

        buffer.Position = 0;
        return buffer;
    }
}
