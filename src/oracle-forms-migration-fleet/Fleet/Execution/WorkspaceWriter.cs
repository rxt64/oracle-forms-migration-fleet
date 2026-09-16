// Copyright (c) Microsoft. All rights reserved.

using System.Globalization;
using System.Text;

namespace OracleFormsMigrationFleet.Fleet.Execution;

/// <summary>Raised when a path would read or write outside the workspace root.</summary>
public sealed class WorkspacePathException(string message) : InvalidOperationException(message);

/// <summary>Which workspace intake limit a supplied tree exceeded.</summary>
public enum WorkspaceLimitKind
{
    /// <summary>One file is larger than the byte budget the caller allowed for reading it.</summary>
    FileSize,

    /// <summary>A directory holds more files than the caller allowed itself to enumerate.</summary>
    FileCount,
}

/// <summary>
/// Raised when a read or an enumeration would have had to discard part of the input to stay inside a
/// caller-supplied limit.
///
/// Both operations used to truncate silently: a 40 MiB export came back as its first 8 MiB of bytes, and
/// a tree of 60,000 files came back as 20,000 of them. Every caller then treated that prefix as the whole
/// input, so an oversized module could be parsed as a complete document and a large estate could be
/// converted, reported, and attested while most of it was never opened. Refusing is the only honest
/// answer, because there is no way to signal partial input through a return value every existing caller
/// already reads as complete.
///
/// The reported path is workspace-relative and the message quotes only that, so neither the host layout
/// nor the session root reaches an operator-visible report.
/// </summary>
public sealed class WorkspaceLimitExceededException(
    WorkspaceLimitKind kind,
    string path,
    long limit,
    long actual,
    bool actualIsLowerBound,
    string message) : InvalidOperationException(message)
{
    /// <summary>Which limit was exceeded.</summary>
    public WorkspaceLimitKind Kind { get; } = kind;

    /// <summary>The workspace-relative file or directory the limit was applied to. Never an absolute path.</summary>
    public string Path { get; } = path;

    /// <summary>The caller-supplied budget: bytes for a file size, file count otherwise.</summary>
    public long Limit { get; } = limit;

    /// <summary>
    /// What was actually present, in the same unit as the limit. For a file count this is the number of
    /// files observed before the refusal, which is the limit plus one: enumeration stops at the first file
    /// beyond the budget rather than walking a hostile tree to completion just to report a total.
    /// </summary>
    public long Actual { get; } = actual;

    /// <summary>True when the actual value is a floor rather than the exact total.</summary>
    public bool ActualIsLowerBound { get; } = actualIsLowerBound;
}

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

    public bool FileExists(string relativePath) =>
        TryResolve(relativePath, out string absolutePath, out _) && File.Exists(absolutePath);

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

    /// <summary>
    /// Reads a workspace file in full, or refuses it. A file larger than the supplied budget throws before
    /// a single byte is read, so no caller can receive a prefix and parse it as a whole document.
    /// </summary>
    public string ReadText(string relativePath, long maxBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytes);

        string absolute = Resolve(relativePath);
        string reported = WorkspacePath.Normalize(relativePath);

        using FileStream stream = File.OpenRead(absolute);

        // A single allocation cannot exceed Array.MaxLength, so that is a real ceiling on any budget.
        long limit = Math.Min(maxBytes, Array.MaxLength);
        long length = stream.Length;

        if (length > limit)
        {
            throw new WorkspaceLimitExceededException(
                WorkspaceLimitKind.FileSize,
                reported,
                limit,
                length,
                actualIsLowerBound: false,
                $"'{reported}' is {length.ToString(CultureInfo.InvariantCulture)} bytes, which exceeds the " +
                $"{limit.ToString(CultureInfo.InvariantCulture)}-byte limit this phase reads. It was not read at all: " +
                "taking only its first bytes would have produced a truncated document that parses as though it were " +
                "complete. Split the file or supply a smaller export.");
        }

        byte[] buffer = new byte[(int)length];
        stream.ReadExactly(buffer);

        string text = s_utf8.GetString(buffer);
        return text.Length > 0 && text[0] == '\uFEFF' ? text[1..] : text;
    }

    /// <summary>
    /// Workspace-relative file listing under the supplied directory, ordered for determinism.
    ///
    /// A tree holding more readable files than the budget allows throws rather than returning the first
    /// <paramref name="maxFiles"/> of them, which every caller would otherwise treat as the whole estate.
    /// </summary>
    public IReadOnlyList<WorkspaceFile> EnumerateFiles(string relativePath, int maxFiles)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxFiles);

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

            // Detected one file beyond the budget, so a tree of exactly maxFiles is complete and a tree of
            // maxFiles + 1 is refused instead of being silently reported as the former.
            if (files.Count == maxFiles)
            {
                throw new WorkspaceLimitExceededException(
                    WorkspaceLimitKind.FileCount,
                    root,
                    maxFiles,
                    (long)maxFiles + 1,
                    actualIsLowerBound: true,
                    $"'{root}' holds more than {maxFiles.ToString(CultureInfo.InvariantCulture)} files, which is the limit " +
                    "this phase enumerates. Nothing was listed, because returning the first " +
                    $"{maxFiles.ToString(CultureInfo.InvariantCulture)} would have been read downstream as the whole estate, " +
                    "and every count, conversion, and report derived from it would have described a fraction of the source " +
                    "as though it were all of it. Narrow the source root or split the estate.");
            }

            files.Add(new WorkspaceFile($"{root}/{full[prefix.Length..].Replace('\\', '/')}", length));
        }

        return [.. files.OrderBy(file => file.RelativePath, StringComparer.Ordinal)];
    }
}
