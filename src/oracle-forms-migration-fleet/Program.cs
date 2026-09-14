// Copyright (c) Microsoft. All rights reserved.

/*
 * Oracle Forms Migration Fleet — Agent Framework Responses agent for C#
 *
 * A hosted agent that assesses and plans Oracle Forms migrations to Azure SQL Database, Azure SQL
 * Managed Instance, or Azure Database for PostgreSQL. The externally hosted service is a single
 * process: a model-backed outer agent (Microsoft.Agents.AI) fronts a deterministic, offline
 * specialist fleet (see Fleet/) that the model calls as tools.
 *
 * The same process also serves the operator workbench: a dependency-free static console under
 * wwwroot at '/', backed by GET /api/workbench/bootstrap and POST /api/workbench/plan. The plan
 * endpoint calls the real MigrationRunPlanner; it does not simulate phase outcomes.
 *
 * Hosting is unchanged from the Foundry hosted-agent scaffold: AgentHost.CreateBuilder() from
 * Azure.AI.AgentServer.Core plus AddFoundryResponses/MapFoundryResponses from
 * Microsoft.Agents.AI.Foundry.Hosting provide the Responses protocol, port binding, health
 * probes, SSE lifecycle, and OpenTelemetry tracing.
 *
 * This service performs assessment and planning only. No execution adapter is implemented in this
 * repository, so it never executes a migration, never mutates a source or target system, and never
 * accepts conversion artifacts without a recorded human approval.
 *
 * Required environment variables:
 *   FOUNDRY_PROJECT_ENDPOINT        — Foundry project endpoint (auto-injected in hosted containers)
 *   AZURE_OPENAI_ENDPOINT           — Azure OpenAI account endpoint (injected from the azd environment)
 *   AZURE_AI_MODEL_DEPLOYMENT_NAME  — Model deployment name (declared in azure.yaml)
 *
 * Usage:
 *   dotnet run
 *
 *   Then open http://localhost:8088/ for the operator workbench, or call the agent directly:
 *
 *   curl -sS -X POST http://localhost:8088/responses \
 *     -H "Content-Type: application/json" \
 *     -d '{"input": "Assess engagement ENG-42 for the ORDERS application.", "stream": false}'
 */

using System.Text.Json.Serialization;
using Azure.AI.AgentServer.Core;
using Azure.AI.OpenAI;
using Azure.Core;
using Azure.Identity;
using DotNetEnv;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Foundry.Hosting;
using Microsoft.Extensions.AI;
using OracleFormsMigrationFleet.Fleet;
using OracleFormsMigrationFleet.Fleet.Agents;
using OracleFormsMigrationFleet.Fleet.Execution;
using OracleFormsMigrationFleet.Hosting;

// Load environment variables from a .env file if present (for local development).
Env.NoClobber().TraversePath().Load();

if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("APPLICATIONINSIGHTS_CONNECTION_STRING")))
{
    Console.Error.WriteLine(
        "[WARNING] APPLICATIONINSIGHTS_CONNECTION_STRING not set — traces will not be sent " +
        "to Application Insights. Set it to enable local telemetry. " +
        "(This variable is auto-injected in hosted Foundry containers — do not declare it in azure.yaml.)");
}

string? openAiEndpointValue = Environment.GetEnvironmentVariable("AZURE_OPENAI_ENDPOINT");
string? deployment = Environment.GetEnvironmentVariable("AZURE_AI_MODEL_DEPLOYMENT_NAME");
bool modelConfigured =
    Uri.TryCreate(openAiEndpointValue, UriKind.Absolute, out Uri? openAiEndpoint) &&
    !string.IsNullOrWhiteSpace(deployment);
string? managedIdentityClientId = Environment.GetEnvironmentVariable("AZURE_CLIENT_ID");
bool managedIdentityConfigured = !string.IsNullOrWhiteSpace(managedIdentityClientId);
bool entraAuthenticationConfigured = string.Equals(
    Environment.GetEnvironmentVariable("WORKBENCH_ENTRA_AUTH_ENABLED"),
    "true",
    StringComparison.OrdinalIgnoreCase);

FoundryAgentClient? remoteAgentClient = null;
if (FoundryAgentClient.TryParseEndpoint(
    Environment.GetEnvironmentVariable("FOUNDRY_AGENT_ENDPOINT"),
    out Uri? foundryAgentEndpoint))
{
    TokenCredential credential = string.IsNullOrWhiteSpace(managedIdentityClientId)
        ? new ChainedTokenCredential(
            new AzureDeveloperCliCredential(),
            new ManagedIdentityCredential(ManagedIdentityId.SystemAssigned))
        : new ChainedTokenCredential(
            new AzureDeveloperCliCredential(),
            new ManagedIdentityCredential(ManagedIdentityId.FromUserAssignedClientId(managedIdentityClientId)));

    remoteAgentClient = new FoundryAgentClient(
        new HttpClient { Timeout = TimeSpan.FromSeconds(90) },
        credential,
        foundryAgentEndpoint!);
}

// AgentHost.CreateBuilder() auto-configures:
//   - Kestrel on port 8088 (or the PORT environment variable)
//   - GET /readiness health probe
//   - OpenTelemetry traces and metrics
//   - x-platform-server response header
var builder = AgentHost.CreateBuilder(args);

// The workbench API speaks enums as strings so the static console never carries numeric enum values.
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

