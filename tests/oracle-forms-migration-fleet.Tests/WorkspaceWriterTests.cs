// Copyright (c) Microsoft. All rights reserved.

using System.Diagnostics;
using OracleFormsMigrationFleet.Fleet.Execution;

namespace OracleFormsMigrationFleet.Tests;

public class WorkspaceWriterTests
{
    [Theory]
    [InlineData("../escape.txt")]
    [InlineData("out/../../escape.txt")]
    [InlineData("..")]
    [InlineData("/etc/passwd")]
    [InlineData("\\\\server\\share\\payload.txt")]
    [InlineData("file:///etc/passwd")]
    [InlineData("https://contoso.example/payload.txt")]
    [InlineData("C:/Windows/System32/drivers/etc/hosts")]
    [InlineData("C:\\Windows\\System32\\drivers\\etc\\hosts")]
    [InlineData("out/report.md:stream")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Paths_that_could_leave_the_workspace_are_rejected(string? path)
    {
        using TemporaryWorkspace workspace = new();
        WorkspaceWriter writer = new(workspace.Root);

        Assert.False(writer.TryResolve(path, out _, out string error));
        Assert.NotEmpty(error);
        Assert.Throws<WorkspacePathException>(() => writer.Resolve(path));
        Assert.Throws<WorkspacePathException>(() => writer.WriteText(path!, "payload"));
    }

    [Fact]
    public void Workspace_relative_paths_resolve_underneath_the_root()
    {
        using TemporaryWorkspace workspace = new();
        WorkspaceWriter writer = new(workspace.Root);

        Assert.True(writer.TryResolve("out/analysis/report.md", out string resolved, out _));
        Assert.StartsWith(writer.Root + Path.DirectorySeparatorChar, resolved, StringComparison.Ordinal);
    }

    [Fact]
    public void A_path_beneath_a_directory_link_is_rejected()
    {
        using TemporaryWorkspace workspace = new();
        string outside = Path.Combine(Path.GetTempPath(), $"ofm-workspace-outside-{Guid.NewGuid():N}");
        string link = Path.Combine(workspace.Root, "out");
        Directory.CreateDirectory(outside);

        try
        {
            CreateDirectoryLink(link, outside);

            WorkspaceWriter writer = new(workspace.Root);

            Assert.False(writer.TryResolve("out/report.md", out _, out string error));
            Assert.Contains("symbolic link or junction", error, StringComparison.Ordinal);
        }
        finally
        {
            try
            {
                Directory.Delete(link);
            }
            catch (DirectoryNotFoundException)
            {
            }
            Directory.Delete(outside, recursive: true);
        }
    }

    private static void CreateDirectoryLink(string link, string target)
    {
        if (!OperatingSystem.IsWindows())
        {
            Directory.CreateSymbolicLink(link, target);
            return;
        }

        ProcessStartInfo startInfo = new("cmd.exe", $"/d /c mklink /J \"{link}\" \"{target}\"")
        {
            UseShellExecute = false,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        using Process process = Process.Start(startInfo)!;
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, process.StandardError.ReadToEnd());
    }

    [Fact]
    public void Written_text_uses_lf_endings_and_no_byte_order_mark()
    {
        using TemporaryWorkspace workspace = new();
        WorkspaceWriter writer = new(workspace.Root);

        writer.WriteText("out/analysis/report.md", "first\r\nsecond\n");

        byte[] bytes = File.ReadAllBytes(Path.Combine(workspace.Root, "out", "analysis", "report.md"));
        Assert.DoesNotContain((byte)'\r', bytes);
        Assert.NotEqual((byte)0xEF, bytes[0]);
        Assert.Equal("first\nsecond\n", writer.ReadText("out/analysis/report.md", 1024));
    }

    [Fact]
    public void Enumeration_returns_ordered_workspace_relative_paths()
    {
        using TemporaryWorkspace workspace = new();
        workspace.WriteFile("legacy/forms/z.sql", "-- z");
        workspace.WriteFile("legacy/forms/a.sql", "-- a");
        workspace.WriteFile("legacy/forms/nested/b.sql", "-- b");

        WorkspaceWriter writer = new(workspace.Root);

        Assert.Equal(
            ["legacy/forms/a.sql", "legacy/forms/nested/b.sql", "legacy/forms/z.sql"],
            writer.EnumerateFiles("legacy/forms", 100).Select(file => file.RelativePath));
    }

    [Fact]
    public void A_relative_workspace_root_is_rejected()
    {
        Assert.Throws<ArgumentException>(() => new WorkspaceWriter("relative/root"));
    }
}
