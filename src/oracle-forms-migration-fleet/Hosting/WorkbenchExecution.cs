// Copyright (c) Microsoft. All rights reserved.

using System.Text;
using OracleFormsMigrationFleet.Fleet;
using OracleFormsMigrationFleet.Fleet.Execution;

namespace OracleFormsMigrationFleet.Hosting;

/// <summary>One artifact a run wrote, addressed by its workspace-relative path.</summary>
public sealed record WorkbenchArtifactView(string Path, string Kind, string Description, bool Previewable);

public sealed record WorkbenchPhaseView(
    string Phase,
    string PlannedStatus,
    string State,
    string? Detail,
    IReadOnlyList<WorkbenchArtifactView> Artifacts,
    IReadOnlyList<string> Findings);

public sealed record WorkbenchAttestationView(
    string Kind,
    bool Succeeded,
    string Summary,
    IReadOnlyList<string> Artifacts);

public sealed record WorkbenchExecutionView(
    string RequestedMode,
    string AuthorizedMode,
    string OutputRoot,
    IReadOnlyList<WorkbenchPhaseView> Phases,
    IReadOnlyList<WorkbenchArtifactView> Artifacts,
    IReadOnlyList<WorkbenchAttestationView> Attestations,
    IReadOnlyList<string> Blockers);

/// <summary>
/// Authorization and path safety for the execution endpoints, kept separate from the HTTP plumbing so
/// the rules are testable on their own.
///
/// A caller supplies a workspace identifier, never a path. The identifier is resolved against the
/// signed-in owner, and everything a run writes is confined to <see cref="OutputRoot"/> inside that
/// workspace, so the read-only source copy is never a write target.
/// </summary>
public static class WorkbenchExecution
{
    /// <summary>Session-private directory every generated artifact is written into.</summary>
    public const string OutputRoot = ".fleet-run";

    public const long MaxPreviewBytes = 512L * 1024;

    private const string UnknownWorkspace = "No source workspace with that identifier is available for this session.";

    private static readonly HashSet<string> s_previewable = new(StringComparer.OrdinalIgnoreCase)
    {
        ".md", ".json", ".sql", ".txt",
    };

    public static bool IsPreviewable(string? path) =>
        !string.IsNullOrEmpty(path) && s_previewable.Contains(System.IO.Path.GetExtension(path));

    /// <summary>
    /// Resolves an owned workspace and rewrites the request so generated artifacts land under
    /// <see cref="OutputRoot"/>. Returns false with the status the endpoint should answer with.
    /// </summary>
    public static bool TryPrepare(
        SourceWorkspaceService workspaces,
        string owner,
        string? workspaceId,
        MigrationRunRequest? request,
        out string workspaceRoot,
        out MigrationRunRequest? prepared,
        out int status,
        out string error)
    {
        ArgumentNullException.ThrowIfNull(workspaces);

        workspaceRoot = string.Empty;
        prepared = null;

        if (string.IsNullOrWhiteSpace(workspaceId) ||
            workspaces.ResolveRoot(owner, workspaceId) is not string root)
        {
            status = 404;
            error = UnknownWorkspace;
            return false;
        }

        if (request is null)
        {
            status = 400;
            error = "The run request could not be read.";
            return false;
        }

        if (WorkspacePath.Validate(request.SourceRoot, "Source folder") is string sourceError)
        {
            status = 400;
            error = sourceError;
            return false;
        }

        if (WorkspacePath.Validate(request.OutputRoot, "Output folder") is string outputError)
        {
            status = 400;
            error = outputError;
            return false;
        }

        string requested = WorkspacePath.Normalize(request.OutputRoot);
        workspaceRoot = root;
        prepared = request with
        {
            OutputRoot = requested is "" or "." ? OutputRoot : $"{OutputRoot}/{requested}",
        };

        status = 200;
        error = string.Empty;
        return true;
    }

