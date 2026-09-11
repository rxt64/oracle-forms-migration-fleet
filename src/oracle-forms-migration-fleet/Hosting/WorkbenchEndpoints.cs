// Copyright (c) Microsoft. All rights reserved.

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OracleFormsMigrationFleet.Fleet;
using System.Text.Json;

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
        SourceWorkspaceService? sourceWorkspaces = null)
    {
        IHostEnvironment environment = endpoints.ServiceProvider.GetRequiredService<IHostEnvironment>();
        string webRoot = Path.Combine(environment.ContentRootPath, "wwwroot");

        endpoints.MapGet("/", (HttpContext context) => ServeAsset(context, webRoot, "index.html", "text/html; charset=utf-8"));
        endpoints.MapGet("/styles.css", (HttpContext context) => ServeAsset(context, webRoot, "styles.css", "text/css; charset=utf-8"));
        endpoints.MapGet("/app.js", (HttpContext context) => ServeAsset(context, webRoot, "app.js", "text/javascript; charset=utf-8"));

        endpoints.MapGet("/api/workbench/bootstrap", () =>
            Results.Ok(MigrationWorkbenchCatalog.Bootstrap(
                ApplicationInsightsConfigured(),
                modelConfigured,
                foundryAgentClient is not null,
                managedIdentityConfigured,
                entraAuthenticationConfigured)));

        endpoints.MapPost("/api/workbench/plan", (MigrationRunRequest request) =>
        {
            MigrationRunPlan plan = MigrationRunPlanner.Plan(request);
            return Results.Ok(new WorkbenchPlanResponse(
                plan,
                MigrationWorkbenchCatalog.Project(plan),
                MigrationWorkbenchCatalog.ExecutionBoundary));
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
            MapSourceAcquisition(endpoints, sourceWorkspaces);
        }
    }

    private static void MapSourceAcquisition(IEndpointRouteBuilder endpoints, SourceWorkspaceService workspaces)
    {
        endpoints.MapPost("/api/workbench/source/clone", async (
            HttpContext context,
            WorkbenchCloneRequest request,
            CancellationToken cancellationToken) =>
        {
            await StreamAsync(
                context,
                workspaces.CloneAsync(Owner(context), request.RepositoryUrl ?? string.Empty, request.Branch, cancellationToken),
                workspaces,
                Owner(context),
                cancellationToken);
        });

        endpoints.MapPost("/api/workbench/source/upload", async (
            HttpContext context,
            CancellationToken cancellationToken) =>
        {
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
                workspaces.ExtractAsync(Owner(context), stream, file.FileName, cancellationToken),
                workspaces,
                Owner(context),
                cancellationToken);
        });

        endpoints.MapDelete("/api/workbench/source/{workspaceId}", (HttpContext context, string workspaceId) =>
            workspaces.Release(Owner(context), workspaceId) ? Results.NoContent() : Results.NotFound());
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

        await foreach (SourceProgress step in progress.WithCancellation(cancellationToken))
        {
            object payload = step.Level == "done" && workspaces.Get(owner, step.Text) is SourceWorkspaceSummary summary
                ? new { level = "done", workspace = summary }
                : new { level = step.Level, text = step.Text };

            await context.Response.WriteAsync($"data: {JsonSerializer.Serialize(payload, JsonOptions)}\n\n", cancellationToken);
            await context.Response.Body.FlushAsync(cancellationToken);
        }
    }

    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Workspace ownership comes from the Container Apps authentication header only. It is never
    /// taken from the request body, so one signed-in user cannot address another user's copy.
    /// </summary>
    private static string Owner(HttpContext context) =>
        context.Request.Headers["X-MS-CLIENT-PRINCIPAL-ID"].FirstOrDefault() is { Length: > 0 } principal
            ? principal
            : "local-development";

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
