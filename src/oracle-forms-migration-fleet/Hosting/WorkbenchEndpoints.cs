// Copyright (c) Microsoft. All rights reserved.

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OracleFormsMigrationFleet.Fleet;
using OracleFormsMigrationFleet.Fleet.Execution;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;

namespace OracleFormsMigrationFleet.Hosting;

/// <summary>
/// Operator console endpoints. They serve the static workbench from wwwroot and expose the
/// deterministic catalog and the real <see cref="MigrationRunPlanner"/>. No phase outcome is
/// simulated here and no secret value is ever returned.
/// </summary>
internal static class WorkbenchEndpoints
{
    public static void Map(
        IEndpointRouteBuilder endpoints,
        bool modelConfigured,
        FoundryAgentClient? foundryAgentClient = null,
        bool managedIdentityConfigured = false,
        IWorkbenchIdentityProvider? identityProvider = null,
        bool sandboxDatabaseConfigured = false,
        SourceWorkspaceService? sourceWorkspaces = null)
    {
        IHostEnvironment environment = endpoints.ServiceProvider.GetRequiredService<IHostEnvironment>();
        string webRoot = Path.Combine(environment.ContentRootPath, "wwwroot");

        // A host that reached here without naming a mode has no identity boundary, so it gets the one
        // that authenticates nobody rather than the one that authenticates everybody.
        IWorkbenchIdentityProvider identity = identityProvider ?? new DevelopmentIdentityProvider();
        bool entraAuthenticationConfigured = identity.Mode == WorkbenchAuthenticationMode.ContainerApps;

        endpoints.MapGet("/", (HttpContext context) => ServeAsset(context, webRoot, "index.html", "text/html; charset=utf-8"));
        endpoints.MapGet("/styles.css", (HttpContext context) => ServeAsset(context, webRoot, "styles.css", "text/css; charset=utf-8"));
        endpoints.MapGet("/app.js", (HttpContext context) => ServeAsset(context, webRoot, "app.js", "text/javascript; charset=utf-8"));

        endpoints.MapGet("/api/workbench/bootstrap", (HttpContext context) =>
        {
            if (!TryActor(context, identity, out _))
            {
                return Results.Unauthorized();
            }

            // Reported from what is actually registered, so the console cannot claim a capability the process lacks.
            FleetAttribution attribution = FleetAttributionMap.Describe(
                [.. MigrationExecutor.DefaultAdapters(context.RequestServices.GetService<IArtifactReviewer>()).Select(adapter => adapter.Phase)],
                Environment.GetEnvironmentVariable("AZURE_AI_MODEL_DEPLOYMENT_NAME"),
                context.RequestServices.GetService<IArtifactReviewer>() is null
                    ? null
                    : Environment.GetEnvironmentVariable("AZURE_AI_REVIEW_MODEL_DEPLOYMENT_NAME")
                      ?? Environment.GetEnvironmentVariable("AZURE_AI_MODEL_DEPLOYMENT_NAME"));

            return Results.Ok(MigrationWorkbenchCatalog.Bootstrap(
                ApplicationInsightsConfigured(),
                modelConfigured,
                foundryAgentClient is not null,
                managedIdentityConfigured,
                entraAuthenticationConfigured,
                sandboxDatabaseConfigured,
                attribution));
        });

        endpoints.MapPost("/api/workbench/plan", async (HttpContext context, CancellationToken cancellationToken) =>
        {
            if (!TryActor(context, identity, out WorkbenchActor actor))
            {
                return Results.Unauthorized();
            }

            // Read as a document so 'workspaceId' rides alongside the run request without changing that
            // contract, exactly as the execute endpoint already does.
            MigrationRunRequest? request = null;
            string? workspaceId = null;
            string? projectId = null;

            try
            {
                using JsonDocument document = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: cancellationToken);
                workspaceId = document.RootElement.TryGetProperty("workspaceId", out JsonElement id) && id.ValueKind == JsonValueKind.String
                    ? id.GetString()
                    : null;
                projectId = ReadString(document.RootElement, "projectId");
                request = document.RootElement.Deserialize<MigrationRunRequest>(RequestOptions);
            }
            catch (JsonException)
            {
                request = null;
            }

            ProjectOwnerResolution project = await ResolveProjectOwnerAsync(
                context, actor, projectId, WorkbenchRoles.MigrationOperator, cancellationToken);
            if (!project.Succeeded)
            {
                return Results.Json(new { error = project.Error }, statusCode: project.Status);
            }

            return WorkbenchExecution.TryPlanRun(
                sourceWorkspaces, actor, workspaceId, request,
                out WorkbenchPlanResponse? response, out int status, out string error, project.OwnerId)
                ? Results.Ok(response)
                : Results.Json(new { error }, statusCode: status);
        });

