// Copyright (c) Microsoft. All rights reserved.

using System.IO.Compression;
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
    private const string AcceptedRepairsFile = "program-unit-repairs.sql";

    private static readonly HashSet<string> s_previewable = new(StringComparer.OrdinalIgnoreCase)
    {
        ".md", ".json", ".sql", ".txt",
    };

    public static bool IsPreviewable(string? path) =>
        !string.IsNullOrEmpty(path)
        && !path.EndsWith("/forms-ir.json", StringComparison.OrdinalIgnoreCase)
        && s_previewable.Contains(System.IO.Path.GetExtension(path));

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

        if (WorkspacePath.IsWithin(OutputRoot, request.SourceRoot))
        {
            status = 400;
            error = "The source folder cannot be the workbench output directory.";
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

    /// <summary>
    /// Everything the execute endpoint needs after the server has discarded what it cannot verify.
    /// <see cref="MutationAuthorizer"/> is fail-closed and is re-asked before each mutating phase.
    /// </summary>
    public sealed record WorkbenchRunPreparation(
        string WorkspaceRoot,
        MigrationRunRequest Request,
        WorkbenchMutationAuthorizer MutationAuthorizer,
        string SourceSnapshotHash,
        string PlanInputHash,
        string TargetHash);

    /// <summary>
    /// The project and target profile the server resolved for this run, or null when the caller is
    /// running outside a persisted project. Every field is server-owned: a caller names a project and a
    /// profile by identifier and the server looks up the rest.
    /// </summary>
    public sealed record WorkbenchRunBinding(
        string ProjectId,
        string TargetProfileId,
        int TargetProfileVersion,
        string TargetProfileHash,
        string WorkspaceOwnerId);

    /// <summary>Outcome of preparing a run, shaped so the endpoint can answer without out-parameters.</summary>
    public sealed record WorkbenchRunPreparationResult(
        bool Succeeded,
        WorkbenchRunPreparation? Preparation,
        int Status,
        string Error);

    public sealed record WorkbenchTrustedPreparation(
        string WorkspaceRoot,
        WorkbenchRequestPreparation Trusted);

    public static bool TryPrepareTrusted(
        SourceWorkspaceService workspaces,
        string workspaceOwner,
        string? workspaceId,
        MigrationRunRequest? request,
        out WorkbenchTrustedPreparation? preparation,
        out int status,
        out string error)
    {
        preparation = null;
        if (!TryPrepare(
            workspaces,
            workspaceOwner,
            workspaceId,
            request,
            out string root,
            out MigrationRunRequest? routed,
            out status,
            out error))
        {
            return false;
        }

        SourceWorkspaceFacts? facts = workspaces.Describe(workspaceOwner, workspaceId!, routed!.SourceRoot);
        if (facts is null)
        {
            status = 404;
            error = "The selected source folder is not available in this workspace.";
            return false;
        }

        preparation = new WorkbenchTrustedPreparation(root, WorkbenchTrustBoundary.Prepare(routed, facts));
        return true;
    }

    /// <summary>
    /// The exact entry point the execute endpoint uses.
    ///
    /// Path safety and ownership are settled first, then every claim of authority in the body is
    /// discarded and re-derived from the owner-resolved source copy. The returned authorizer is bound to
    /// the authenticated actor and to this run's tenant, project, source, input, and target profile, so a
    /// grant issued for some other run cannot cover it.
    ///
    /// When a persisted grant does cover the run, the internal execution approval is materialized here
    /// and names the actor who actually approved it in the store. That is the only path by which
    /// <see cref="MigrationRunRequest.ExecutionApproval"/> becomes anything other than pending.
    /// </summary>
    public static async Task<WorkbenchRunPreparationResult> PrepareRunAsync(
        SourceWorkspaceService workspaces,
        WorkbenchActor actor,
        string? workspaceId,
        MigrationRunRequest? request,
        WorkbenchAuthorizationService authorization,
        WorkbenchRunBinding? binding = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workspaces);
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(authorization);

        string workspaceOwner = binding?.WorkspaceOwnerId ?? actor.OwnerId;
        if (!TryPrepareTrusted(
            workspaces,
            workspaceOwner,
            workspaceId,
            request,
            out WorkbenchTrustedPreparation? preparation,
            out int status,
            out string error))
        {
            return new WorkbenchRunPreparationResult(false, null, status, error);
        }

        WorkbenchRequestPreparation trusted = preparation!.Trusted;

        // A persisted target profile names the target, so the binding hash replaces the stack-enum one:
        // two engagements pointed at different databases must not share a target identity because they
        // both chose PostgreSQL.
        string targetHash = binding?.TargetProfileHash ?? trusted.TargetHash;

        WorkbenchMutationAuthorizer authorizer = new(
            authorization,
            actor,
            trusted.SourceSnapshotHash,
            WorkbenchTrustBoundary.PlanInputHash(trusted.Request),
            targetHash,
            clock: null,
            projectId: binding?.ProjectId ?? string.Empty,
            targetProfileId: binding?.TargetProfileId ?? string.Empty,
            targetProfileVersion: binding?.TargetProfileVersion ?? 0);

        MigrationRunRequest prepared = trusted.Request;

        WorkbenchAuthorizationDecision sandbox = await authorization.AuthorizeAsync(
            new WorkbenchAuthorizationQuery(
                actor,
                prepared.EngagementId,
                trusted.SourceSnapshotHash,
                WorkbenchTrustBoundary.PlanInputHash(prepared),
                targetHash,
                WorkbenchMutationScope.SandboxDatabaseWrite,
                actor.TenantId,
                binding?.ProjectId ?? string.Empty,
                binding?.TargetProfileId ?? string.Empty,
                binding?.TargetProfileVersion ?? 0),
            DateTimeOffset.UtcNow,
            cancellationToken).ConfigureAwait(false);

        if (sandbox.IsAuthorized)
        {
            prepared = prepared with
            {
                ExecutionApproval = new HumanApproval
                {
                    Decision = ApprovalDecision.Approved,
                    ApproverId = sandbox.ApprovedByObjectId.Length == 0 ? sandbox.AuthorizationId : sandbox.ApprovedByObjectId,
                    Notes = $"Materialized from persisted authorization {sandbox.AuthorizationId}.",
                },
            };
        }

        return new WorkbenchRunPreparationResult(
            true,
            new WorkbenchRunPreparation(
                preparation.WorkspaceRoot,
                prepared,
                authorizer,
                trusted.SourceSnapshotHash,
                WorkbenchTrustBoundary.PlanInputHash(trusted.Request),
                targetHash),
            200,
            string.Empty);
    }

    /// <summary>
    /// The exact entry point the plan endpoint uses.
    ///
    /// Planning writes nothing, so it is available with or without an acquired source. What it must not
    /// do is repeat a caller's own claims back as findings, so verified evidence comes only from a copy
    /// this owner acquired and this server indexed. A caller working from a manually described estate
    /// gets a plan in which nothing is verified, which is the truthful result.
    /// </summary>
    public static bool TryPlanRun(
        SourceWorkspaceService? workspaces,
        WorkbenchActor actor,
        string? workspaceId,
        MigrationRunRequest? request,
        out WorkbenchPlanResponse? response,
        out int status,
        out string error,
        string? workspaceOwner = null)
    {
        ArgumentNullException.ThrowIfNull(actor);

        response = null;
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

        if (WorkspacePath.IsWithin(OutputRoot, request.SourceRoot))
        {
            status = 400;
            error = "The source folder cannot be the workbench output directory.";
            return false;
        }

        string owner = workspaceOwner ?? actor.OwnerId;
        SourceWorkspaceSummary? summary = workspaces is not null && !string.IsNullOrWhiteSpace(workspaceId)
            ? workspaces.DescribeSummary(owner, workspaceId, request.SourceRoot)
            : null;

        if (!string.IsNullOrWhiteSpace(workspaceId) && summary is null)
        {
            status = 404;
            error = workspaces?.Describe(owner, workspaceId) is null
                ? UnknownWorkspace
                : "The selected source folder is not available in this workspace.";
            return false;
        }

        MigrationRunRequest sanitized = WorkbenchTrustBoundary.PreparePlan(request, summary);
        MigrationRunPlan plan = MigrationRunPlanner.Plan(sanitized);

        response = new WorkbenchPlanResponse(
            plan,
            MigrationWorkbenchCatalog.Project(plan),
            MigrationWorkbenchCatalog.ExecutionBoundary,
            AzureFootprintCalculator.Describe(plan.Target.Database, plan.AuthorizedMode));

        status = 200;
        error = string.Empty;
        return true;
    }

    /// <summary>Drops the previous run's output so a run never analyses or reports its own earlier artifacts.</summary>
    public static void ResetOutput(string workspaceRoot)
        => ResetOutput(workspaceRoot, OutputRoot);

    public static void ResetOutput(string workspaceRoot, string outputRoot)
    {
        string normalized = WorkspacePath.Normalize(outputRoot);
        if (!WorkspacePath.IsWithin(OutputRoot, normalized))
        {
            throw new InvalidOperationException("A run output root must stay inside the workbench output directory.");
        }

        string path = System.IO.Path.Combine(workspaceRoot, normalized);
        if (!Directory.Exists(path))
        {
            return;
        }

        try
        {
            List<(string RelativePath, byte[] Content)> acceptedRepairs =
            [.. Directory.EnumerateFiles(path, AcceptedRepairsFile, SearchOption.AllDirectories)
                .Where(file => new FileInfo(file).Length <= MaxPreviewBytes)
                .Select(file => (System.IO.Path.GetRelativePath(path, file), File.ReadAllBytes(file)))];

            Directory.Delete(path, recursive: true);

            foreach ((string relativePath, byte[] content) in acceptedRepairs)
            {
                string restored = System.IO.Path.Combine(path, relativePath);
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(restored)!);
                File.WriteAllBytes(restored, content);
            }
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

        return TryResolveArtifact(root, path, out absolutePath, out status, out error);
    }

    internal static bool TryResolveArtifact(
        string workspaceRoot,
        string? path,
        out string absolutePath,
        out int status,
        out string error)
    {
        absolutePath = string.Empty;

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

        WorkspaceWriter workspace = new(workspaceRoot);
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
    /// Resolves the directory a run wrote so it can be exported. Only <see cref="OutputRoot"/> is
    /// exportable: the acquired source copy is the customer's code and must never leave in an export.
    /// </summary>
    public static bool TryResolveExport(
        SourceWorkspaceService workspaces,
        string owner,
        string? workspaceId,
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

        string resolved = System.IO.Path.Combine(root, OutputRoot);
        if (!Directory.Exists(resolved) || !Directory.EnumerateFiles(resolved, "*", SearchOption.AllDirectories).Any())
        {
            status = 404;
            error = "This session has no generated artifacts to export. Run the authorized phases first.";
            return false;
        }

        absolutePath = resolved;
        status = 200;
        error = string.Empty;
        return true;
    }

    /// <summary>
    /// Writes the run output as a zip. Entry names stay relative to the run root so an archive cannot
    /// carry an absolute path or a traversal segment to whoever opens it.
    /// </summary>
    public static void WriteExport(string runRoot, Stream destination)
    {
        using ZipArchive archive = new(destination, ZipArchiveMode.Create, leaveOpen: true);

        foreach (string file in Directory.EnumerateFiles(runRoot, "*", SearchOption.AllDirectories))
        {
            string entryName = System.IO.Path.GetRelativePath(runRoot, file).Replace('\\', '/');
            if (entryName.StartsWith("..", StringComparison.Ordinal) || System.IO.Path.IsPathRooted(entryName))
            {
                continue;
            }

            archive.CreateEntryFromFile(file, entryName, CompressionLevel.Optimal);
        }
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