    /// <summary>Drops the previous run's output so a run never analyses or reports its own earlier artifacts.</summary>
    public static void ResetOutput(string workspaceRoot)
    {
        string path = System.IO.Path.Combine(workspaceRoot, OutputRoot);
        if (!Directory.Exists(path))
        {
            return;
        }

        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The next write recreates what it needs; a stale file is not worth failing the run over.
        }
    }

    /// <summary>
    /// Resolves a preview request to an absolute path. The path must be workspace-relative, must sit
    /// inside <see cref="OutputRoot"/>, and must carry a text extension this console is willing to serve.
    /// </summary>
    public static bool TryResolveArtifact(
        SourceWorkspaceService workspaces,
        string owner,
        string? workspaceId,
        string? path,
        out string absolutePath,
        out int status,
        out string error)
    {
        ArgumentNullException.ThrowIfNull(workspaces);

        absolutePath = string.Empty;

        if (string.IsNullOrWhiteSpace(workspaceId) ||
            workspaces.ResolveRoot(owner, workspaceId) is not string root)
        {
            status = 404;
            error = UnknownWorkspace;
            return false;
        }

        string normalized = WorkspacePath.Normalize(path ?? string.Empty);
        bool inOutput = normalized == OutputRoot || normalized.StartsWith(OutputRoot + "/", StringComparison.Ordinal);

        if (WorkspacePath.Validate(path, "Artifact path") is not null || !inOutput)
        {
            status = 400;
            error = "Only artifacts this workbench generated in your session workspace can be previewed.";
            return false;
        }

        if (!IsPreviewable(normalized))
        {
            status = 415;
            error = "Only .md, .json, .sql, and .txt artifacts can be previewed.";
            return false;
        }

        WorkspaceWriter workspace = new(root);
        if (!workspace.TryResolve(normalized, out string resolved, out _) || !File.Exists(resolved))
        {
            status = 404;
            error = "That artifact is not in your session workspace.";
            return false;
        }

        absolutePath = resolved;
        status = 200;
        error = string.Empty;
        return true;
    }

    /// <summary>Reads at most <see cref="MaxPreviewBytes"/> so one artifact cannot dominate a response.</summary>
    public static string ReadPreview(string absolutePath, out bool truncated)
    {
        using FileStream stream = File.OpenRead(absolutePath);
        truncated = stream.Length > MaxPreviewBytes;

        byte[] buffer = new byte[(int)Math.Min(MaxPreviewBytes, stream.Length)];
        stream.ReadExactly(buffer);

        string text = Encoding.UTF8.GetString(buffer);
        return text.Length > 0 && text[0] == '\uFEFF' ? text[1..] : text;
    }

    /// <summary>
    /// Shapes a run for the browser. Enum values become names, and only workspace-relative paths cross
    /// the boundary: no absolute path, credential, or exception detail appears here.
    /// </summary>
    public static WorkbenchExecutionView Project(MigrationExecutionResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        return new WorkbenchExecutionView(
            result.Plan.RequestedMode.ToString(),
            result.Plan.AuthorizedMode.ToString(),
            OutputRoot,
            [.. result.Phases.Select(phase => new WorkbenchPhaseView(
                phase.Phase.ToString(),
                phase.PlannedStatus.ToString(),
                phase.State.ToString(),
                phase.Detail,
                [.. phase.Artifacts.Select(Artifact)],
                phase.Findings))],
            [.. result.Artifacts.Select(Artifact)],
            [.. result.Attestations.Select(attestation => new WorkbenchAttestationView(
                attestation.Kind.ToString(),
                attestation.Succeeded,
                attestation.Summary,
                [.. attestation.Artifacts.Select(artifact => artifact.Path)]))],
            result.Plan.Blockers);
    }

    private static WorkbenchArtifactView Artifact(ArtifactReference artifact) =>
        new(artifact.Path, artifact.Kind.ToString(), artifact.Description, IsPreviewable(artifact.Path));
}
