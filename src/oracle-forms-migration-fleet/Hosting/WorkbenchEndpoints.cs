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
        bool entraAuthenticationConfigured = false,
        bool sandboxDatabaseConfigured = false,
        SourceWorkspaceService? sourceWorkspaces = null)
    {
        IHostEnvironment environment = endpoints.ServiceProvider.GetRequiredService<IHostEnvironment>();
        string webRoot = Path.Combine(environment.ContentRootPath, "wwwroot");

        endpoints.MapGet("/", (HttpContext context) => ServeAsset(context, webRoot, "index.html", "text/html; charset=utf-8"));
        endpoints.MapGet("/styles.css", (HttpContext context) => ServeAsset(context, webRoot, "styles.css", "text/css; charset=utf-8"));
        endpoints.MapGet("/app.js", (HttpContext context) => ServeAsset(context, webRoot, "app.js", "text/javascript; charset=utf-8"));

        endpoints.MapGet("/api/workbench/bootstrap", (HttpContext context) =>
        {
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
            if (!TryActor(context, entraAuthenticationConfigured, out WorkbenchActor actor))
            {
                return Results.Unauthorized();
            }

            // Read as a document so 'workspaceId' rides alongside the run request without changing that
            // contract, exactly as the execute endpoint already does.
            MigrationRunRequest? request = null;
            string? workspaceId = null;

            try
            {
                using JsonDocument document = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: cancellationToken);
                workspaceId = document.RootElement.TryGetProperty("workspaceId", out JsonElement id) && id.ValueKind == JsonValueKind.String
                    ? id.GetString()
                    : null;
                request = document.RootElement.Deserialize<MigrationRunRequest>(RequestOptions);
            }
            catch (JsonException)
            {
                request = null;
            }

            return WorkbenchExecution.TryPlanRun(
                sourceWorkspaces, actor, workspaceId, request,
                out WorkbenchPlanResponse? response, out int status, out string error)
                ? Results.Ok(response)
                : Results.Json(new { error }, statusCode: status);
        });

        endpoints.MapPost("/api/workbench/agent", async (
            WorkbenchAgentRequest request,
            CancellationToken cancellationToken) =>
        {
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

            MapSourceAcquisition(endpoints, sourceWorkspaces, entraAuthenticationConfigured);
            MapExecution(endpoints, sourceWorkspaces, logger, entraAuthenticationConfigured);
        }
    }

    private static void MapSourceAcquisition(
        IEndpointRouteBuilder endpoints,
        SourceWorkspaceService workspaces,
        bool entraAuthenticationConfigured)
    {
        endpoints.MapPost("/api/workbench/source/clone", async (
            HttpContext context,
            WorkbenchCloneRequest request,
            CancellationToken cancellationToken) =>
        {
            if (!TryOwner(context, entraAuthenticationConfigured, out string owner))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }

            await StreamAsync(
                context,
                workspaces.CloneAsync(owner, request.RepositoryUrl ?? string.Empty, request.Branch, cancellationToken),
                workspaces,
                owner,
                cancellationToken);
        });

        endpoints.MapPost("/api/workbench/source/upload", async (
            HttpContext context,
            CancellationToken cancellationToken) =>
        {
            if (!TryOwner(context, entraAuthenticationConfigured, out string owner))
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

            IFormFile? file = (await context.Request.ReadFormAsync(cancellationToken)).Files.GetFile("archive");
            if (file is null)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }

            await using Stream stream = file.OpenReadStream();
            await StreamAsync(
                context,
                workspaces.ExtractAsync(owner, stream, file.FileName, cancellationToken),
                workspaces,
                owner,
                cancellationToken);
        });

        endpoints.MapDelete("/api/workbench/source/{workspaceId}", (HttpContext context, string workspaceId) =>
            !TryOwner(context, entraAuthenticationConfigured, out string owner)
                ? Results.Unauthorized()
                : workspaces.Release(owner, workspaceId) ? Results.NoContent() : Results.NotFound());
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
        bool entraAuthenticationConfigured)
    {
        endpoints.MapPost("/api/workbench/execute", async (HttpContext context, CancellationToken cancellationToken) =>
        {
            if (!TryActor(context, entraAuthenticationConfigured, out WorkbenchActor actor))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }
            string owner = actor.OwnerId;
            MigrationRunRequest? request = null;
            string? workspaceId = null;

            try
            {
                using JsonDocument document = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: cancellationToken);
                workspaceId = document.RootElement.TryGetProperty("workspaceId", out JsonElement id) && id.ValueKind == JsonValueKind.String
                    ? id.GetString()
                    : null;
                request = document.RootElement.Deserialize<MigrationRunRequest>(RequestOptions);
            }
            catch (JsonException)
            {
                request = null;
            }

            // The authorization service is resolved from the container rather than constructed here, so a
            // deployment that gains a trusted store gets it without this endpoint changing. Absent one it
            // is backed by a store that holds nothing, and every mutating phase is refused.
            WorkbenchAuthorizationService authorization =
                context.RequestServices.GetService<WorkbenchAuthorizationService>() ?? new WorkbenchAuthorizationService();

            if (!WorkbenchExecution.TryPrepareRun(
                workspaces, actor, workspaceId, request, authorization,
                out WorkbenchExecution.WorkbenchRunPreparation? preparation, out int status, out string error))
            {
                context.Response.StatusCode = status;
                await context.Response.WriteAsJsonAsync(new { error }, cancellationToken);
                return;
            }

            string workspaceRoot = preparation!.WorkspaceRoot;
            MigrationRunRequest prepared = preparation.Request;

            context.Response.ContentType = "text/event-stream";
            context.Response.Headers.CacheControl = "no-cache";

            WorkbenchExecution.ResetOutput(workspaceRoot);

            // Sequence numbers are assigned here, in one place, so a consumer can tell a gap from a
            // reorder. The producers know what happened; only the stream knows the order it left in.
            WorkbenchProgressSequence frames = new();

            Channel<ExecutionProgress> channel = Channel.CreateUnbounded<ExecutionProgress>(
                new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });

            MigrationExecutor executor = new(
                workspaceRoot,
                MigrationExecutor.DefaultAdapters(
                    context.RequestServices.GetService<IArtifactReviewer>(),
                    context.RequestServices.GetService<IDataMigrationGateway>(),
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

        endpoints.MapGet("/api/workbench/artifact", (HttpContext context, string? workspaceId, string? path) =>
        {
            if (!TryOwner(context, entraAuthenticationConfigured, out string owner))
            {
                return Results.Unauthorized();
            }

            if (!WorkbenchExecution.TryResolveArtifact(
                workspaces, owner, workspaceId, path,
                out string absolutePath, out int status, out string error))
            {
                return Results.Json(new { error }, statusCode: status);
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
        endpoints.MapGet("/api/workbench/export", async (HttpContext context, string? workspaceId, CancellationToken cancellationToken) =>
        {
            if (!TryOwner(context, entraAuthenticationConfigured, out string owner))
            {
                await Results.Unauthorized().ExecuteAsync(context);
                return;
            }

            if (!WorkbenchExecution.TryResolveExport(
                workspaces, owner, workspaceId,
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
    /// Workspace ownership comes from the Container Apps authentication header only. It is never
    /// taken from the request body, so one signed-in user cannot address another user's copy.
    /// </summary>
    private static bool TryOwner(
        HttpContext context,
        bool entraAuthenticationConfigured,
        out string owner)
    {
        if (!entraAuthenticationConfigured)
        {
            owner = "local-development";
            return true;
        }

        owner = context.Request.Headers["X-MS-CLIENT-PRINCIPAL-ID"].FirstOrDefault() ?? string.Empty;
        return !string.IsNullOrWhiteSpace(owner);
    }

    /// <summary>
    /// The authenticated caller and the roles the host says they hold.
    ///
    /// Both come from the Container Apps authentication headers, which the platform overwrites on every
    /// inbound request. No request body is consulted: a caller who could name their own roles would be
    /// granting themselves authority. A principal header this method cannot parse yields no roles, so an
    /// unreadable claim set denies rather than defaults.
    /// </summary>
    private static bool TryActor(
        HttpContext context,
        bool entraAuthenticationConfigured,
        out WorkbenchActor actor)
    {
        if (!TryOwner(context, entraAuthenticationConfigured, out string owner))
        {
            actor = new WorkbenchActor(string.Empty, []);
            return false;
        }

        actor = new WorkbenchActor(owner, entraAuthenticationConfigured ? Roles(context) : []);
        return true;
    }

    private static IReadOnlyList<string> Roles(HttpContext context)
    {
        string? encoded = context.Request.Headers["X-MS-CLIENT-PRINCIPAL"].FirstOrDefault();
        if (string.IsNullOrEmpty(encoded) || encoded.Length > 32_768)
        {
            return [];
        }

        try
        {
            using JsonDocument principal = JsonDocument.Parse(Convert.FromBase64String(encoded));
            if (!principal.RootElement.TryGetProperty("claims", out JsonElement claims) ||
                claims.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            return
            [.. claims.EnumerateArray()
                .Where(claim => claim.ValueKind == JsonValueKind.Object
                    && claim.TryGetProperty("typ", out JsonElement type)
                    && type.ValueKind == JsonValueKind.String
                    && IsRoleClaimType(type.GetString()))
                .Select(claim => claim.TryGetProperty("val", out JsonElement value) && value.ValueKind == JsonValueKind.String
                    ? value.GetString()
                    : null)
                .OfType<string>()
                .Where(role => role.Length is > 0 and <= 256)
                .Distinct(StringComparer.OrdinalIgnoreCase)];
        }
        catch (Exception exception) when (exception is FormatException or JsonException)
        {
            return [];
        }
    }

    private static bool IsRoleClaimType(string? type) =>
        type is "roles" or "role" or "http://schemas.microsoft.com/ws/2008/06/identity/claims/role";

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
