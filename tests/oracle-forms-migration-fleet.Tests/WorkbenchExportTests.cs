// Copyright (c) Microsoft. All rights reserved.

using System.IO.Compression;
using OracleFormsMigrationFleet.Hosting;

namespace OracleFormsMigrationFleet.Tests;

public class WorkbenchExportTests
{
    private static string NewRunRoot()
    {
        string root = Path.Combine(Path.GetTempPath(), "ofmf-export-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(Path.Combine(root, "out", "application", "backend"));
        File.WriteAllText(Path.Combine(root, "out", "application", "backend", "pom.xml"), "<project/>");
        File.WriteAllText(Path.Combine(root, "out", "schema.sql"), "CREATE TABLE t (a int);");
        return root;
    }

    [Fact]
    public void The_export_contains_every_generated_file_under_relative_names()
    {
        string root = NewRunRoot();
        try
        {
            using MemoryStream buffer = new();
            WorkbenchExecution.WriteExport(root, buffer);
            buffer.Position = 0;

            using ZipArchive archive = new(buffer, ZipArchiveMode.Read);
            string[] entries = [.. archive.Entries.Select(entry => entry.FullName).Order(StringComparer.Ordinal)];

            Assert.Equal(["out/application/backend/pom.xml", "out/schema.sql"], entries);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void No_entry_is_rooted_or_carries_a_traversal_segment()
    {
        string root = NewRunRoot();
        try
        {
            using MemoryStream buffer = new();
            WorkbenchExecution.WriteExport(root, buffer);
            buffer.Position = 0;

            using ZipArchive archive = new(buffer, ZipArchiveMode.Read);
            foreach (ZipArchiveEntry entry in archive.Entries)
            {
                Assert.False(Path.IsPathRooted(entry.FullName));
                Assert.DoesNotContain("..", entry.FullName, StringComparison.Ordinal);
                Assert.DoesNotContain('\\', entry.FullName);
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void An_unknown_workspace_is_refused_without_revealing_a_path()
    {
        SourceWorkspaceService workspaces = new(Path.Combine(Path.GetTempPath(), "ofmf-export-tests", Guid.NewGuid().ToString("n")));

        bool resolved = WorkbenchExecution.TryResolveExport(
            workspaces, "owner@contoso.com", "not-a-workspace", out string path, out int status, out string error);

        Assert.False(resolved);
        Assert.Equal(404, status);
        Assert.Empty(path);
        Assert.DoesNotContain(Path.GetTempPath(), error, StringComparison.OrdinalIgnoreCase);
    }
}
