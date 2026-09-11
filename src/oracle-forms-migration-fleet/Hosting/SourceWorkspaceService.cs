// Copyright (c) Microsoft. All rights reserved.

using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace OracleFormsMigrationFleet.Hosting;

/// <summary>A single acquisition step reported back to the operator console as it happens.</summary>
public sealed record SourceProgress(string Level, string Text);

public sealed record SourceArtifact(string Kind, int Count, string Example);

public sealed record SourceWorkspaceSummary(
    string WorkspaceId,
    string Origin,
    string OriginLabel,
    int FileCount,
    long ByteCount,
    string SourceRoot,
    IReadOnlyList<SourceArtifact> Artifacts,
    DateTimeOffset AcquiredUtc,
    DateTimeOffset ExpiresUtc);

/// <summary>
/// Acquires a read-only copy of customer source into a per-session sandbox.
///
/// Every workspace is owned by one authenticated principal, lives under a server-generated
/// identifier, and is deleted on a timer. The original repository or archive is never written to:
/// clones are shallow and detached, archives are expanded into a fresh directory, and every file is
/// marked read-only once acquisition completes.
/// </summary>
public sealed class SourceWorkspaceService : IDisposable
{
    private const int MaxFiles = 60_000;
    private const long MaxBytes = 512L * 1024 * 1024;
    private const long MaxArchiveBytes = 256L * 1024 * 1024;

    // Container Apps allocates 4 GiB of ephemeral storage at 1 vCPU and the app image shares it, so
    // the workspaces collectively stay well inside that budget rather than filling the replica disk.
    private const long MaxTotalBytes = 2L * 1024 * 1024 * 1024;

    private static readonly TimeSpan Lifetime = TimeSpan.FromHours(4);
    private static readonly TimeSpan CloneTimeout = TimeSpan.FromMinutes(5);