if (modelConfigured)
{
    // Azure Developer CLI authenticates local runs; the hosted service falls back to its managed identity.
    // The identity must be the one assigned to the container app: asking for a system-assigned token on a
    // user-assigned host fails at the token call, which surfaces only as a dead review agent.
    // The model handles intake dialogue and reporting; the deterministic fleet, exposed as tools, owns
    // every stage transition and the target platform recommendation.
    var credential = new ChainedTokenCredential(
        // The default process timeout is short enough that a cold azd invocation loses the race.
        new AzureDeveloperCliCredential(new AzureDeveloperCliCredentialOptions { ProcessTimeout = TimeSpan.FromSeconds(30) }),
        new ManagedIdentityCredential(managedIdentityConfigured
            ? ManagedIdentityId.FromUserAssignedClientId(managedIdentityClientId!)
            : ManagedIdentityId.SystemAssigned));

    IChatClient modelClient = new AzureOpenAIClient(openAiEndpoint!, credential)
        .GetChatClient(deployment!)
        .AsIChatClient();

    AIAgent agent = new SecretRejectingChatClient(modelClient)
        .AsAIAgent(
            instructions: FleetAgentInstructions.Build(),
            name: "oracle-forms-migration-fleet",
            description: "Assesses and plans Oracle Forms migrations to Azure SQL Database, Azure SQL Managed Instance, or Azure Database for PostgreSQL. Plans and gates the lifecycle; it never executes one.",
            tools: FleetTools.Create());

    builder.Services.AddFoundryResponses(agent);

    // Reviewing generated DDL is an adversarial reasoning task, so it gets its own deployment rather than
    // the conversational one. Falls back to the conversation model when no review deployment is set.
    string reviewDeployment = Environment.GetEnvironmentVariable("AZURE_AI_REVIEW_MODEL_DEPLOYMENT_NAME") is { Length: > 0 } configured
        ? configured
        : deployment!;

    IChatClient reviewModelClient = string.Equals(reviewDeployment, deployment, StringComparison.OrdinalIgnoreCase)
        ? modelClient
        : new AzureOpenAIClient(openAiEndpoint!, credential).GetChatClient(reviewDeployment).AsIChatClient();

    Console.WriteLine($"[INFO] Conversation model: {deployment}. Artifact review model: {reviewDeployment}.");

    builder.Services.AddSingleton<IArtifactReviewer>(
        new ModelArtifactReviewer(new SecretRejectingChatClient(reviewModelClient)));

    // The one genuinely multi-agent exchange: a critic raises statements it claims will fail and a
    // repairer proposes a fix, bounded by a step budget. Its output is a proposal written beside the
    // deterministic artifact, never over it.
    SqlRepairAgent sqlRepairAgent = new(new SecretRejectingChatClient(reviewModelClient));
    builder.Services.AddSingleton(new CritiqueRepairOrchestrator(
        new ReviewerAgent(new ModelArtifactReviewer(new SecretRejectingChatClient(reviewModelClient))),
        sqlRepairAgent,
        maxRounds: 2));
    builder.Services.AddSingleton(new ProgramUnitRepairLoop(sqlRepairAgent, maxAttempts: 2));
}
else
{
    Console.Error.WriteLine(
        "[WARNING] Azure OpenAI is not configured. The deterministic workbench will run, " +
        "but POST /responses will remain unavailable until AZURE_OPENAI_ENDPOINT and " +
        "AZURE_AI_MODEL_DEPLOYMENT_NAME are set.");
}

// Cloned and uploaded source lives in a per-session sandbox that is swept on a timer and on shutdown.
SourceWorkspaceService sourceWorkspaces = new(Environment.GetEnvironmentVariable("WORKBENCH_SOURCE_ROOT"));

// The sandbox database target is configured here, never by a caller, so a request can ask for a data
// migration but cannot choose where the rows land.
if (Environment.GetEnvironmentVariable("SANDBOX_PGHOST") is { Length: > 0 } sandboxHost &&
    Environment.GetEnvironmentVariable("SANDBOX_PGUSER") is { Length: > 0 } sandboxUser)
{
    builder.Services.AddSingleton<IDataMigrationGateway>(new PostgresDataMigrationGateway(
        sandboxHost,
        Environment.GetEnvironmentVariable("SANDBOX_PGDATABASE") ?? "postgres",
        sandboxUser,
        string.IsNullOrWhiteSpace(managedIdentityClientId)
            ? new AzureDeveloperCliCredential(new AzureDeveloperCliCredentialOptions { ProcessTimeout = TimeSpan.FromSeconds(30) })
            : new ManagedIdentityCredential(ManagedIdentityId.FromUserAssignedClientId(managedIdentityClientId))));

    Console.WriteLine($"[INFO] Sandbox data migration target: {sandboxHost}. Authentication is Entra only.");
}

builder.RegisterProtocol("responses", endpoints =>
{
    if (modelConfigured)
    {
        endpoints.MapFoundryResponses();
    }
    else
    {
        endpoints.MapPost("/responses", () => Results.Problem(
            title: "Foundry model is not configured",
            detail: "Set AZURE_OPENAI_ENDPOINT and AZURE_AI_MODEL_DEPLOYMENT_NAME to enable the agent endpoint.",
            statusCode: StatusCodes.Status503ServiceUnavailable));
    }

    WorkbenchEndpoints.Map(
        endpoints,
        modelConfigured,
        remoteAgentClient,
        managedIdentityConfigured,
        entraAuthenticationConfigured,
        sourceWorkspaces);
});

var app = builder.Build();
app.Run();