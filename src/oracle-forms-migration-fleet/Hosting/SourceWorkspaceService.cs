// Copyright (c) Microsoft. All rights reserved.

using System.Collections.Concurrent;
using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using OracleFormsMigrationFleet.Fleet;
using OracleFormsMigrationFleet.Fleet.Execution;

namespace OracleFormsMigrationFleet.Hosting;

/// <summary>
/// A single acquisition step reported back to the operator console as it happens.
///
/// <paramref name="Signal"/> is the typed framing the browser renders. Lines that carry none are
/// raw tool output — git's own progress, for instance — and stay inspectable detail rather than
/// something the summary is allowed to interpret.
/// </summary>
public sealed record SourceProgress(string Level, string Text, ProgressSignal? Signal = null);

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
/// What the server knows about an owned source copy, including facts the browser is never given.
///
/// <paramref name="SnapshotHash"/> identifies the exact bytes that were acquired. It is server-only
/// metadata: an authorization is bound to it so a grant obtained against one source cannot be replayed
/// against a different one, and it is deliberately absent from <see cref="SourceWorkspaceSummary"/>,
/// which is serialized to the operator console.
/// </summary>
public sealed record SourceWorkspaceFacts(SourceWorkspaceSummary Summary, string SnapshotHash);

/// <summary>One source file exactly as it was hashed, kept from the pass that produced a snapshot digest.</summary>
public sealed record TrustedSourceFile(string RelativePath, byte[] Content);

/// <summary>
/// What a source folder currently hashes to, and the bytes behind that digest for the files a caller kept.
///
/// <paramref name="SnapshotHash"/> is computed by the same calculator every authorization was issued
/// against, so a caller establishes that the bytes it holds are the approved ones by comparing digests and
/// never by re-reading the paths.
/// </summary>
public sealed record TrustedSourceRead(string SnapshotHash, IReadOnlyList<TrustedSourceFile> Files);