    private static readonly HashSet<string> AllowedHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "github.com", "www.github.com", "dev.azure.com", "gitlab.com", "bitbucket.org",
    };

    private readonly ConcurrentDictionary<string, WorkspaceRecord> _workspaces = new(StringComparer.Ordinal);
    private readonly string _root;
    private readonly Timer _sweeper;

    public SourceWorkspaceService(string? rootOverride = null)
    {
        _root = rootOverride ?? Path.Combine(Path.GetTempPath(), "workbench-sources");
        Directory.CreateDirectory(_root);
        _sweeper = new Timer(_ => Sweep(), null, TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(10));
    }

    private sealed record WorkspaceRecord(string Owner, string Path, SourceWorkspaceSummary Summary);

    /// <summary>True when the address is an https URL on a supported host with no embedded credentials.</summary>
    public static bool TryParseRepositoryUrl(string? raw, out Uri? repository, out string error)
    {
        repository = null;
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(raw) || raw.StartsWith('-'))
        {
            error = "Enter the https address of the repository.";
            return false;
        }

        if (!Uri.TryCreate(raw.Trim(), UriKind.Absolute, out Uri? parsed))
        {
            error = "That is not a valid web address.";
            return false;
        }

        if (parsed.Scheme != Uri.UriSchemeHttps)
        {
            error = "Only https addresses are supported.";
            return false;
        }

        if (!string.IsNullOrEmpty(parsed.UserInfo))
        {
            error = "Remove the sign-in details from the address. Tokens are never accepted here.";
            return false;
        }

        bool allowed = AllowedHosts.Contains(parsed.Host) ||
            parsed.Host.EndsWith(".visualstudio.com", StringComparison.OrdinalIgnoreCase);
        if (!allowed)
        {
            error = "Use GitHub, Azure DevOps, GitLab, or Bitbucket.";
            return false;
        }

        repository = parsed;
        return true;
    }

    public SourceWorkspaceSummary? Get(string owner, string workspaceId) =>
        _workspaces.TryGetValue(workspaceId, out WorkspaceRecord? record) &&
        string.Equals(record.Owner, owner, StringComparison.Ordinal)
            ? record.Summary
            : null;

    public bool Release(string owner, string workspaceId)
    {
        if (!_workspaces.TryGetValue(workspaceId, out WorkspaceRecord? record) ||
            !string.Equals(record.Owner, owner, StringComparison.Ordinal))
        {
            return false;
        }

        _workspaces.TryRemove(workspaceId, out _);
        DeleteDirectory(record.Path);
        return true;
    }

    /// <summary>Frees expired workspaces first, then reports whether the shared disk budget allows another.</summary>
    private bool TryReserveDisk()
    {
        Sweep();
        return _workspaces.Values.Sum(record => record.Summary.ByteCount) + MaxBytes <= MaxTotalBytes;
    }

    public async IAsyncEnumerable<SourceProgress> CloneAsync(
        string owner,
        string repositoryUrl,
        string? branch,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (!TryParseRepositoryUrl(repositoryUrl, out Uri? repository, out string error))
        {
            yield return new SourceProgress("error", error);
            yield break;
        }

        if (branch is not null && !IsSafeRefName(branch))
        {
            yield return new SourceProgress("error", "That branch name contains characters that are not allowed.");
            yield break;
        }

        if (!TryReserveDisk())
        {
            yield return new SourceProgress("error", "The workbench is holding too many source copies right now. Delete one and try again.");
            yield break;
        }

        string workspaceId = NewWorkspaceId();
        string path = Path.Combine(_root, workspaceId);
        Directory.CreateDirectory(path);

        yield return new SourceProgress("info", $"Workspace {workspaceId} created for this session.");
        yield return new SourceProgress("info", $"Connecting to {repository!.Host}...");

        List<string> arguments =
        [
            "-c", "core.symlinks=false",
            "-c", "core.hooksPath=/dev/null",
            "-c", "http.followRedirects=false",
            "-c", "credential.helper=",
            "clone", "--depth", "1", "--single-branch", "--no-tags",
            "--no-recurse-submodules", "--progress",
        ];

        if (!string.IsNullOrWhiteSpace(branch))
        {
            arguments.Add("--branch");
            arguments.Add(branch);
        }

        arguments.Add("--");
        arguments.Add(repository.GetLeftPart(UriPartial.Path));
        arguments.Add(path);

        bool cloned = true;
        await foreach (SourceProgress progress in RunGitAsync(arguments, cancellationToken))
        {
            if (progress.Level == "error")
            {
                cloned = false;
            }

            yield return progress;
        }

        if (!cloned)
        {
            DeleteDirectory(path);
            yield return new SourceProgress("error", "Clone failed. Private repositories are not supported yet; export a zip instead.");
            yield break;
        }

        // The .git directory is only needed to fetch. Dropping it removes any chance of a later
        // command pushing back to the customer's remote.
        DeleteDirectory(Path.Combine(path, ".git"));
        yield return new SourceProgress("info", "Remote metadata removed. This copy cannot push back to your repository.");

        string label = $"{repository.Host}{repository.AbsolutePath.TrimEnd('/')}";
        await foreach (SourceProgress progress in FinishAsync(owner, workspaceId, path, repository.ToString(), label, cancellationToken))
        {
            yield return progress;
        }
    }

    public async IAsyncEnumerable<SourceProgress> ExtractAsync(
        string owner,
        Stream archive,
        string fileName,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (!TryReserveDisk())
        {
            yield return new SourceProgress("error", "The workbench is holding too many source copies right now. Delete one and try again.");
            yield break;
        }

        string workspaceId = NewWorkspaceId();
        string path = Path.Combine(_root, workspaceId);
        Directory.CreateDirectory(path);
        string fullRoot = Path.GetFullPath(path) + Path.DirectorySeparatorChar;

        yield return new SourceProgress("info", $"Workspace {workspaceId} created for this session.");
        yield return new SourceProgress("info", $"Opening {Sanitize(fileName)}...");

        bool failed = false;
        string failure = string.Empty;
        long written = 0;
        int files = 0;

        ZipArchive? zip = null;
        try
        {
            zip = new ZipArchive(archive, ZipArchiveMode.Read, leaveOpen: true);
        }
        catch (InvalidDataException)
        {
            failed = true;
            failure = "That file is not a readable zip archive.";
        }

        if (!failed)
        {
            using (zip)
            {
                foreach (ZipArchiveEntry entry in zip!.Entries)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        failed = true;
                        failure = "Cancelled.";
                        break;
                    }

                    if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\'))
                    {
                        continue;
                    }

                    // Reject zip-slip before touching the file system: the resolved destination has
                    // to stay underneath this workspace.
                    string destination = Path.GetFullPath(Path.Combine(path, entry.FullName));
                    if (!destination.StartsWith(fullRoot, StringComparison.Ordinal))
                    {
                        failed = true;
                        failure = $"The archive tries to write outside its folder ({Sanitize(entry.FullName)}). Nothing was kept.";
                        break;
                    }

                    written += entry.Length;
                    files++;
                    if (written > MaxArchiveBytes || files > MaxFiles)
                    {
                        failed = true;
                        failure = "That archive is larger than this workbench accepts.";
                        break;
                    }

                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    entry.ExtractToFile(destination, overwrite: true);
                }
            }
        }

        if (failed)
        {
            DeleteDirectory(path);
            yield return new SourceProgress("error", failure);
            yield break;
        }

        yield return new SourceProgress("info", $"Expanded {files} files.");

        await foreach (SourceProgress progress in FinishAsync(owner, workspaceId, path, Sanitize(fileName), Sanitize(fileName), cancellationToken))
        {
            yield return progress;
        }
    }

    private async IAsyncEnumerable<SourceProgress> FinishAsync(
        string owner,
        string workspaceId,
        string path,
        string origin,
        string label,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        yield return new SourceProgress("info", "Indexing files...");

        SourceInventory? inventory = null;
        try
        {
            inventory = await Task.Run(() => SourceInventory.Build(path, MaxFiles, MaxBytes), cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            inventory = null;
        }

        if (inventory is null)
        {
            DeleteDirectory(path);
            yield return new SourceProgress("error", "The copied files could not be read.");
            yield break;
        }

        if (inventory.Truncated)
        {
            yield return new SourceProgress("warn", "This source is very large, so only part of it was indexed.");
        }

        foreach (SourceArtifact artifact in inventory.Artifacts)
        {
            yield return new SourceProgress("found", $"{artifact.Count} x {artifact.Kind} ({artifact.Example})");
        }

        if (inventory.Artifacts.Count == 0)
        {
            yield return new SourceProgress("warn", "No Oracle Forms or PL/SQL artifacts were recognised.");
        }

        MarkReadOnly(path);
        yield return new SourceProgress("info", "Copy locked read-only. The workbench cannot modify it.");

        DateTimeOffset now = DateTimeOffset.UtcNow;
        var summary = new SourceWorkspaceSummary(
            workspaceId,
            origin,
            label,
            inventory.FileCount,
            inventory.ByteCount,
            inventory.SourceRoot,
            inventory.Artifacts,
            now,
            now.Add(Lifetime));

        _workspaces[workspaceId] = new WorkspaceRecord(owner, path, summary);
        yield return new SourceProgress("done", workspaceId);
    }

    private static async IAsyncEnumerable<SourceProgress> RunGitAsync(
        IReadOnlyList<string> arguments,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        // git reads credentials from the environment and from any terminal it can find; both are
        // disabled so a private repository fails fast instead of prompting or reusing a token.
        startInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";
        startInfo.Environment["GIT_ASKPASS"] = "";
        startInfo.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        startInfo.Environment["GIT_ALLOW_PROTOCOL"] = "https";

        var channel = System.Threading.Channels.Channel.CreateUnbounded<SourceProgress>();
        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };

        process.OutputDataReceived += (_, e) => Publish(channel, "info", e.Data);
        process.ErrorDataReceived += (_, e) => Publish(channel, "info", e.Data);

        bool started = false;
        string? startFailure = null;
        try
        {
            started = process.Start();
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            startFailure = "Git is not available in this container.";
        }

        if (startFailure is not null || !started)
        {
            yield return new SourceProgress("error", startFailure ?? "Git could not be started.");
            yield break;
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(CloneTimeout);

        _ = Task.Run(async () =>
        {
            try
            {
                await process.WaitForExitAsync(timeout.Token);
                if (process.ExitCode != 0)
                {
                    channel.Writer.TryWrite(new SourceProgress("error", $"Git exited with code {process.ExitCode}."));
                }
            }
            catch (OperationCanceledException)
            {
                TryKill(process);
                channel.Writer.TryWrite(new SourceProgress("error", "The clone took too long and was stopped."));
            }
            finally
            {
                channel.Writer.TryComplete();
            }
        }, CancellationToken.None);

        await foreach (SourceProgress progress in channel.Reader.ReadAllAsync(CancellationToken.None))
        {
            yield return progress;
        }
    }

    private static void Publish(System.Threading.Channels.Channel<SourceProgress> channel, string level, string? line)
    {
        if (!string.IsNullOrWhiteSpace(line))
        {
            channel.Writer.TryWrite(new SourceProgress(level, Sanitize(line)));
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or NotSupportedException)
        {
            // The process already exited.
        }
    }

    /// <summary>Strips control characters so remote output cannot rewrite the operator's console.</summary>
    private static string Sanitize(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (char character in value)
        {
            builder.Append(char.IsControl(character) ? ' ' : character);
        }

        return builder.ToString().Trim();
    }

    private static bool IsSafeRefName(string branch) =>
        branch.Length <= 200 &&
        !branch.StartsWith('-') &&
        branch.All(character => char.IsLetterOrDigit(character) || character is '.' or '_' or '-' or '/');

    private static string NewWorkspaceId() =>
        Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

    private static void MarkReadOnly(string path)
    {
        foreach (string file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
        {
            try
            {
                File.SetAttributes(file, FileAttributes.ReadOnly);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Best effort; the copy is already isolated from the customer's original.
            }
        }
    }

    private static void DeleteDirectory(string path)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        try
        {
            foreach (string file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(path, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Swept again on the next pass.
        }
    }

    private void Sweep()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        foreach (KeyValuePair<string, WorkspaceRecord> entry in _workspaces)
        {
            if (entry.Value.Summary.ExpiresUtc <= now && _workspaces.TryRemove(entry.Key, out WorkspaceRecord? removed))
            {
                DeleteDirectory(removed.Path);
            }
        }
    }

    public void Dispose()
    {
        _sweeper.Dispose();
        foreach (KeyValuePair<string, WorkspaceRecord> entry in _workspaces)
        {
            DeleteDirectory(entry.Value.Path);
        }

        _workspaces.Clear();
    }
}