        endpoints.MapPost("/api/workbench/agent", async (
            HttpContext context,
            WorkbenchAgentRequest request,
            CancellationToken cancellationToken) =>
        {
            if (!TryActor(context, identity, out _))
            {
                return Results.Unauthorized();
            }

            if (foundryAgentClient is null)
            {
                return Results.Problem(
                    title: "Foundry agent is not connected",
                    detail: "Configure FOUNDRY_AGENT_ENDPOINT to enable this workbench capability.",
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            string message = request.Message?.Trim() ?? string.Empty;
            if (message.Length is < 1 or > 8000)
            {
                return Results.BadRequest(new { error = "Message must contain between 1 and 8,000 characters." });
            }

            if (FleetGuardrails.ContainsPotentialSecret(message))
            {
                return Results.BadRequest(new { error = "Potential credential material was rejected before model invocation." });
            }

            try
            {
                WorkbenchAgentResponse response = await foundryAgentClient.AskAsync(message, cancellationToken);
                return Results.Ok(response);
            }
            catch (HttpRequestException)
            {
                return Results.Problem(
                    title: "Foundry agent invocation failed",
                    detail: "The configured agent did not return a successful response.",
                    statusCode: StatusCodes.Status502BadGateway);
            }
        });

        if (sourceWorkspaces is not null)
        {
            ILogger logger = endpoints.ServiceProvider
                .GetRequiredService<ILoggerFactory>()
                .CreateLogger("OracleFormsMigrationFleet.Workbench");

            MapSourceAcquisition(endpoints, sourceWorkspaces, identity);
            MapExecution(endpoints, sourceWorkspaces, logger, identity);
        }

        WorkbenchPlatformEndpoints.Map(endpoints, identity, sourceWorkspaces);
    }

    private static void MapSourceAcquisition(
        IEndpointRouteBuilder endpoints,
        SourceWorkspaceService workspaces,
        IWorkbenchIdentityProvider identity)
    {
        endpoints.MapPost("/api/workbench/source/clone", async (
            HttpContext context,
            WorkbenchCloneRequest request,
            CancellationToken cancellationToken) =>
        {
            if (!TryActor(context, identity, out WorkbenchActor actor))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }

            ProjectOwnerResolution project = await ResolveProjectOwnerAsync(
                context, actor, request.ProjectId, WorkbenchRoles.MigrationOperator, cancellationToken);
            if (!project.Succeeded)
            {
                await WriteErrorAsync(context, project.Status, project.Error, cancellationToken);
                return;
            }

            await StreamAsync(
                context,
                workspaces.CloneAsync(project.OwnerId, request.RepositoryUrl ?? string.Empty, request.Branch, cancellationToken),
                workspaces,
                project.OwnerId,
                cancellationToken);
        });

        endpoints.MapPost("/api/workbench/source/upload", async (
            HttpContext context,
            CancellationToken cancellationToken) =>
        {
            if (!TryActor(context, identity, out WorkbenchActor actor))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }

            // Uploaded source is much larger than any JSON payload this console handles.
            if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } sizeFeature)
            {
                sizeFeature.MaxRequestBodySize = 268_435_456;
            }