/// <summary>
/// What an atomic prepared-artifact commit produced.
///
/// <paramref name="WorkspacePaths"/> is populated only when <paramref name="Committed"/> is true, and is
/// in the order the caller supplied its payloads.
/// </summary>
public sealed record PreparedCommitResult(bool Committed, IReadOnlyList<string> WorkspacePaths, string Error)
{
    public static PreparedCommitResult Refused(string error) => new(false, [], error);
}

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
    private readonly ConcurrentDictionary<string, int> _retained = new(StringComparer.Ordinal);

    // One writer at a time per session copy, shared by the module and schema commits. The ledger append
    // is a read-modify-write over a whole document, so two modules prepared at the same moment would
    // otherwise each write a ledger built from the state before the other one started, and the second
    // rename would silently drop the first module's claim while leaving its bytes on disk looking
    // admitted. Bounded by the number of live workspaces, and released when the workspace is.
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _commitLocks = new(StringComparer.Ordinal);
    private readonly string _root;
    private readonly Timer _sweeper;

    public SourceWorkspaceService(string? rootOverride = null)
    {
        _root = rootOverride ?? Path.Combine(Path.GetTempPath(), "workbench-sources");
        Directory.CreateDirectory(_root);
        _sweeper = new Timer(_ => Sweep(), null, TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(10));
    }

    private sealed record WorkspaceRecord(string Owner, string Path, SourceWorkspaceSummary Summary, string SnapshotHash);
    private sealed record ScopedSource(SourceWorkspaceSummary Summary, string Path);

    private const string AcquisitionPurpose =
        "Taking a private read-only copy of your source so the fleet has something it can read.";

    private static ProgressSignal Acquiring(string action, string observed, string nextAction) =>
        new(ProgressOperations.SourceAcquisition, action, ProgressState.Running, AcquisitionPurpose, observed, nextAction);

    private static ProgressSignal AcquisitionFailed(string observed) =>
        new(
            ProgressOperations.SourceAcquisition,
            ProgressActions.SourceFailed,
            ProgressState.Failed,
            AcquisitionPurpose,
            observed,
            "Nothing was kept. Correct the problem above and start the copy again.");

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

    internal string? ResolveDurableRoot(string workspaceId)
    {
        if (workspaceId.Length != 32 || workspaceId.Any(character => !Uri.IsHexDigit(character)))
        {
            return null;
        }

        string root = Path.GetFullPath(_root) + Path.DirectorySeparatorChar;
        string candidate = Path.GetFullPath(Path.Combine(_root, workspaceId));
        return candidate.StartsWith(root, StringComparison.Ordinal) && Directory.Exists(candidate)
            ? candidate
            : null;
    }

    internal string? DurableSnapshotHash(string workspaceId, string sourceRoot) =>
        DurableSourcePath(workspaceId, sourceRoot) is { } selected ? SnapshotHash(selected) : null;

    private string? DurableSourcePath(string workspaceId, string sourceRoot)
    {
        string? root = ResolveDurableRoot(workspaceId);
        if (root is null || WorkspacePath.Validate(sourceRoot, "Source folder") is not null)
        {
            return null;
        }
        string normalized = WorkspacePath.Normalize(sourceRoot);
        string selected = Path.GetFullPath(Path.Combine(root, normalized.Replace('/', Path.DirectorySeparatorChar)));
        string prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        return selected.StartsWith(prefix, StringComparison.Ordinal) && Directory.Exists(selected)
            ? selected
            : null;
    }

    /// <summary>
    /// Re-reads a run's source folder and reports the snapshot it currently is, together with the exact
    /// bytes of the files the caller asked to keep.
    ///
    /// A caller comparing the returned digest with the one its run was authorized against learns whether
    /// the source is still the source that was approved. Keeping the bytes from the same pass is the point
    /// of the method: re-opening a file after checking a digest is a second read of a path that may no
    /// longer hold what the first one hashed, so what gets parsed is the buffer that went into the digest
    /// and nothing else. The algorithm is the one every other snapshot on this server is computed with, so
    /// no caller can end up comparing against a digest of its own invention.
    /// </summary>
    public TrustedSourceRead? ReadTrustedSource(
        string owner,
        string workspaceId,
        string sourceRoot,
        Func<string, bool> retain,
        long maxRetainedBytes)
    {
        ArgumentNullException.ThrowIfNull(retain);
        ArgumentOutOfRangeException.ThrowIfNegative(maxRetainedBytes);

        string? selected = OwnedSourcePath(owner, workspaceId, sourceRoot)
            ?? DurableSourcePath(workspaceId, sourceRoot);

        return selected is null ? null : SnapshotRead(selected, retain, maxRetainedBytes);
    }

    internal IDisposable Retain(string workspaceId)
    {
        _retained.AddOrUpdate(workspaceId, 1, (_, count) => count + 1);
        return new Retention(() =>
        {
            int remaining = _retained.AddOrUpdate(workspaceId, 0, (_, count) => Math.Max(0, count - 1));
            if (remaining == 0)
            {
                _retained.TryRemove(workspaceId, out _);
            }
        });
    }

    internal bool IsRetained(string workspaceId) => _retained.ContainsKey(workspaceId);

    /// <summary>
    /// Everything the server knows about a copy the caller owns, for server-side decisions only. A
    /// caller who does not own the workspace gets null rather than any fact about it.
    /// </summary>
    public SourceWorkspaceFacts? Describe(string owner, string workspaceId) =>
        _workspaces.TryGetValue(workspaceId, out WorkspaceRecord? record) &&
        string.Equals(record.Owner, owner, StringComparison.Ordinal)
            ? new SourceWorkspaceFacts(record.Summary, record.SnapshotHash)
            : null;

    /// <summary>
    /// Server-owned facts for the exact source folder a run selected. Files elsewhere in the same
    /// repository cannot satisfy evidence or change the authorization binding for this scope.
    /// </summary>
    public SourceWorkspaceFacts? Describe(string owner, string workspaceId, string sourceRoot)
    {
        ScopedSource? scoped = Scope(owner, workspaceId, sourceRoot);
        return scoped is null ? null : new SourceWorkspaceFacts(scoped.Summary, SnapshotHash(scoped.Path));
    }

    /// <summary>Scoped source inventory for planning, which needs no content digest.</summary>
    public SourceWorkspaceSummary? DescribeSummary(string owner, string workspaceId, string sourceRoot) =>
        Scope(owner, workspaceId, sourceRoot)?.Summary;

    /// <summary>
    /// The identity of the bytes the OPERATOR supplied in a source folder they own, or null when the
    /// folder is not theirs.
    ///
    /// It excludes the prepared-source folder, and that exclusion is the whole point of it existing
    /// beside <see cref="Describe(string, string, string)"/>. A preparation writes admitted artifacts
    /// into that folder itself, so binding to the full snapshot would make the fleet's own first commit
    /// look like the estate changing underneath it — every module after the first in one request, and
    /// every concurrent preparation, would deny itself. The question a preparation has to ask again
    /// after a slow gateway call is "is this still the estate I pinned a module out of", and the
    /// answer must not depend on what this server already admitted into it.
    ///
    /// It is server-only and one-way, like every other digest here.
    /// </summary>
    public string? SuppliedSourceBinding(string owner, string workspaceId, string sourceRoot) =>
        Scope(owner, workspaceId, sourceRoot) is { } scoped
            ? SnapshotRead(scoped.Path, static _ => false, 0, PreparedSourceExclusion).SnapshotHash
            : null;

    private ScopedSource? Scope(string owner, string workspaceId, string sourceRoot)
    {
        if (OwnedSourcePath(owner, workspaceId, sourceRoot) is not { } selected ||
            !_workspaces.TryGetValue(workspaceId, out WorkspaceRecord? record))
        {
            return null;
        }

        try
        {
            SourceInventory inventory = SourceInventory.Build(selected, MaxFiles, MaxBytes);
            return new ScopedSource(record.Summary with
            {
                FileCount = inventory.FileCount,
                ByteCount = inventory.ByteCount,
                SourceRoot = WorkspacePath.Normalize(sourceRoot),
                Artifacts = inventory.Artifacts,
            }, selected);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>The source folder of a workspace the caller owns, or null. One containment rule, used by both readers.</summary>
    private string? OwnedSourcePath(string owner, string workspaceId, string sourceRoot)
    {
        if (!_workspaces.TryGetValue(workspaceId, out WorkspaceRecord? record) ||
            !string.Equals(record.Owner, owner, StringComparison.Ordinal) ||
            WorkspacePath.Validate(sourceRoot, "Source folder") is not null)
        {
            return null;
        }

        string normalized = WorkspacePath.Normalize(sourceRoot);
        string root = Path.GetFullPath(record.Path);
        string selected = Path.GetFullPath(Path.Combine(root, normalized.Replace('/', Path.DirectorySeparatorChar)));
        string prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;

        return (string.Equals(selected, root, StringComparison.Ordinal) ||
            selected.StartsWith(prefix, StringComparison.Ordinal)) && Directory.Exists(selected)
                ? selected
                : null;
    }

    /// <summary>
    /// Absolute path of a workspace the caller owns, for server-side work only. The value is never
    /// returned to a browser, and a caller who does not own the workspace gets null rather than a path.
    /// </summary>
    public string? ResolveRoot(string owner, string workspaceId) =>
        _workspaces.TryGetValue(workspaceId, out WorkspaceRecord? record) &&
        string.Equals(record.Owner, owner, StringComparison.Ordinal)
            ? record.Path
            : null;

    /// <summary>
    /// Folder inside a selected source root that holds artifacts the fleet fetched from the source
    /// gateway. It sits under the source root on purpose: these bytes ARE source now, so they belong to
    /// the snapshot identity, unlike <see cref="WorkbenchExecution.OutputRoot"/>, which is run output and
    /// is excluded from it.
    /// </summary>
    public const string PreparedSourceRoot = PreparedSourceTrustStore.PreparedFolder;

    /// <summary>
    /// Writes one artifact the source gateway returned into a source folder the caller owns.
    ///
    /// <paramref name="fileName"/> is a single name segment the caller has already validated as an alias;
    /// it is re-validated here and resolved under the selected folder, so a name that escapes the
    /// workspace is refused rather than written. Nothing about the remote gateway reaches the filesystem:
    /// the gateway never supplies a path, and this method never accepts one.
    /// </summary>
    public bool TryWritePreparedArtifact(
        string owner,
        string workspaceId,
        string sourceRoot,
        string fileName,
        byte[] content,
        out string workspaceRelativePath,
        out string error)
    {
        ArgumentNullException.ThrowIfNull(content);
        workspaceRelativePath = string.Empty;

        if (!IsSafeArtifactName(fileName))
        {
            error = "The prepared artifact name is not a simple file name.";
            return false;
        }

        if (OwnedSourcePath(owner, workspaceId, sourceRoot) is not { } selected)
        {
            error = "That source folder is not part of a copy you own.";
            return false;
        }

        string folder = Path.Combine(selected, ".fleet-source", "extraction");
        string full = Path.GetFullPath(Path.Combine(folder, fileName));
        string prefix = Path.GetFullPath(selected) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, StringComparison.Ordinal))
        {
            error = "The prepared artifact did not resolve inside the source folder.";
            return false;
        }

        try
        {
            Directory.CreateDirectory(folder);
            if (File.Exists(full))
            {
                File.SetAttributes(full, FileAttributes.Normal);
            }

            File.WriteAllBytes(full, content);
            File.SetAttributes(full, FileAttributes.ReadOnly);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            error = "The prepared artifact could not be written into the session workspace.";
            return false;
        }

        string normalized = WorkspacePath.Normalize(sourceRoot);
        workspaceRelativePath = normalized.Length == 0
            ? $"{PreparedSourceRoot}/{fileName}"
            : $"{normalized}/{PreparedSourceRoot}/{fileName}";
        error = string.Empty;
        return true;
    }

    /// <summary>
    /// Writes an admitted extraction and its provenance into a source folder the caller owns, and records
    /// the server-side claim that makes them consumable.
    ///
    /// The pair used to be two independent writes, so an artifact could land and its provenance could
    /// fail, leaving a file in the estate that the normalization phase would later find with nothing
    /// beside it. Ordering alone cannot fix that, because no ordering survives the process stopping
    /// between two writes. What fixes it is that the claim is the whole of the trust decision: a file
    /// with no claim is never read as source, and a claim whose files do not hash to the digests it
    /// records is refused with a reason rather than read.
    ///
    /// So the commit is: take the copy's writer lock, re-establish the caller's authority, both payloads
    /// to temporary names, then the claim, then the renames. A failure after the claim withdraws it, and
    /// a process that dies between the two leaves a claim whose files are missing or short — which the
    /// reader refuses, rather than an artifact that looks admitted.
    ///
    /// <paramref name="revalidate"/> is called while the lock is held and immediately before anything is
    /// recorded. It is where the caller re-checks the things that were true before it went out to the
    /// gateway — the actor's role, the source profile version and hash, and the copy's own identity — so
    /// authority that lapsed while a slow external call was in flight denies the write instead of being
    /// published under. Returning a reason denies the commit and leaves no file and no claim.
    /// </summary>
    public async Task<PreparedCommitResult> CommitPreparedModuleAsync(
        string owner,
        string workspaceId,
        string sourceRoot,
        string artifactFileName,
        byte[] artifact,
        string provenanceFileName,
        byte[] provenance,
        Func<string, string, PreparedSourceClaim> claim,
        Func<CancellationToken, Task<string?>> revalidate,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        ArgumentNullException.ThrowIfNull(provenance);
        ArgumentNullException.ThrowIfNull(claim);
        ArgumentNullException.ThrowIfNull(revalidate);

        if (!IsSafeArtifactName(artifactFileName) || !IsSafeArtifactName(provenanceFileName))
        {
            return PreparedCommitResult.Refused("The prepared artifact name is not a simple file name.");
        }

        string ownerBinding = PreparedSourceOwnerBinding.Derive(owner);
        SemaphoreSlim gate = CommitLock(workspaceId);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (OwnedSourcePath(owner, workspaceId, sourceRoot) is not { } selected ||
                !_workspaces.TryGetValue(workspaceId, out WorkspaceRecord? record))
            {
                return PreparedCommitResult.Refused("That source folder is not part of a copy you own.");
            }

            string folder = Path.Combine(selected, ".fleet-source", "extraction");
            string prefix = Path.GetFullPath(selected) + Path.DirectorySeparatorChar;
            string artifactFull = Path.GetFullPath(Path.Combine(folder, artifactFileName));
            string provenanceFull = Path.GetFullPath(Path.Combine(folder, provenanceFileName));

            if (!artifactFull.StartsWith(prefix, StringComparison.Ordinal) ||
                !provenanceFull.StartsWith(prefix, StringComparison.Ordinal))
            {
                return PreparedCommitResult.Refused("The prepared artifact did not resolve inside the source folder.");
            }

            if (await revalidate(cancellationToken).ConfigureAwait(false) is { } denied)
            {
                return PreparedCommitResult.Refused(denied);
            }

            string stamp = Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
            string artifactTemporary = $"{artifactFull}.{stamp}.part";
            string provenanceTemporary = $"{provenanceFull}.{stamp}.part";

            try
            {
                Directory.CreateDirectory(folder);
                Stage(artifactTemporary, artifact);
                Stage(provenanceTemporary, provenance);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                Discard(artifactTemporary);
                Discard(provenanceTemporary);
                return PreparedCommitResult.Refused("The prepared artifact could not be written into the session workspace.");
            }

            string normalized = WorkspacePath.Normalize(sourceRoot);
            string artifactRelative = Relative(normalized, artifactFileName);
            string provenanceRelative = Relative(normalized, provenanceFileName);

            if (!PreparedSourceTrustStore.TryAppend(
                    record.Path, ownerBinding, claim(artifactRelative, provenanceRelative), out string error))
            {
                Discard(artifactTemporary);
                Discard(provenanceTemporary);
                return PreparedCommitResult.Refused(error);
            }

            try
            {
                Publish(artifactTemporary, artifactFull);
                Publish(provenanceTemporary, provenanceFull);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                Discard(artifactTemporary);
                Discard(provenanceTemporary);
                PreparedSourceTrustStore.TryWithdraw(record.Path, ownerBinding, artifactRelative, out _);
                return PreparedCommitResult.Refused("The prepared artifact could not be written into the session workspace.");
            }

            return new PreparedCommitResult(true, [artifactRelative, provenanceRelative], string.Empty);
        }
        finally
        {
            gate.Release();
        }

        static string Relative(string root, string fileName) =>
            root.Length == 0 ? $"{PreparedSourceRoot}/{fileName}" : $"{root}/{PreparedSourceRoot}/{fileName}";

        static void Stage(string path, byte[] content)
        {
            Discard(path);
            using FileStream stream = File.Create(path);
            stream.Write(content);
            stream.Flush(flushToDisk: true);
        }

        static void Publish(string temporary, string final)
        {
            if (File.Exists(final))
            {
                File.SetAttributes(final, FileAttributes.Normal);
            }

            File.Move(temporary, final, overwrite: true);
            File.SetAttributes(final, FileAttributes.ReadOnly);
        }
    }

    /// <summary>The one writer lock for a session copy, created on first use and freed with the copy.</summary>
    private SemaphoreSlim CommitLock(string workspaceId) =>
        _commitLocks.GetOrAdd(workspaceId, _ => new SemaphoreSlim(1, 1));

    private static void Discard(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.SetAttributes(path, FileAttributes.Normal);
                File.Delete(path);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// Writes an admitted Oracle schema extraction into a source folder the caller owns, and records the
    /// server-side claim that makes the statements consumable.
    ///
    /// Same commit shape as <see cref="CommitPreparedModuleAsync"/> and for the same reason: the copy's
    /// writer lock is taken, the caller's authority is re-established, every payload is staged under a
    /// temporary name, the claim is recorded next, and only then are the files published. The claim is
    /// the whole of the trust decision, so a process that dies mid-commit leaves statements with no
    /// claim — which read as an upload — rather than statements that look admitted.
    ///
    /// The file names are supplied by the caller and validated here; the paths are built from the owned
    /// source folder alone, so nothing a gateway returned can influence where bytes land.
    /// </summary>
    public async Task<PreparedCommitResult> CommitPreparedSchemaAsync(
        string owner,
        string workspaceId,
        string sourceRoot,
        IReadOnlyList<(string FileName, byte[] Content)> files,
        Func<IReadOnlyList<string>, PreparedSchemaClaim> claim,
        Func<CancellationToken, Task<string?>> revalidate,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(claim);
        ArgumentNullException.ThrowIfNull(revalidate);

        if (files.Count is 0 ||
            files.Any(file => !IsSafeArtifactName(file.FileName) || file.Content is not { Length: > 0 }) ||
            files.Select(file => file.FileName).Distinct(StringComparer.OrdinalIgnoreCase).Count() != files.Count)
        {
            return PreparedCommitResult.Refused(
                "The prepared schema file names are not distinct simple file names, or a payload was empty.");
        }

        string ownerBinding = PreparedSourceOwnerBinding.Derive(owner);
        SemaphoreSlim gate = CommitLock(workspaceId);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (OwnedSourcePath(owner, workspaceId, sourceRoot) is not { } selected ||
                !_workspaces.TryGetValue(workspaceId, out WorkspaceRecord? record))
            {
                return PreparedCommitResult.Refused("That source folder is not part of a copy you own.");
            }

            string folder = Path.Combine(selected, ".fleet-source", "extraction");
            string prefix = Path.GetFullPath(selected) + Path.DirectorySeparatorChar;
            string[] finals = [.. files.Select(file => Path.GetFullPath(Path.Combine(folder, file.FileName)))];

            if (finals.Any(full => !full.StartsWith(prefix, StringComparison.Ordinal)))
            {
                return PreparedCommitResult.Refused("The prepared schema did not resolve inside the source folder.");
            }

            if (await revalidate(cancellationToken).ConfigureAwait(false) is { } denied)
            {
                return PreparedCommitResult.Refused(denied);
            }

            string stamp = Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
            string[] staged = [.. finals.Select(full => $"{full}.{stamp}.part")];

            try
            {
                Directory.CreateDirectory(folder);
                for (int index = 0; index < files.Count; index++)
                {
                    Discard(staged[index]);
                    using FileStream stream = File.Create(staged[index]);
                    stream.Write(files[index].Content);
                    stream.Flush(flushToDisk: true);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                foreach (string path in staged) Discard(path);
                return PreparedCommitResult.Refused("The prepared schema could not be written into the session workspace.");
            }

            string normalized = WorkspacePath.Normalize(sourceRoot);
            string[] relatives =
            [
                .. files.Select(file => normalized.Length == 0
                    ? $"{PreparedSourceRoot}/{file.FileName}"
                    : $"{normalized}/{PreparedSourceRoot}/{file.FileName}"),
            ];

            PreparedSchemaClaim recorded = claim(relatives);
            if (!PreparedSchemaTrustStore.TryAppend(record.Path, ownerBinding, recorded, out string error))
            {
                foreach (string path in staged) Discard(path);
                return PreparedCommitResult.Refused(error);
            }

            try
            {
                for (int index = 0; index < finals.Length; index++)
                {
                    if (File.Exists(finals[index]))
                    {
                        File.SetAttributes(finals[index], FileAttributes.Normal);
                    }

                    File.Move(staged[index], finals[index], overwrite: true);
                    File.SetAttributes(finals[index], FileAttributes.ReadOnly);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                foreach (string path in staged) Discard(path);
                PreparedSchemaTrustStore.TryWithdraw(record.Path, ownerBinding, recorded.SchemaDdlPath, out _);
                return PreparedCommitResult.Refused("The prepared schema could not be written into the session workspace.");
            }

            return new PreparedCommitResult(true, relatives, string.Empty);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Re-reads a workspace after the fleet wrote into it and republishes its inventory and snapshot
    /// digest.
    ///
    /// The digest deliberately changes. An authorization bound to the pre-preparation source no longer
    /// matches, which is correct: the estate a run would now read is not the estate that was approved.
    /// </summary>
    public SourceWorkspaceFacts? Reindex(string owner, string workspaceId)
    {
        if (!_workspaces.TryGetValue(workspaceId, out WorkspaceRecord? record) ||
            !string.Equals(record.Owner, owner, StringComparison.Ordinal))
        {
            return null;
        }

        SourceInventory inventory;
        try
        {
            inventory = SourceInventory.Build(record.Path, MaxFiles, MaxBytes);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        WorkspaceRecord refreshed = record with
        {
            Summary = record.Summary with
            {
                FileCount = inventory.FileCount,
                ByteCount = inventory.ByteCount,
                Artifacts = inventory.Artifacts,
            },
            SnapshotHash = SnapshotHash(record.Path),
        };

        _workspaces[workspaceId] = refreshed;
        return new SourceWorkspaceFacts(refreshed.Summary, refreshed.SnapshotHash);
    }

    private static bool IsSafeArtifactName(string? fileName) =>
        fileName is { Length: > 0 and <= 128 } &&
        char.IsAsciiLetterOrDigit(fileName[0]) &&
        fileName.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-') &&
        !fileName.Contains("..", StringComparison.Ordinal);

    public bool Release(string owner, string workspaceId)
    {
        if (!_workspaces.TryGetValue(workspaceId, out WorkspaceRecord? record) ||
            !string.Equals(record.Owner, owner, StringComparison.Ordinal) ||
            _retained.ContainsKey(workspaceId))
        {
            return false;
        }

        _workspaces.TryRemove(workspaceId, out _);
        ForgetCommitLock(workspaceId);
        DeleteDirectory(record.Path);
        return true;
    }

    /// <summary>Drops a released copy's writer lock. A commit in flight keeps its own reference.</summary>
    private void ForgetCommitLock(string workspaceId)
    {
        if (_commitLocks.TryRemove(workspaceId, out SemaphoreSlim? gate))
        {
            gate.Dispose();
        }
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
            yield return new SourceProgress("error", error, AcquisitionFailed(error));
            yield break;
        }

        if (branch is not null && !IsSafeRefName(branch))
        {
            const string BranchRejected = "That branch name contains characters that are not allowed.";
            yield return new SourceProgress("error", BranchRejected, AcquisitionFailed(BranchRejected));
            yield break;
        }

        if (!TryReserveDisk())
        {
            const string NoRoom = "The workbench is holding too many source copies right now. Delete one and try again.";
            yield return new SourceProgress("error", NoRoom, AcquisitionFailed(NoRoom));
            yield break;
        }

        string workspaceId = NewWorkspaceId();
        string path = Path.Combine(_root, workspaceId);
        Directory.CreateDirectory(path);

        yield return new SourceProgress(
            "info",
            $"Workspace {workspaceId} created for this session.",
            Acquiring(ProgressActions.WorkspaceCreated, "A private session folder was created.", "Connecting to the repository host."));
        yield return new SourceProgress(
            "info",
            $"Connecting to {repository!.Host}...",
            Acquiring(ProgressActions.RepositoryClone, $"Copying from {repository.Host}.", "Indexing the copied files once the copy finishes."));

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
            const string CloneFailed = "Clone failed. Private repositories are not supported yet; export a zip instead.";
            yield return new SourceProgress("error", CloneFailed, AcquisitionFailed(CloneFailed));
            yield break;
        }

        // The .git directory is only needed to fetch. Dropping it removes any chance of a later
        // command pushing back to the customer's remote.
        DeleteDirectory(Path.Combine(path, ".git"));
        yield return new SourceProgress(
            "info",
            "Remote metadata removed. This copy cannot push back to your repository.",
            Acquiring(
                ProgressActions.RemoteDetached,
                "Git remote metadata was deleted, so the copy cannot push back to your repository.",
                "Indexing the copied files."));

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
            const string NoRoom = "The workbench is holding too many source copies right now. Delete one and try again.";
            yield return new SourceProgress("error", NoRoom, AcquisitionFailed(NoRoom));
            yield break;
        }

        string workspaceId = NewWorkspaceId();
        string path = Path.Combine(_root, workspaceId);
        Directory.CreateDirectory(path);
        string fullRoot = Path.GetFullPath(path) + Path.DirectorySeparatorChar;

        yield return new SourceProgress(
            "info",
            $"Workspace {workspaceId} created for this session.",
            Acquiring(ProgressActions.WorkspaceCreated, "A private session folder was created.", "Opening the archive."));
        yield return new SourceProgress(
            "info",
            $"Opening {Sanitize(fileName)}...",
            Acquiring(ProgressActions.ArchiveExtract, $"Expanding {Sanitize(fileName)}.", "Indexing the expanded files once extraction finishes."));

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
            yield return new SourceProgress("error", failure, AcquisitionFailed(failure));
            yield break;
        }

        yield return new SourceProgress(
            "info",
            $"Expanded {files} files.",
            Acquiring(ProgressActions.ArchiveExtract, $"{files} files were expanded from the archive.", "Indexing the expanded files.")
                with
            { ArtifactKind = "ExtractedFile", ArtifactCount = files });

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
        yield return new SourceProgress(
            "info",
            "Indexing files...",
            Acquiring(ProgressActions.Index, "Reading the copied file names to recognise Oracle artifacts.", "Reporting what was recognised."));

        // This directory is owned by the workbench. A clone or archive may contain a path with the
        // same name, but supplied content must never be mistaken for compiler-accepted run state.
        DeleteDirectory(Path.Combine(path, WorkbenchExecution.OutputRoot));

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
            const string Unreadable = "The copied files could not be read.";
            yield return new SourceProgress("error", Unreadable, AcquisitionFailed(Unreadable));
            yield break;
        }

        if (inventory.Truncated)
        {
            yield return new SourceProgress(
                "warn",
                "This source is very large, so only part of it was indexed.",
                Acquiring(
                    ProgressActions.Index,
                    "The source exceeded the indexing limits, so only part of it was read.",
                    "Treat the recognised list as incomplete when you fill in the source checklist."));
        }

        foreach (SourceArtifact artifact in inventory.Artifacts)
        {
            yield return new SourceProgress(
                "found",
                $"{artifact.Count} x {artifact.Kind} ({artifact.Example})",
                Acquiring(
                    ProgressActions.ArtifactCounted,
                    $"{artifact.Count} {artifact.Kind} file(s) recognised by name, for example {artifact.Example}.",
                    "Matching source-checklist items will be ticked for you when the copy finishes.")
                    with
                { ArtifactKind = artifact.Kind, ArtifactCount = artifact.Count });
        }

        if (inventory.Artifacts.Count == 0)
        {
            yield return new SourceProgress(
                "warn",
                "No Oracle Forms or PL/SQL artifacts were recognised.",
                Acquiring(
                    ProgressActions.Index,
                    "No Oracle Forms or PL/SQL artifacts were recognised by file name.",
                    "Fill in the source checklist yourself; nothing was ticked for you."));
        }

        MarkReadOnly(path);
        yield return new SourceProgress(
            "info",
            "Copy locked read-only. The workbench cannot modify it.",
            Acquiring(ProgressActions.LockedReadOnly, "The copy was locked read-only.", "Finishing the copy."));

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

        _workspaces[workspaceId] = new WorkspaceRecord(owner, path, summary, SnapshotHash(path));
        yield return new SourceProgress(
            "done",
            workspaceId,
            new ProgressSignal(
                ProgressOperations.SourceAcquisition,
                ProgressActions.SourceReady,
                ProgressState.Completed,
                AcquisitionPurpose,
                $"{inventory.FileCount} file(s) copied and locked read-only; {inventory.Artifacts.Count} Oracle artifact kind(s) recognised.",
                "Continue the setup. The copy is deleted automatically four hours from now.",
                ArtifactKind: "RecognisedArtifactKind",
                ArtifactCount: inventory.Artifacts.Count));
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

    /// <summary>
    /// Deterministic identity of the acquired bytes, over sorted relative paths plus each file's length
    /// and content. Two acquisitions of the same tree agree; any changed, added, renamed, or removed file
    /// changes the value.
    ///
    /// The digest is one-way and never leaves the server, so it identifies the source without disclosing
    /// anything about it. <see cref="WorkbenchExecution.OutputRoot"/> is excluded because it is the
    /// workbench's own output area: a run writing into it must not change the identity of what it read.
    /// Enumeration stops at the same intake limits acquisition used, and a tree that exceeds them hashes
    /// to a value that cannot match any bounded read, so an oversized source can never be authorized.
    /// </summary>
    private static string SnapshotHash(string path) => SnapshotRead(path, static _ => false, 0).SnapshotHash;

    /// <summary>Everything this server wrote into a selected source folder, as a relative path prefix.</summary>
    private const string PreparedSourceExclusion = ".fleet-source/";

    /// <summary>
    /// The one pass that computes a snapshot digest, optionally keeping the bytes of the files
    /// <paramref name="retain"/> selects. A kept file is hashed out of the buffer that is handed back, so a
    /// caller that trusts the digest is holding the bytes the digest was taken over.
    /// </summary>
    private static TrustedSourceRead SnapshotRead(
        string path,
        Func<string, bool> retain,
        long maxRetainedBytes,
        string? alsoExcluded = null)
    {
        string prefix = Path.GetFullPath(path) + Path.DirectorySeparatorChar;
        string excluded = WorkbenchExecution.OutputRoot + "/";

        List<string> relativePaths;
        try
        {
            relativePaths =
            [.. Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
                .Select(file => Path.GetFullPath(file))
                .Where(file => file.StartsWith(prefix, StringComparison.Ordinal))
                .Select(file => file[prefix.Length..].Replace('\\', '/'))
                .Where(relative => !relative.StartsWith(excluded, StringComparison.Ordinal))
                .Where(relative => alsoExcluded is null || !relative.StartsWith(alsoExcluded, StringComparison.Ordinal))
                .Take(MaxFiles + 1)];
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return s_unreadableSource;
        }

        if (relativePaths.Count > MaxFiles)
        {
            return s_unreadableSource;
        }

        relativePaths.Sort(StringComparer.Ordinal);

        using IncrementalHash digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[64 * 1024];
        Span<byte> length = stackalloc byte[sizeof(long)];
        List<TrustedSourceFile> kept = [];
        long total = 0;
        long retained = 0;

        foreach (string relative in relativePaths)
        {
            FileInfo info = new(Path.Combine(path, relative.Replace('/', Path.DirectorySeparatorChar)));
            total += info.Length;
            if (total > MaxBytes)
            {
                return s_unreadableSource;
            }

            digest.AppendData(Encoding.UTF8.GetBytes(relative));
            BinaryPrimitives.WriteInt64BigEndian(length, info.Length);
            digest.AppendData(length);

            bool keep = retain(relative);
            if (keep)
            {
                retained += info.Length;
                if (retained > maxRetainedBytes)
                {
                    return s_unreadableSource;
                }
            }

            try
            {
                if (keep)
                {
                    byte[] content = File.ReadAllBytes(info.FullName);
                    digest.AppendData(content);
                    kept.Add(new TrustedSourceFile(relative, content));
                    continue;
                }

                using FileStream stream = info.OpenRead();
                int read;
                while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    digest.AppendData(buffer, 0, read);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return s_unreadableSource;
            }
        }

        return new TrustedSourceRead(Convert.ToHexString(digest.GetHashAndReset()).ToLowerInvariant(), kept);
    }

    /// <summary>
    /// Stands in for a snapshot that could not be read within the intake limits. It is not a digest, so
    /// it matches nothing an authorization could ever have been issued against.
    /// </summary>
    public const string UnhashableSource = "unhashable";

    /// <summary>A pass that could not complete. It carries no file, so nothing is parsed out of a refused read.</summary>
    private static readonly TrustedSourceRead s_unreadableSource = new(UnhashableSource, []);

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
            if (entry.Value.Summary.ExpiresUtc <= now &&
                !_retained.ContainsKey(entry.Key) &&
                _workspaces.TryRemove(entry.Key, out WorkspaceRecord? removed))
            {
                ForgetCommitLock(entry.Key);
                DeleteDirectory(removed.Path);
            }
        }
    }

    public void Dispose()
    {
        _sweeper.Dispose();
        foreach (KeyValuePair<string, WorkspaceRecord> entry in _workspaces)
        {
            if (!_retained.ContainsKey(entry.Key))
            {
                DeleteDirectory(entry.Value.Path);
            }
        }

        _workspaces.Clear();
        _retained.Clear();

        foreach (KeyValuePair<string, SemaphoreSlim> gate in _commitLocks)
        {
            gate.Value.Dispose();
        }

        _commitLocks.Clear();
    }

    private sealed class Retention(Action release) : IDisposable
    {
        private Action? _release = release;

        public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
    }
}
