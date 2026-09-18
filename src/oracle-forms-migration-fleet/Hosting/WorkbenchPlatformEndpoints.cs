// Copyright (c) Microsoft. All rights reserved.

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using OracleFormsMigrationFleet.Fleet;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OracleFormsMigrationFleet.Hosting;

/// <summary>
/// The authenticated project and approval surface.
///
/// Everything here is addressed by identifier. A caller names a project, a target profile, and a
/// workspace they own; the server looks up membership, the immutable target profile, the source
/// snapshot it indexed itself, and the sanitized plan input, and stores the bindings it derived. No
/// hash, actor identity, role, decision, or timestamp is ever read from a request body.
/// </summary>
internal static class WorkbenchPlatformEndpoints
{
    private static readonly JsonSerializerOptions s_requestOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public static void Map(
        IEndpointRouteBuilder endpoints,
        IWorkbenchIdentityProvider identity,
        SourceWorkspaceService? workspaces)
    {
        endpoints.MapGet("/api/workbench/context", async (HttpContext context, CancellationToken cancellationToken) =>
        {
            if (!WorkbenchEndpoints.TryActor(context, identity, out WorkbenchActor actor))
            {
                return Results.Unauthorized();
            }

            PlatformAccessService? platform = context.RequestServices.GetService<PlatformAccessService>();

            List<object> projects = [];
            if (platform is not null)
            {
                foreach (PlatformProject project in await platform.ProjectsAsync(actor, cancellationToken))
                {
                    PlatformMembership? membership = await platform.Store
                        .GetMembershipAsync(actor.TenantId, project.ProjectId, actor.ObjectId, cancellationToken);

                    IReadOnlyList<PlatformTargetProfile> profiles = await platform.Store
                        .TargetProfilesAsync(actor.TenantId, project.ProjectId, cancellationToken);

                    IReadOnlyList<PlatformApproval> approvals = await platform.Store
                        .ApprovalsForProjectAsync(actor.TenantId, project.ProjectId, cancellationToken);

                    projects.Add(new
                    {
                        projectId = project.ProjectId,
                        name = project.Name,
                        createdUtc = project.CreatedUtc,
                        roles = membership?.Roles ?? [],
                        targetProfiles = profiles.Select(Describe).ToArray(),
                        approvals = approvals.Select(approval => Describe(approval, actor, membership)).ToArray(),
                    });
                }
            }

            return Results.Ok(new
            {
                authentication = new
                {
                    mode = identity.Mode.ToString(),
                    tenantId = actor.TenantId,
                    objectId = actor.ObjectId,
                    roles = actor.Roles,
                },
                persistence = new
                {
                    configured = platform is not null,
                    description = platform?.Store.Description
                        ?? "No platform state store is configured, so no project or approval can be persisted.",
                },
                sandbox = platform?.Sandbox is { } sandbox
                    ? new
                    {
                        configured = true,
                        endpointHost = sandbox.EndpointHost,
                        databaseName = sandbox.DatabaseName,
                        executionIdentity = sandbox.ExecutionIdentity,
                        canWrite = sandbox.CanWrite,
                    }
                    : new
                    {
                        configured = false,
                        endpointHost = string.Empty,
                        databaseName = string.Empty,
                        executionIdentity = string.Empty,
                        canWrite = false,
                    },
                productionApproval = new
                {
                    available = false,
                    reason = "Production approval is not available in this increment.",
                },
                projects,
            });
        });

        endpoints.MapPost("/api/workbench/projects", async (HttpContext context, CancellationToken cancellationToken) =>
        {
            if (!WorkbenchEndpoints.TryActor(context, identity, out WorkbenchActor actor))
            {
                return Results.Unauthorized();
            }

            if (context.RequestServices.GetService<PlatformAccessService>() is not { } platform)
            {
                return Unavailable();
            }

            JsonElement? body = await ReadBodyAsync(context, cancellationToken);
            PlatformResult<PlatformProject> created = await platform.CreateProjectAsync(
                actor,
                body is { } element ? WorkbenchEndpoints.ReadString(element, "name") : null,
                cancellationToken);

            if (!created.Succeeded)
            {
                return Results.Json(new { error = created.Error }, statusCode: created.Status);
            }

            // The target the server is wired to is recorded as the project's profile straight away.
            // A project with no target cannot request an approval, and a caller must not be able to
            // supply one, so the only honest moment to write it is here from configuration.
            PlatformResult<PlatformTargetProfile> profile = await platform.EnsureConfiguredTargetProfileAsync(
                actor,
                created.Value!.ProjectId,
                context.RequestServices.GetRequiredService<PlatformTargetProfileEnvironment>(),
                cancellationToken);

            return Results.Json(
                new
                {
                    projectId = created.Value.ProjectId,
                    name = created.Value.Name,
                    createdUtc = created.Value.CreatedUtc,
                    targetProfile = profile.Succeeded ? Describe(profile.Value!) : null,
                    targetProfileUnavailable = profile.Succeeded ? null : profile.Error,
                },
                statusCode: StatusCodes.Status201Created);
        });

        endpoints.MapPost("/api/workbench/projects/{projectId}/members", async (
            HttpContext context, string projectId, CancellationToken cancellationToken) =>
        {
            if (!WorkbenchEndpoints.TryActor(context, identity, out WorkbenchActor actor))
            {
                return Results.Unauthorized();
            }

            if (context.RequestServices.GetService<PlatformAccessService>() is not { } platform)
            {
                return Unavailable();
            }

            if (await ReadBodyAsync(context, cancellationToken) is not { } body)
            {
                return Results.BadRequest(new { error = "The membership request could not be read." });
            }

            string[]? roles = body.TryGetProperty("roles", out JsonElement declared) && declared.ValueKind == JsonValueKind.Array
                ? [.. declared.EnumerateArray()
                    .Where(role => role.ValueKind == JsonValueKind.String)
                    .Select(role => role.GetString()!)]
                : null;

            PlatformResult<PlatformMembership> added = await platform.AddMemberAsync(
                actor, projectId, WorkbenchEndpoints.ReadString(body, "objectId"), roles, cancellationToken);

            return added.Succeeded
                ? Results.Ok(new { objectId = added.Value!.ObjectId, roles = added.Value.Roles, version = added.Value.Version })
                : Results.Json(new { error = added.Error }, statusCode: added.Status);
        });

        endpoints.MapGet("/api/workbench/projects/{projectId}/approvals", async (
            HttpContext context, string projectId, CancellationToken cancellationToken) =>
        {
            if (!WorkbenchEndpoints.TryActor(context, identity, out WorkbenchActor actor))
            {
                return Results.Unauthorized();
            }

            if (context.RequestServices.GetService<PlatformAccessService>() is not { } platform)
            {
                return Unavailable();
            }

            PlatformResult<IReadOnlyList<PlatformApproval>> approvals =
                await platform.ApprovalsAsync(actor, projectId, cancellationToken);

            PlatformMembership? membership = approvals.Succeeded
                ? await platform.Store.GetMembershipAsync(actor.TenantId, projectId, actor.ObjectId, cancellationToken)
                : null;

            return approvals.Succeeded
                ? Results.Ok(new { approvals = approvals.Value!.Select(approval => Describe(approval, actor, membership)).ToArray() })
                : Results.Json(new { error = approvals.Error }, statusCode: approvals.Status);
        });

        endpoints.MapPost("/api/workbench/projects/{projectId}/approvals", async (
            HttpContext context, string projectId, CancellationToken cancellationToken) =>
        {
            if (!WorkbenchEndpoints.TryActor(context, identity, out WorkbenchActor actor))
            {
                return Results.Unauthorized();
            }

            if (context.RequestServices.GetService<PlatformAccessService>() is not { } platform)
            {
                return Unavailable();
            }

            if (workspaces is null)
            {
                return Results.Json(
                    new { error = "Source acquisition is not enabled on this host, so no source snapshot can be bound." },
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            if (await ReadBodyAsync(context, cancellationToken) is not { } body)
            {
                return Results.BadRequest(new { error = "The approval request could not be read." });
            }

            string? workspaceId = WorkbenchEndpoints.ReadString(body, "workspaceId");
            MigrationRunRequest? run = body.TryGetProperty("request", out JsonElement nested)
                ? Deserialize(nested)
                : null;

            if (run is null || string.IsNullOrWhiteSpace(workspaceId))
            {
                return Results.BadRequest(new { error = "An approval binds a workspace and a run request. Both are required." });
            }

            if (!TryReadScope(body, out WorkbenchMutationScope scope, out string scopeError))
            {
                return Results.BadRequest(new { error = scopeError });
            }

            if (WorkspacePath.Validate(run.SourceRoot, "Source folder") is string sourceError)
            {
                return Results.BadRequest(new { error = sourceError });
            }

            PlatformResult<PlatformMembership> membership = await platform.RequireMembershipAsync(
                actor, projectId, WorkbenchRoles.MigrationOperator, cancellationToken);
            if (!membership.Succeeded)
            {
                return Results.Json(new { error = membership.Error }, statusCode: membership.Status);
            }

            string workspaceOwner = PlatformIdentity.WorkspaceOwner(actor, projectId);
            if (!WorkbenchExecution.TryPrepareTrusted(
                workspaces,
                workspaceOwner,
                workspaceId,
                run,
                out WorkbenchExecution.WorkbenchTrustedPreparation? preparation,
                out int routeStatus,
                out string routeError))
            {
                return Results.Json(new { error = routeError }, statusCode: routeStatus);
            }

            // The bindings are the server's own view: the snapshot it hashed and the request after every
            // claim of authority was stripped out of it.
            WorkbenchRequestPreparation trusted = preparation!.Trusted;

            PlatformResult<PlatformApproval> requested = await platform.RequestAsync(
                actor,
                new PlatformApprovalRequestInput(
                    projectId,
                    WorkbenchEndpoints.ReadString(body, "targetProfileId") ?? "sandbox",
                    scope,
                    trusted.Request.EngagementId,
                    trusted.SourceSnapshotHash,
                    trusted.PlanInputHash,
                    TimeSpan.FromMinutes(ReadInt(body, "lifetimeMinutes") ?? 60),
                    WorkbenchEndpoints.ReadString(body, "notes")),
                cancellationToken);

            return requested.Succeeded
                ? Results.Json(Describe(requested.Value!, actor, membership.Value), statusCode: StatusCodes.Status201Created)
                : Results.Json(new { error = requested.Error }, statusCode: requested.Status);
        });

        endpoints.MapPost("/api/workbench/approvals/{approvalId}/decision", async (
            HttpContext context, string approvalId, CancellationToken cancellationToken) =>
        {
            if (!WorkbenchEndpoints.TryActor(context, identity, out WorkbenchActor actor))
            {
                return Results.Unauthorized();
            }

            if (context.RequestServices.GetService<PlatformAccessService>() is not { } platform)
            {
                return Unavailable();
            }

            if (await ReadBodyAsync(context, cancellationToken) is not { } body ||
                WorkbenchEndpoints.ReadString(body, "decision") is not { } decision ||
                ReadInt(body, "expectedVersion") is not int expectedVersion)
            {
                return Results.BadRequest(new { error = "A decision and the version it was read at are required." });
            }

            bool approve = string.Equals(decision, "Approved", StringComparison.OrdinalIgnoreCase);
            if (!approve && !string.Equals(decision, "Rejected", StringComparison.OrdinalIgnoreCase))
            {
                return Results.BadRequest(new { error = "A decision is either Approved or Rejected." });
            }

            PlatformResult<PlatformApproval> decided = await platform.DecideAsync(
                actor, approvalId, approve, expectedVersion, WorkbenchEndpoints.ReadString(body, "notes"), cancellationToken);

            PlatformMembership? membership = decided.Succeeded
                ? await platform.Store.GetMembershipAsync(actor.TenantId, decided.Value!.ProjectId, actor.ObjectId, cancellationToken)
                : null;

            return decided.Succeeded
                ? Results.Ok(Describe(decided.Value!, actor, membership))
                : Results.Json(new { error = decided.Error }, statusCode: decided.Status);
        });

        endpoints.MapPost("/api/workbench/approvals/{approvalId}/revoke", async (
            HttpContext context, string approvalId, CancellationToken cancellationToken) =>
        {
            if (!WorkbenchEndpoints.TryActor(context, identity, out WorkbenchActor actor))
            {
                return Results.Unauthorized();
            }

            if (context.RequestServices.GetService<PlatformAccessService>() is not { } platform)
            {
                return Unavailable();
            }

            if (await ReadBodyAsync(context, cancellationToken) is not { } body ||
                ReadInt(body, "expectedVersion") is not int expectedVersion)
            {
                return Results.BadRequest(new { error = "The version the approval was read at is required." });
            }

            PlatformResult<PlatformApproval> revoked = await platform.RevokeAsync(
                actor, approvalId, expectedVersion, WorkbenchEndpoints.ReadString(body, "notes"), cancellationToken);

            PlatformMembership? membership = revoked.Succeeded
                ? await platform.Store.GetMembershipAsync(actor.TenantId, revoked.Value!.ProjectId, actor.ObjectId, cancellationToken)
                : null;

            return revoked.Succeeded
                ? Results.Ok(Describe(revoked.Value!, actor, membership))
                : Results.Json(new { error = revoked.Error }, statusCode: revoked.Status);
        });
    }

    private static IResult Unavailable() => Results.Json(
        new { error = "This deployment has no platform state store, so projects and approvals cannot be persisted." },
        statusCode: StatusCodes.Status503ServiceUnavailable);

    private static async Task<JsonElement?> ReadBodyAsync(HttpContext context, CancellationToken cancellationToken)
    {
        try
        {
            using JsonDocument document =
                await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: cancellationToken);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static MigrationRunRequest? Deserialize(JsonElement element)
    {
        try
        {
            return element.Deserialize<MigrationRunRequest>(s_requestOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool TryReadScope(
        JsonElement body,
        out WorkbenchMutationScope scope,
        out string error)
    {
        string? declared = WorkbenchEndpoints.ReadString(body, "scope");
        if (declared is null)
        {
            scope = WorkbenchMutationScope.SandboxDatabaseWrite;
            error = string.Empty;
            return true;
        }

        if (Enum.TryParse(declared, ignoreCase: true, out scope) && Enum.IsDefined(scope))
        {
            error = string.Empty;
            return true;
        }

        error = "The requested approval scope is not supported.";
        return false;
    }

    private static int? ReadInt(JsonElement body, string property) =>
        body.TryGetProperty(property, out JsonElement value) &&
        value.ValueKind == JsonValueKind.Number &&
        value.TryGetInt32(out int parsed)
            ? parsed
            : null;

    private static object Describe(PlatformTargetProfile profile) => new
    {
        targetProfileId = profile.TargetProfileId,
        version = profile.Version,
        environmentName = profile.EnvironmentName,
        azureTenantId = profile.AzureTenantId,
        subscriptionId = profile.SubscriptionId,
        resourceGroup = profile.ResourceGroup,
        resourceId = profile.ResourceId,
        region = profile.Region,
        endpointHost = profile.EndpointHost,
        databaseName = profile.DatabaseName,
        schemaName = profile.SchemaName,
        executionIdentity = profile.ExecutionIdentity,
        stack = new { database = profile.StackDatabase, frontEnd = profile.StackFrontEnd, backEnd = profile.StackBackEnd },
        canonicalHash = profile.CanonicalHash,
        createdUtc = profile.CreatedUtc,
    };

    /// <summary>
    /// What the console is allowed to know about one approval, plus which actions this actor may take.
    /// The permissions are computed here rather than in the browser so a console that shows a button it
    /// should not have cannot turn that mistake into a decision.
    /// </summary>
    private static object Describe(
        PlatformApproval approval,
        WorkbenchActor actor,
        PlatformMembership? membership)
    {
        bool isRequester = string.Equals(approval.RequestedByObjectId, actor.ObjectId, StringComparison.OrdinalIgnoreCase);
        bool canDecide = approval.State == PlatformApprovalState.Requested
            && !isRequester
            && membership?.HasRole(WorkbenchRoles.SandboxApprover) == true;
        bool canRevoke = approval.State is PlatformApprovalState.Requested or PlatformApprovalState.Approved
            && (isRequester || membership?.HasRole(WorkbenchRoles.SandboxApprover) == true);

        return new
        {
            approvalId = approval.ApprovalId,
            projectId = approval.ProjectId,
            state = approval.State.ToString(),
            scope = approval.Scope.ToString(),
            requiredRole = approval.RequiredRole,
            engagementId = approval.EngagementId,
            requestedByObjectId = approval.RequestedByObjectId,
            requestedUtc = approval.RequestedUtc,
            decidedByObjectId = approval.DecidedByObjectId,
            decidedUtc = approval.DecidedUtc,
            revokedByObjectId = approval.RevokedByObjectId,
            revokedUtc = approval.RevokedUtc,
            expiresUtc = approval.ExpiresUtc,
            requestNotes = approval.RequestNotes,
            decisionNotes = approval.DecisionNotes,
            revocationNotes = approval.RevocationNotes,
            targetProfileId = approval.TargetProfileId,
            targetProfileVersion = approval.TargetProfileVersion,
            version = approval.Version,
            isRequester,
            canDecide,
            canRevoke,
            isEffective = approval.IsEffective(DateTimeOffset.UtcNow),
        };
    }
}