            if (!context.Request.HasFormContentType)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }

            ProjectOwnerResolution project = await ResolveProjectOwnerAsync(
                context,
                actor,
                context.Request.Query["projectId"].FirstOrDefault(),
                WorkbenchRoles.MigrationOperator,
                cancellationToken);
            if (!project.Succeeded)
            {
                await WriteErrorAsync(context, project.Status, project.Error, cancellationToken);
                return;
            }

            IFormCollection form = await context.Request.ReadFormAsync(cancellationToken);
            IFormFile? file = form.Files.GetFile("archive");
            if (file is null)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }

            await using Stream stream = file.OpenReadStream();
            await StreamAsync(
                context,
                workspaces.ExtractAsync(project.OwnerId, stream, file.FileName, cancellationToken),
                workspaces,
                project.OwnerId,
                cancellationToken);
        });

        endpoints.MapDelete("/api/workbench/source/{workspaceId}", async (
            HttpContext context, string workspaceId, string? projectId, CancellationToken cancellationToken) =>
        {
            if (!TryActor(context, identity, out WorkbenchActor actor))
            {
                return Results.Unauthorized();
            }

            ProjectOwnerResolution project = await ResolveProjectOwnerAsync(
                context, actor, projectId, WorkbenchRoles.MigrationOperator, cancellationToken);
            if (!project.Succeeded)
            {
                return Results.Json(new { error = project.Error }, statusCode: project.Status);
            }

            return workspaces.Release(project.OwnerId, workspaceId) ? Results.NoContent() : Results.NotFound();
        });
    }

    /// <summary>
    /// Runs the phases the planner authorized for a source copy the caller owns.
    ///
    /// The workspace is addressed by identifier and resolved against the signed-in owner, so no path
    /// from the request body ever reaches the file system. Every write lands under the session-private
    /// output directory; the read-only source copy, the customer's repository, and every database and
    /// Azure resource are untouched.
    /// </summary>
    private static void MapExecution(
        IEndpointRouteBuilder endpoints,
        SourceWorkspaceService workspaces,
        ILogger logger,
        IWorkbenchIdentityProvider identity)
    {
        endpoints.MapPost("/api/workbench/execute", async (HttpContext context, CancellationToken cancellationToken) =>
        {
            if (!TryActor(context, identity, out WorkbenchActor actor))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }
            string owner = actor.OwnerId;
            MigrationRunRequest? request = null;
            string? workspaceId = null;
            string? projectId = null;
            string? targetProfileId = null;

            try
            {
                using JsonDocument document = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: cancellationToken);
                workspaceId = ReadString(document.RootElement, "workspaceId");
                projectId = ReadString(document.RootElement, "projectId");
                targetProfileId = ReadString(document.RootElement, "targetProfileId");
                request = document.RootElement.Deserialize<MigrationRunRequest>(RequestOptions);
            }
            catch (JsonException)
            {
                request = null;
            }

            if (string.IsNullOrWhiteSpace(projectId))
            {
                await WriteErrorAsync(context, 400, "A project is required for every run.", cancellationToken);
                return;
            }

            // The authorization service is resolved from the container rather than constructed here, so a
            // deployment that gains a trusted store gets it without this endpoint changing. Absent one it
            // is backed by a store that holds nothing, and every mutating phase is refused.
            WorkbenchAuthorizationService authorization =
                context.RequestServices.GetService<WorkbenchAuthorizationService>() ?? new WorkbenchAuthorizationService();

            PlatformAccessService? platform = context.RequestServices.GetService<PlatformAccessService>();

            // A project and a profile are identifiers. Everything they bind to is looked up here, so a
            // caller can select an engagement they belong to and can select nothing else about it.
            WorkbenchExecution.WorkbenchRunBinding? binding = null;
            if (!string.IsNullOrWhiteSpace(projectId))
            {
                if (platform is null)
                {
                    await WriteErrorAsync(context, 503, "This deployment has no platform state store, so a project cannot be resolved.", cancellationToken);
                    return;
                }

                PlatformResult<PlatformMembership> membership = await platform
                    .RequireMembershipAsync(actor, projectId!, WorkbenchRoles.MigrationOperator, cancellationToken);

                if (!membership.Succeeded)
                {
                    await WriteErrorAsync(context, membership.Status, membership.Error, cancellationToken);
                    return;
                }

                PlatformTargetProfile? profile = await platform.Store.GetTargetProfileAsync(
                    actor.TenantId, projectId!, targetProfileId ?? "sandbox", version: null, cancellationToken);

                if (profile is null)
                {
                    await WriteErrorAsync(context, 404, "That project has no target profile with that identifier.", cancellationToken);
                    return;
                }

                binding = new WorkbenchExecution.WorkbenchRunBinding(
                    profile.ProjectId,
                    profile.TargetProfileId,
                    profile.Version,
                    profile.CanonicalHash,
                    PlatformIdentity.WorkspaceOwner(actor, profile.ProjectId));
            }

            WorkbenchExecution.WorkbenchRunPreparationResult preparationResult = await WorkbenchExecution.PrepareRunAsync(
                workspaces, actor, workspaceId, request, authorization, binding, cancellationToken);

            if (!preparationResult.Succeeded)
            {
                await WriteErrorAsync(context, preparationResult.Status, preparationResult.Error, cancellationToken);
                return;
            }

            WorkbenchExecution.WorkbenchRunPreparation preparation = preparationResult.Preparation!;
            string workspaceRoot = preparation.WorkspaceRoot;
            MigrationRunRequest prepared = preparation.Request;

            if (context.RequestServices.GetService<IMigrationRunStore>() is { } runStore)
            {
                if (FleetGuardrails.ContainsPotentialSecret(JsonSerializer.Serialize(prepared, RequestOptions)))
                {
                    await WriteErrorAsync(
                        context,
                        400,
                        "Potential credential material was rejected before the run was stored.",
                        cancellationToken);
                    return;
                }
                string runId = $"run-{Guid.NewGuid():N}";
                string suffix = prepared.OutputRoot.Length > WorkbenchExecution.OutputRoot.Length
                    ? prepared.OutputRoot[(WorkbenchExecution.OutputRoot.Length + 1)..]
                    : "out";
                MigrationRunRequest durableRequest = prepared with
                {
                    OutputRoot = $"{WorkbenchExecution.OutputRoot}/runs/{runId}/{suffix}",
                    ExecutionApproval = HumanApproval.Pending,
                    ProductionApproval = HumanApproval.Pending,
                    Attestations = [],
                };
                await runStore.EnqueueAsync(
                    new MigrationRunRecord
                    {
                        RunId = runId,
                        TenantId = actor.TenantId,
                        ProjectId = projectId!,
                        ActorObjectId = actor.ObjectId,
                        WorkspaceId = workspaceId!,
                        WorkspaceNodeId = MigrationRunNode.Current,
                        WorkspaceOwnerId = binding!.WorkspaceOwnerId,
                        SourceSnapshotHash = preparation.SourceSnapshotHash,
                        PlanInputHash = preparation.PlanInputHash,
                        TargetProfileId = binding.TargetProfileId,
                        TargetProfileVersion = binding.TargetProfileVersion,
                        TargetProfileHash = preparation.TargetHash,
                        Request = durableRequest,
                        EnqueuedUtc = DateTimeOffset.UtcNow,
                    },
                    cancellationToken);
                context.Response.StatusCode = StatusCodes.Status202Accepted;
                await context.Response.WriteAsJsonAsync(new { runId }, cancellationToken);
                return;
            }

            context.Response.ContentType = "text/event-stream";
            context.Response.Headers.CacheControl = "no-cache";

            WorkbenchExecution.ResetOutput(workspaceRoot);

            // Sequence numbers are assigned here, in one place, so a consumer can tell a gap from a
            // reorder. The producers know what happened; only the stream knows the order it left in.
            WorkbenchProgressSequence frames = new();

            Channel<ExecutionProgress> channel = Channel.CreateUnbounded<ExecutionProgress>(
                new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });

            // The gateway is re-asked for permission on every call it makes, not once for the phase, so a
            // revocation part way through a long data copy stops the next statement.
            IDataMigrationGateway? gateway = context.RequestServices.GetService<IDataMigrationGateway>();
            if (gateway is not null)
            {
                gateway = new AuthorizingDataMigrationGateway(gateway, preparation.MutationAuthorizer, prepared);
            }

            MigrationExecutor executor = new(
                workspaceRoot,
                MigrationExecutor.DefaultAdapters(
                    context.RequestServices.GetService<IArtifactReviewer>(),
                    gateway,
                    context.RequestServices.GetService<Fleet.Agents.CritiqueRepairOrchestrator>(),
                    context.RequestServices.GetService<ProgramUnitRepairLoop>(),
                    context.RequestServices.GetService<IApplicationBuildGateway>()),
                preparation.MutationAuthorizer);

            // The run moves off the request thread to keep progress frames flowing while it works.
            Task<MigrationExecutionResult> run = Task.Run(async () =>
            {
                try
                {
                    return await executor.ExecuteAsync(
                        prepared,
                        owner,
                        step => channel.Writer.TryWrite(step),
                        cancellationToken);
                }
                finally
                {
                    channel.Writer.TryComplete();
                }
            }, cancellationToken);

            try
            {
                while (!run.IsCompleted)
                {
                    Task<bool> available = channel.Reader.WaitToReadAsync(cancellationToken).AsTask();
                    Task heartbeat = Task.Delay(TimeSpan.FromSeconds(20), cancellationToken);
                    Task completed = await Task.WhenAny(available, heartbeat);

                    if (completed == heartbeat)
                    {
                        // Waiting, not working: the run has produced nothing new for twenty seconds.
                        await WriteFrameAsync(
                            context,
                            WorkbenchProgressFrame.Create(
                                frames,
                                "keepalive",
                                "Build or migration work is still running.",
                                new ProgressSignal(
                                    ProgressOperations.MigrationRun,
                                    ProgressActions.RunHeartbeat,
                                    ProgressState.Waiting,
                                    RunPurpose,
                                    "The run is still open but has reported nothing new for twenty seconds.",
                                    "Leave this open, or close it and come back \u2014 the run is not affected either way.")),
                            cancellationToken);
                        continue;
                    }

                    while (channel.Reader.TryRead(out ExecutionProgress? step))
                    {
                        await WriteFrameAsync(context, WorkbenchProgressFrame.Create(frames, step.Level, step.Text, step.Signal), cancellationToken);
                    }
                }

                while (channel.Reader.TryRead(out ExecutionProgress? step))
                {
                    await WriteFrameAsync(context, WorkbenchProgressFrame.Create(frames, step.Level, step.Text, step.Signal), cancellationToken);
                }

                MigrationExecutionResult result = await run;
                int executed = result.Phases.Count(phase => phase.State == PhaseExecutionState.Executed);
                bool failed = WorkbenchRunOutcome.Failed(result.Phases);

                // Terminal frame. The browser treats a stream that ends without one as interrupted,
                // so this is the only thing entitled to say the run reached an outcome.
                await WriteFrameAsync(
                    context,
                    WorkbenchProgressFrame.Create(
                        frames,
                        failed ? "error" : "done",
                        string.Empty,
                        new ProgressSignal(
                            ProgressOperations.MigrationRun,
                            failed ? ProgressActions.RunFailed : ProgressActions.RunCompleted,
                            failed ? ProgressState.Failed : ProgressState.Completed,
                            RunPurpose,
                            $"{executed} of {result.Phases.Count} phase(s) ran and {result.Artifacts.Count} file(s) were written.",
                            failed
                                ? "Read the unperformed or failed phases below before running anything else."
                                : "Review what was written. Generated code that has never been compiled is not working software.",
                            "GeneratedFile",
                            result.Artifacts.Count),
                        extra: new Dictionary<string, object?> { ["result"] = WorkbenchExecution.Project(result) }),
                    cancellationToken);
            }
            catch (OperationCanceledException)
            {
                // The operator navigated away or the host is shutting down. Nothing further is written.
            }
            catch (Exception exception)
            {
                // Reason stays in the server log; the browser is told only that the run stopped.
                logger.LogError(exception, "Workbench execution failed for workspace {WorkspaceId}.", workspaceId);
                await WriteFrameAsync(
                    context,
                    WorkbenchProgressFrame.Create(
                        frames,
                        "error",
                        "The run stopped before it finished. Nothing further was written.",
                        new ProgressSignal(
                            ProgressOperations.MigrationRun,
                            ProgressActions.RunFailed,
                            ProgressState.Failed,
                            RunPurpose,
                            "The run stopped before it finished. Nothing further was written.",
                            "Nothing beyond the files already listed was produced. Start the run again when the cause is understood.")),
                    CancellationToken.None);
            }
        });

        endpoints.MapGet("/api/workbench/projects/{projectId}/runs", async (
            HttpContext context, string projectId, CancellationToken cancellationToken) =>
        {
            if (!TryActor(context, identity, out WorkbenchActor actor))
            {
                return Results.Unauthorized();
            }
            PlatformAccessService? platform = context.RequestServices.GetService<PlatformAccessService>();
            IMigrationRunStore? runs = context.RequestServices.GetService<IMigrationRunStore>();
            if (platform is null || runs is null)
            {
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            }
            PlatformResult<PlatformMembership> membership = await platform.RequireMembershipAsync(
                actor, projectId, null, cancellationToken);
            if (!membership.Succeeded)
            {
                return Results.Json(new { error = membership.Error }, statusCode: membership.Status);
            }
            IReadOnlyList<MigrationRunRecord> history = await runs.ForProjectAsync(
                actor.TenantId, projectId, 50, cancellationToken);
            return Results.Ok(new { runs = history.Select(ProjectRun).ToArray() });
        });

        endpoints.MapGet("/api/workbench/runs/{runId}", async (
            HttpContext context, string runId, CancellationToken cancellationToken) =>
        {
            if (!TryActor(context, identity, out WorkbenchActor actor))
            {
                return Results.Unauthorized();
            }
            PlatformAccessService? platform = context.RequestServices.GetService<PlatformAccessService>();
            IMigrationRunStore? runs = context.RequestServices.GetService<IMigrationRunStore>();
            MigrationRunRecord? run = runs is null
                ? null
                : await runs.GetAsync(actor.TenantId, runId, cancellationToken);
            if (platform is null || runs is null)
            {
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            }
            if (run is null)
            {
                return Results.NotFound();
            }
            PlatformResult<PlatformMembership> membership = await platform.RequireMembershipAsync(
                actor, run.ProjectId, null, cancellationToken);
            if (!membership.Succeeded)
            {
                return Results.Json(new { error = membership.Error }, statusCode: membership.Status);
            }
            return Results.Ok(new
            {
                run = ProjectRun(run),
                outcome = run.Outcome,
                artifacts = await runs.ArtifactsAsync(actor.TenantId, runId, cancellationToken),
            });
        });

        endpoints.MapGet("/api/workbench/runs/{runId}/events", async (
            HttpContext context, string runId, long? afterSequence, CancellationToken cancellationToken) =>
        {
            if (!TryActor(context, identity, out WorkbenchActor actor))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }
            PlatformAccessService? platform = context.RequestServices.GetService<PlatformAccessService>();
            IMigrationRunStore? runs = context.RequestServices.GetService<IMigrationRunStore>();
            MigrationRunRecord? run = runs is null
                ? null
                : await runs.GetAsync(actor.TenantId, runId, cancellationToken);
            if (platform is null || runs is null)
            {
                context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                return;
            }
            if (run is null)
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }
            PlatformResult<PlatformMembership> membership = await platform.RequireMembershipAsync(
                actor, run.ProjectId, null, cancellationToken);
            if (!membership.Succeeded)
            {
                context.Response.StatusCode = membership.Status;
                return;
            }

            context.Response.ContentType = "text/event-stream";
            context.Response.Headers.CacheControl = "no-cache";
            long cursor = Math.Max(0, afterSequence ?? 0);
            while (!cancellationToken.IsCancellationRequested)
            {
                membership = await platform.RequireMembershipAsync(
                    actor, run.ProjectId, null, cancellationToken);
                if (!membership.Succeeded)
                {
                    return;
                }
                IReadOnlyList<MigrationRunEvent> events = await runs.EventsAsync(
                    actor.TenantId, runId, cursor, cancellationToken);
                foreach (MigrationRunEvent item in events)
                {
                    await WriteFrameAsync(context, WorkbenchProgressFrame.Replay(item), cancellationToken);
                    cursor = item.Sequence;
                }

                run = await runs.GetAsync(actor.TenantId, runId, cancellationToken);
                if (run is null || (run.IsTerminal && cursor >= run.LastSequence))
                {
                    return;
                }
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
            }
        });

        endpoints.MapPost("/api/workbench/runs/{runId}/cancel", async (
            HttpContext context, string runId, CancellationToken cancellationToken) =>
        {
            if (!TryActor(context, identity, out WorkbenchActor actor))
            {
                return Results.Unauthorized();
            }
            PlatformAccessService? platform = context.RequestServices.GetService<PlatformAccessService>();
            IMigrationRunStore? runs = context.RequestServices.GetService<IMigrationRunStore>();
            MigrationRunRecord? run = runs is null
                ? null
                : await runs.GetAsync(actor.TenantId, runId, cancellationToken);
            if (platform is null || runs is null)
            {
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            }
            if (run is null)
            {
                return Results.NotFound();
            }
            PlatformResult<PlatformMembership> membership = await platform.RequireMembershipAsync(
                actor, run.ProjectId, WorkbenchRoles.MigrationOperator, cancellationToken);
            if (!membership.Succeeded)
            {
                return Results.Json(new { error = membership.Error }, statusCode: membership.Status);
            }
            return await runs.RequestCancellationAsync(
                actor.TenantId, runId, actor.ObjectId, DateTimeOffset.UtcNow, cancellationToken)
                ? Results.Accepted($"/api/workbench/runs/{runId}")
                : Results.Conflict();
        });

        endpoints.MapGet("/api/workbench/artifact", async (
            HttpContext context, string? workspaceId, string? path, string? projectId, string? runId, CancellationToken cancellationToken) =>
        {
            if (!TryActor(context, identity, out WorkbenchActor actor))
            {
                return Results.Unauthorized();
            }

            string owner;
            string? durableRoot = null;
            MigrationRunArtifact? manifestArtifact = null;
            if (!string.IsNullOrWhiteSpace(runId) && context.RequestServices.GetService<IMigrationRunStore>() is { } runs)
            {
                MigrationRunRecord? run = await runs.GetAsync(actor.TenantId, runId, cancellationToken);
                if (run is null)
                {
                    return Results.NotFound();
                }
                PlatformAccessService? platform = context.RequestServices.GetService<PlatformAccessService>();
                if (platform is null)
                {
                    return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
                }
                PlatformResult<PlatformMembership> membership = await platform.RequireMembershipAsync(
                    actor, run.ProjectId, WorkbenchRoles.MigrationOperator, cancellationToken);
                if (!membership.Succeeded)
                {
                    return Results.Json(new { error = membership.Error }, statusCode: membership.Status);
                }
                IReadOnlyList<MigrationRunArtifact> manifest = await runs.ArtifactsAsync(
                    actor.TenantId, runId, cancellationToken);
                manifestArtifact = manifest.FirstOrDefault(
                    artifact => string.Equals(artifact.Path, path, StringComparison.Ordinal));
                if (manifestArtifact is null)
                {
                    return Results.NotFound();
                }
                owner = run.WorkspaceOwnerId;
                workspaceId = run.WorkspaceId;
                durableRoot = workspaces.ResolveRoot(owner, workspaceId) ?? workspaces.ResolveDurableRoot(workspaceId);
            }
            else
            {
                ProjectOwnerResolution project = await ResolveProjectOwnerAsync(
                    context, actor, projectId, WorkbenchRoles.MigrationOperator, cancellationToken);
                if (!project.Succeeded)
                {
                    return Results.Json(new { error = project.Error }, statusCode: project.Status);
                }
                owner = project.OwnerId;
            }

            string absolutePath;
            int status;
            string error;
            bool resolved = durableRoot is null
                ? WorkbenchExecution.TryResolveArtifact(
                    workspaces, owner, workspaceId, path,
                    out absolutePath, out status, out error)
                : WorkbenchExecution.TryResolveArtifact(
                    durableRoot, path,
                    out absolutePath, out status, out error);
            if (!resolved)
            {
                if (!string.IsNullOrWhiteSpace(runId) && status == 404)
                {
                    return Results.Json(
                        new { error = "The artifact manifest is retained, but its workspace bytes have expired." },
                        statusCode: StatusCodes.Status410Gone);
                }
                return Results.Json(new { error }, statusCode: status);
            }

            if (manifestArtifact is not null)
            {
                FileInfo file = new(absolutePath);
                using FileStream stream = file.OpenRead();
                string hash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
                if (file.Length != manifestArtifact.ByteLength ||
                    !CryptographicOperations.FixedTimeEquals(
                        Convert.FromHexString(hash), Convert.FromHexString(manifestArtifact.ContentSha256)))
                {
                    return Results.Json(
                        new { error = "The retained artifact bytes no longer match the durable run manifest." },
                        statusCode: StatusCodes.Status409Conflict);
                }
            }

            string text;
            bool truncated;
            try
            {
                text = WorkbenchExecution.ReadPreview(absolutePath, out truncated);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(exception, "Artifact preview could not be read.");
                return Results.Json(
                    new { error = "That artifact could not be read." },
                    statusCode: StatusCodes.Status500InternalServerError);
            }

            if (truncated)
            {
                text += "\n\n--- Truncated at 512 KB. ---\n";
            }

            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers.XContentTypeOptions = "nosniff";
            return Results.Text(text, "text/plain; charset=utf-8");
        });

        // Session workspaces are swept after four hours, so without this a completed migration is lost.
        endpoints.MapGet("/api/workbench/export", async (
            HttpContext context, string? workspaceId, string? projectId, CancellationToken cancellationToken) =>
        {
            if (!TryActor(context, identity, out WorkbenchActor actor))
            {
                await Results.Unauthorized().ExecuteAsync(context);
                return;
            }

            ProjectOwnerResolution project = await ResolveProjectOwnerAsync(
                context, actor, projectId, WorkbenchRoles.MigrationOperator, cancellationToken);
            if (!project.Succeeded)
            {
                await Results.Json(new { error = project.Error }, statusCode: project.Status).ExecuteAsync(context);
                return;
            }

            if (!WorkbenchExecution.TryResolveExport(
                workspaces, project.OwnerId, workspaceId,
                out string runRoot, out int status, out string error))
            {
                await Results.Json(new { error }, statusCode: status).ExecuteAsync(context);
                return;
            }

            context.Response.ContentType = "application/zip";
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers.XContentTypeOptions = "nosniff";
            context.Response.Headers.ContentDisposition = "attachment; filename=\"migration-output.zip\"";

            try
            {
                await using MemoryStream buffer = new();
                WorkbenchExecution.WriteExport(runRoot, buffer);
                buffer.Position = 0;
                await buffer.CopyToAsync(context.Response.Body, cancellationToken);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(exception, "Run output could not be exported.");
            }
        });
    }

    private const string RunPurpose =
        "Running the phases the planner authorized and writing their output into your session workspace.";

    private const string AcquisitionPurpose =
        "Taking a private read-only copy of your source so the fleet has something it can read.";

    private static async Task WriteFrameAsync(HttpContext context, object payload, CancellationToken cancellationToken)
    {
        await context.Response.WriteAsync($"data: {JsonSerializer.Serialize(payload, JsonOptions)}\n\n", cancellationToken);
        await context.Response.Body.FlushAsync(cancellationToken);
    }

    private static async Task StreamAsync(
        HttpContext context,
        IAsyncEnumerable<SourceProgress> progress,
        SourceWorkspaceService workspaces,
        string owner,
        CancellationToken cancellationToken)
    {
        context.Response.ContentType = "text/event-stream";
        context.Response.Headers.CacheControl = "no-cache";

        WorkbenchProgressSequence frames = new();
        bool terminal = false;

        await foreach (SourceProgress step in progress.WithCancellation(cancellationToken))
        {
            Dictionary<string, object?> frame =
                step.Level == "done" && workspaces.Get(owner, step.Text) is SourceWorkspaceSummary summary
                    ? WorkbenchProgressFrame.Create(
                        frames,
                        "done",
                        string.Empty,
                        step.Signal,
                        new Dictionary<string, object?> { ["workspace"] = summary })
                    : WorkbenchProgressFrame.Create(frames, step.Level, step.Text, step.Signal);

            terminal |= step.Signal?.State is ProgressState.Completed or ProgressState.Failed;
            await WriteFrameAsync(context, frame, cancellationToken);
        }

        if (terminal)
        {
            return;
        }

        // The enumerator finished without either outcome. Saying so is the honest frame: the browser
        // would otherwise have to guess, and a stopped stream is not a successful copy.
        await WriteFrameAsync(
            context,
            WorkbenchProgressFrame.Create(
                frames,
                "error",
                "The copy stopped before it reported an outcome. Nothing was kept.",
                new ProgressSignal(
                    ProgressOperations.SourceAcquisition,
                    ProgressActions.SourceFailed,
                    ProgressState.Failed,
                    AcquisitionPurpose,
                    "The copy stopped before it reported an outcome.",
                    "Nothing was kept. Start the copy again.")),
            cancellationToken);
    }

    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    /// <summary>The console posts enum values as names, exactly as the plan endpoint accepts them.</summary>
    private static readonly JsonSerializerOptions RequestOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>
    /// Workspace ownership comes from the authenticated identity only. It is never taken from the
    /// request body, so one signed-in user cannot address another user's copy.
    /// </summary>
    private static bool TryOwner(
        HttpContext context,
        IWorkbenchIdentityProvider identity,
        out string owner)
    {
        bool authenticated = TryActor(context, identity, out WorkbenchActor actor);
        owner = actor.OwnerId;
        return authenticated;
    }

    private static object ProjectRun(MigrationRunRecord run) => new
    {
        runId = run.RunId,
        projectId = run.ProjectId,
        engagementId = run.Request.EngagementId,
        applicationName = run.Request.ApplicationName,
        state = run.State.ToString(),
        enqueuedUtc = run.EnqueuedUtc,
        startedUtc = run.StartedUtc,
        completedUtc = run.CompletedUtc,
        lastSequence = run.LastSequence,
        cancelRequestedUtc = run.CancelRequestedUtc,
        failureReason = run.FailureReason,
    };

    /// <summary>
    /// The authenticated caller and the roles the host says they hold.
    ///
    /// Both come from the configured identity provider, which reads platform headers the ingress
    /// overwrites on every inbound request. No request body is consulted: a caller who could name their
    /// own roles would be granting themselves authority.
    /// </summary>
    internal static bool TryActor(
        HttpContext context,
        IWorkbenchIdentityProvider identity,
        out WorkbenchActor actor)
    {
        WorkbenchIdentityResult result = identity.Authenticate(
            name => context.Request.Headers[name] is { Count: 1 } values ? values[0] : null);

        if (!result.IsAuthenticated && identity.Mode == WorkbenchAuthenticationMode.ContainerApps)
        {
            context.RequestServices
                .GetRequiredService<ILoggerFactory>()
                .CreateLogger("OracleFormsMigrationFleet.WorkbenchIdentity")
                .LogWarning("Container Apps identity rejected the request: {Reason}", result.Reason);
        }

        actor = result.Actor ?? new WorkbenchActor(string.Empty, []);
        return result.IsAuthenticated;
    }

    internal static string? ReadString(JsonElement root, string property) =>
        root.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private sealed record ProjectOwnerResolution(bool Succeeded, string OwnerId, int Status, string Error);

    private static async Task<ProjectOwnerResolution> ResolveProjectOwnerAsync(
        HttpContext context,
        WorkbenchActor actor,
        string? projectId,
        string requiredRole,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(projectId))
        {
            return new(false, string.Empty, 400, "A project is required for this operation.");
        }

        if (context.RequestServices.GetService<PlatformAccessService>() is not { } platform)
        {
            return new(false, string.Empty, 503, "This deployment has no platform state store, so a project cannot be resolved.");
        }

        PlatformResult<PlatformMembership> membership = await platform
            .RequireMembershipAsync(actor, projectId, requiredRole, cancellationToken);
        return membership.Succeeded
            ? new(true, PlatformIdentity.WorkspaceOwner(actor, projectId), 200, string.Empty)
            : new(false, string.Empty, membership.Status, membership.Error);
    }

    private static async Task WriteErrorAsync(HttpContext context, int status, string error, CancellationToken cancellationToken)
    {
        context.Response.StatusCode = status;
        await context.Response.WriteAsJsonAsync(new { error }, cancellationToken);
    }

    /// <summary>Reports presence only. The connection string value is never read into the response.</summary>
    private static bool ApplicationInsightsConfigured() =>
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("APPLICATIONINSIGHTS_CONNECTION_STRING"));

    private static IResult ServeAsset(HttpContext context, string webRoot, string fileName, string contentType)
    {
        string path = Path.Combine(webRoot, fileName);
        if (!File.Exists(path))
        {
            return Results.NotFound();
        }

        // These asset names are stable across releases, so without a validator the browser keeps
        // serving the previous deployment's bundle. "no-cache" still allows a 304 revalidation.
        context.Response.Headers.CacheControl = "no-cache";
        return Results.File(path, contentType, lastModified: File.GetLastWriteTimeUtc(path));
    }
}
