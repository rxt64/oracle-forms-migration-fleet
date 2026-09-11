// Copyright (c) Microsoft. All rights reserved.

using System.Text;

namespace OracleFormsMigrationFleet.Fleet.Execution;

/// <summary>Raised when a path would read or write outside the workspace root.</summary>
public sealed class WorkspacePathException(string message) : InvalidOperationException(message);

public sealed record WorkspaceFile(string RelativePath, long Length);

/// <summary>
/// Every read and write an execution adapter performs goes through this type. Paths are validated with
/// <see cref="WorkspacePath"/> and then re-checked after resolution, so rooted paths, URI syntax, drive
/// qualifiers, and '..' traversal cannot escape the caller-supplied root even through a symlinked segment.
/// </summary>
public sealed class WorkspaceWriter
{
    private static readonly UTF8Encoding s_utf8 = new(encoderShouldEmitUTF8Identifier: false);

    private readonly string _prefix;

    public WorkspaceWriter(string workspaceRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);

        if (!Path.IsPathRooted(workspaceRoot))
        {
            throw new ArgumentException("The workspace root must be an absolute path.", nameof(workspaceRoot));
        }

        Root = Path.GetFullPath(workspaceRoot).TrimEnd(Path.DirectorySeparatorChar);
        _prefix = Root + Path.DirectorySeparatorChar;
    }

    public string Root { get; }

    public bool TryResolve(string? relativePath, out string absolutePath, out string error)
    {
        absolutePath = string.Empty;

        if (WorkspacePath.Validate(relativePath, "path") is string invalid)
        {
            error = invalid;
            return false;
        }

        string candidate = Path.GetFullPath(Path.Combine(Root, WorkspacePath.Normalize(relativePath!)));

        if (!candidate.StartsWith(_prefix, StringComparison.Ordinal) &&
            !string.Equals(candidate, Root, StringComparison.Ordinal))
        {
            error = "The resolved path is outside the workspace root and was rejected.";
            return false;
        }

        absolutePath = candidate;
        error = string.Empty;
        return true;
    }

    public string Resolve(string? relativePath) =>
        TryResolve(relativePath, out string absolutePath, out string error)
            ? absolutePath
            : throw new WorkspacePathException($"{error} ({relativePath})");

    public bool DirectoryExists(string relativePath) =>
        TryResolve(relativePath, out string absolutePath, out _) && Directory.Exists(absolutePath);

    /// <summary>Writes UTF-8 text with LF endings so repeated runs produce byte-identical artifacts.</summary>
    public void WriteText(string relativePath, string content)
    {
        string absolute = Resolve(relativePath);
        string? directory = Path.GetDirectoryName(absolute);

        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(absolute, (content ?? string.Empty).Replace("\r\n", "\n", StringComparison.Ordinal), s_utf8);
    }

    public string ReadText(string relativePath, long maxBytes)
    {
        string absolute = Resolve(relativePath);

        using FileStream stream = File.OpenRead(absolute);
        int take = (int)Math.Min(maxBytes, stream.Length);
        byte[] buffer = new byte[take];
        stream.ReadExactly(buffer);

        string text = s_utf8.GetString(buffer);
        return text.Length > 0 && text[0] == '\uFEFF' ? text[1..] : text;
    }

    /// <summary>Workspace-relative file listing under <paramref name="relativePath"/>, ordered for determinism.</summary>
    public IReadOnlyList<WorkspaceFile> EnumerateFiles(string relativePath, int maxFiles)
    {
        string absolute = Resolve(relativePath);

        if (!Directory.Exists(absolute))
        {
            return [];
        }

        string root = WorkspacePath.Normalize(relativePath);
        string prefix = Path.GetFullPath(absolute).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        List<WorkspaceFile> files = [];

        foreach (string file in Directory.EnumerateFiles(absolute, "*", SearchOption.AllDirectories))
        {
            if (files.Count >= maxFiles)
            {
                break;
            }

            string full = Path.GetFullPath(file);
            if (!full.StartsWith(prefix, StringComparison.Ordinal))
            {
                continue;
            }

            long length;
            try
            {
                length = new FileInfo(full).Length;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            files.Add(new WorkspaceFile($"{root}/{full[prefix.Length..].Replace('\\', '/')}", length));
        }

        return [.. files.OrderBy(file => file.RelativePath, StringComparer.Ordinal)];
    }
}
