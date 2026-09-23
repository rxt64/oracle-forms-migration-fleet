using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using OracleFormsMigrationFleet.Fleet;
using System.Text.Json;

namespace OracleFormsMigrationFleet.Hosting;

/// <summary>
/// The authenticated disposition ledger surface.
///
/// A caller names a project, a run, a ledger, an entry, and a decision. Nothing else in a request body is
/// read, and a body that tries to carry evidence — a snapshot hash, a generated reference, a verification
/// result, an actor, a version of the source — is refused rather than quietly ignored, because silently
/// dropping a field a client believed it had set is how a console comes to show a result nothing produced.
/// </summary>
internal static class DispositionLedgerEndpoints
{
    /// <summary>Fields only the server may write. A request naming one is answered, not sanitized.</summary>
    private static readonly string[] s_serverOwnedFields =
    [
        "sourceSnapshotHash", "snapshotHash", "intermediateContentSha256", "contentSha256",
        "generatedRefs", "generated", "testRefs", "tests", "verification", "verified",
        "decidedByObjectId", "createdByObjectId", "actor", "objectId", "tenantId",
        "observedValue", "identity", "entries", "counts", "completion", "workspaceId",
        "decisionRevision", "decisionSequence",
    ];

    public static void Map(IEndpointRouteBuilder endpoints, IWorkbenchIdentityProvider identity)
    {
        endpoints.MapGet("/api/workbench/projects/{projectId}/disposition-ledgers", async (
            HttpContext context, string projectId, CancellationToken cancellationToken) =>
        {
            if (!WorkbenchEndpoints.TryActor(context, identity, out WorkbenchActor actor))
            {
                return Results.Unauthorized();
            }

            if (context.RequestServices.GetService<DispositionLedgerService>() is not { } ledgers)
            {
                return Unavailable();
            }

            PlatformResult<IReadOnlyList<DispositionLedgerSummary>> result =
                await ledgers.LedgersAsync(actor, projectId, cancellationToken);

            return result.Succeeded
                ? Results.Ok(new { ledgers = result.Value })
                : Results.Json(new { error = result.Error }, statusCode: result.Status);
        });

        endpoints.MapPost("/api/workbench/projects/{projectId}/disposition-ledgers", async (
            HttpContext context, string projectId, CancellationToken cancellationToken) =>
        {
            if (!WorkbenchEndpoints.TryActor(context, identity, out WorkbenchActor actor))
            {
                return Results.Unauthorized();
            }

            if (context.RequestServices.GetService<DispositionLedgerService>() is not { } ledgers)
            {
                return Unavailable();
            }

            if (await ReadBodyAsync(context, cancellationToken) is not { } body)
            {
                return Results.BadRequest(new { error = "The ledger request could not be read." });
            }

            if (Injected(body) is { } injected)
            {
                return Results.BadRequest(new { error = injected });
            }

            PlatformResult<DispositionLedgerView> result = await ledgers.IngestFromRunAsync(
                actor, projectId, WorkbenchEndpoints.ReadString(body, "runId"), cancellationToken);

            return result.Succeeded
                ? Results.Json(result.Value, statusCode: StatusCodes.Status201Created)
                : Results.Json(new { error = result.Error }, statusCode: result.Status);
        });

        endpoints.MapGet("/api/workbench/disposition-ledgers/{ledgerId}", async (
            HttpContext context, string ledgerId, CancellationToken cancellationToken) =>
        {
            if (!WorkbenchEndpoints.TryActor(context, identity, out WorkbenchActor actor))
            {
                return Results.Unauthorized();
            }

            if (context.RequestServices.GetService<DispositionLedgerService>() is not { } ledgers)
            {
                return Unavailable();
            }

            PlatformResult<DispositionLedgerView> result = await ledgers.ViewAsync(actor, ledgerId, cancellationToken);
            return result.Succeeded
                ? Results.Ok(result.Value)
                : Results.Json(new { error = result.Error }, statusCode: result.Status);
        });

        endpoints.MapGet("/api/workbench/disposition-ledgers/{ledgerId}/entries", async (
            HttpContext context,
            string ledgerId,
            string? module,
            string? group,
            bool? undecidedOnly,
            int? skip,
            int? take,
            CancellationToken cancellationToken) =>
        {
            if (!WorkbenchEndpoints.TryActor(context, identity, out WorkbenchActor actor))
            {
                return Results.Unauthorized();
            }

            if (context.RequestServices.GetService<DispositionLedgerService>() is not { } ledgers)
            {
                return Unavailable();
            }

            PlatformResult<DispositionEntryPage> result = await ledgers.EntriesAsync(
                actor, ledgerId, module, group, undecidedOnly ?? false,
                skip ?? 0, take ?? 50, cancellationToken);

            return result.Succeeded
                ? Results.Ok(result.Value)
                : Results.Json(new { error = result.Error }, statusCode: result.Status);
        });

        endpoints.MapPost("/api/workbench/disposition-ledgers/{ledgerId}/entries/{entryId}/decision", async (
            HttpContext context, string ledgerId, string entryId, CancellationToken cancellationToken) =>
        {
            if (!WorkbenchEndpoints.TryActor(context, identity, out WorkbenchActor actor))
            {
                return Results.Unauthorized();
            }

            if (context.RequestServices.GetService<DispositionLedgerService>() is not { } ledgers)
            {
                return Unavailable();
            }

            if (await ReadBodyAsync(context, cancellationToken) is not { } body)
            {
                return Results.BadRequest(new { error = "The disposition could not be read." });
            }

            if (Injected(body) is { } injected)
            {
                return Results.BadRequest(new { error = injected });
            }

            if (!Enum.TryParse(WorkbenchEndpoints.ReadString(body, "decision"), ignoreCase: true, out DispositionDecision decision) ||
                !Enum.IsDefined(decision))
            {
                return Results.BadRequest(new { error = "A disposition is Preserve, Transform, Retire, or Defer." });
            }

            if (ReadInt(body, "expectedVersion") is not int expectedVersion ||
                ReadInt(body, "mappingRuleVersion") is not int ruleVersion)
            {
                return Results.BadRequest(new { error = "A disposition cites a mapping rule version and the entry version it was read at." });
            }

            PlatformResult<DispositionLedgerEntry> result = await ledgers.DecideAsync(
                actor,
                ledgerId,
                entryId,
                new DispositionDecisionInput(
                    decision,
                    WorkbenchEndpoints.ReadString(body, "rationale"),
                    WorkbenchEndpoints.ReadString(body, "mappingRuleId"),
                    ruleVersion,
                    expectedVersion),
                cancellationToken);

            return result.Succeeded
                ? Results.Ok(result.Value)
                : Results.Json(new { error = result.Error }, statusCode: result.Status);
        });
    }

    /// <summary>The reason a body was refused, or null when it names only what a caller may name.</summary>
    internal static string? Injected(JsonElement body)
    {
        if (body.ValueKind != JsonValueKind.Object)
        {
            return "A request body is a JSON object.";
        }

        foreach (JsonProperty property in body.EnumerateObject())
        {
            if (s_serverOwnedFields.Contains(property.Name, StringComparer.OrdinalIgnoreCase))
            {
                return $"'{property.Name}' is recorded by the server from what actually ran, so a request naming it was refused.";
            }
        }

        return null;
    }

    private static IResult Unavailable() => Results.Json(
        new { error = "This deployment records no disposition ledger, because it has no platform state store." },
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

    private static int? ReadInt(JsonElement body, string property) =>
        body.TryGetProperty(property, out JsonElement value) &&
        value.ValueKind == JsonValueKind.Number &&
        value.TryGetInt32(out int parsed)
            ? parsed
            : null;
}
