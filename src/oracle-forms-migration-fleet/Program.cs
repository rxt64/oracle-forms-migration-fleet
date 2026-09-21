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
 * Execution adapters write generated artifacts inside private workspaces and may reach only the
 * host-configured sandbox PostgreSQL target after planner authorization. Source repositories are never
 * mutated, and production writes still require the production gate and its attestations.
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
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
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

// Authentication is named, not inferred. The environment is read the same way ASP.NET resolves it, and
// the host re-checks the answer against IHostEnvironment during startup before it serves anything.
string environmentName =
    Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")
    ?? Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT")
    ?? "Production";
bool isDevelopmentEnvironment = string.Equals(environmentName, "Development", StringComparison.OrdinalIgnoreCase);

WorkbenchAuthenticationOptions authenticationOptions =
    WorkbenchAuthenticationOptions.Resolve(isDevelopmentEnvironment, Environment.GetEnvironmentVariable);

IWorkbenchIdentityProvider identityProvider = authenticationOptions.Mode switch
{
    WorkbenchAuthenticationMode.ContainerApps => new ContainerAppsIdentityProvider(authenticationOptions),
    _ => new DevelopmentIdentityProvider(authenticationOptions.DefaultDevelopmentObjectId),
};

Console.WriteLine($"[INFO] Workbench authentication mode: {authenticationOptions.Mode}.");

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
builder.Services.AddSingleton<IApplicationBuildGateway, ProcessApplicationBuildGateway>();
builder.Services.AddSingleton<IApplicationTestGateway, ProcessApplicationTestGateway>();

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
builder.Services.AddSingleton(sourceWorkspaces);

// The sandbox database target is configured here, never by a caller, so a request can ask for a data
// migration but cannot choose where the rows land.
string? sandboxHost = Environment.GetEnvironmentVariable("SANDBOX_PGHOST");
string? sandboxUser = Environment.GetEnvironmentVariable("SANDBOX_PGUSER");
string sandboxDatabase = Environment.GetEnvironmentVariable("SANDBOX_PGDATABASE") ?? "postgres";
bool sandboxDatabaseConfigured = !string.IsNullOrWhiteSpace(sandboxHost) && !string.IsNullOrWhiteSpace(sandboxUser);
if (sandboxDatabaseConfigured)
{
    TokenCredential sandboxCredential = string.IsNullOrWhiteSpace(managedIdentityClientId)
        ? new AzureDeveloperCliCredential(new AzureDeveloperCliCredentialOptions { ProcessTimeout = TimeSpan.FromSeconds(30) })
        : new ManagedIdentityCredential(ManagedIdentityId.FromUserAssignedClientId(managedIdentityClientId));
    builder.Services.AddSingleton<IDataMigrationGateway>(new PostgresDataMigrationGateway(
        sandboxHost!,
        sandboxDatabase,
        sandboxUser!,
        sandboxCredential));
    builder.Services.AddSingleton<ITargetApplicationVerificationGateway>(
        new PostgresTargetApplicationVerificationGateway(
            sandboxHost!,
            sandboxDatabase,
            sandboxUser!,
            sandboxCredential));

    Console.WriteLine($"[INFO] Sandbox data migration target: {sandboxHost}. Authentication is Entra only.");
}

// The identity of the target, separated from the ability to write to it. A persisted target profile is
// compared against this before any grant is honoured, so an approval cannot name a database this process
// is not actually wired to.
ISandboxTargetBinding? sandboxBinding = sandboxDatabaseConfigured
    ? new ConfiguredSandboxTargetBinding(sandboxHost!, sandboxDatabase, sandboxUser!, CanWrite: true)
    : DevelopmentSandboxTarget();

if (sandboxBinding is not null)
{
    builder.Services.AddSingleton(sandboxBinding);
}

builder.Services.AddSingleton(
    PlatformTargetProfileEnvironment.Read(
        Environment.GetEnvironmentVariable,
        environmentName,
        requireSandboxCoordinates: sandboxBinding is not null));

// Platform state: PostgreSQL in a deployment, a durable local file in explicit Development mode.
// Production must not fall back to the file adapter — a per-replica file is not a shared record of who
// approved what, and treating it as one would make an approval disappear on the next revision.
IPlatformStateStore? platformStore = null;
ISandboxProjectBindingStore? sandboxProjects = null;
IMigrationRunStore? migrationRuns = null;
bool migratePlatformStore = false;

if (PlatformDatabaseOptions.TryRead(Environment.GetEnvironmentVariable, out PlatformDatabaseOptions? platformDatabase, out string platformError))
{
    TokenCredential platformCredential = string.IsNullOrWhiteSpace(managedIdentityClientId)
        ? new AzureDeveloperCliCredential(new AzureDeveloperCliCredentialOptions { ProcessTimeout = TimeSpan.FromSeconds(30) })
        : new ManagedIdentityCredential(ManagedIdentityId.FromUserAssignedClientId(managedIdentityClientId));
    platformStore = new PostgresPlatformStateStore(platformDatabase!, platformCredential);
    sandboxProjects = new PostgresSandboxProjectBindingStore(platformDatabase!, platformCredential);
    migrationRuns = new PostgresMigrationRunStore(platformDatabase!, platformCredential);
    migratePlatformStore = true;

    Console.WriteLine($"[INFO] Platform state store: PostgreSQL schema '{platformDatabase!.Schema}' on {platformDatabase.Host}.");
}
else if (authenticationOptions.Mode == WorkbenchAuthenticationMode.Development)
{
    string statePath = Environment.GetEnvironmentVariable("PLATFORM_STATE_FILE")
        ?? Path.Combine(Path.GetTempPath(), "ofm-platform-development", "platform-state.json");

    platformStore = new FilePlatformStateStore(statePath);
    migrationRuns = new FileMigrationRunStore(Path.ChangeExtension(statePath, ".runs.json"));
    Console.WriteLine($"[INFO] Platform state store: durable development file at {statePath}.");
}
else
{
    throw new InvalidOperationException(
        $"A deployed workbench requires a platform state store. {platformError}");
}

builder.Services.AddSingleton(platformStore);
builder.Services.AddSingleton(migrationRuns!);
builder.Services.AddSingleton(new PlatformAccessService(
    platformStore, sandboxBinding, sandboxProjects: sandboxProjects));
builder.Services.AddSingleton(new WorkbenchAuthorizationService(
    new PlatformAuthorizationStore(platformStore, sandboxBinding, sandboxProjects: sandboxProjects)));

bool migrate = migratePlatformStore;
IPlatformStateStore startupStore = platformStore;
builder.Services.AddSingleton<IHostedService>(provider => new PlatformStartupService(
    provider.GetRequiredService<IHostEnvironment>(),
    authenticationOptions,
    provider.GetRequiredService<ILogger<PlatformStartupService>>(),
    startupStore,
    migrate));
builder.Services.AddHostedService<MigrationRunWorker>();

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
        identityProvider,
        sandboxDatabaseConfigured,
        sourceWorkspaces);
});

var app = builder.Build();
app.Run();

/// <summary>
/// A declared development target, used only to exercise the approval chain offline.
///
/// It is opt-in through one environment variable that no deployment sets, it exists only in Development
/// mode, and it registers no gateway, so an approval made against it opens the gate and the adapter then
/// reports that there is nothing to write to. That is the honest shape: the authorization path is real
/// and the write capability is absent.
/// </summary>
ISandboxTargetBinding? DevelopmentSandboxTarget()
{
    if (!isDevelopmentEnvironment || authenticationOptions.Mode != WorkbenchAuthenticationMode.Development)
    {
        return null;
    }

    string? declared = Environment.GetEnvironmentVariable("WORKBENCH_DEV_SANDBOX_TARGET");
    if (string.IsNullOrWhiteSpace(declared))
    {
        return null;
    }

    string[] parts = declared.Split('|', StringSplitOptions.TrimEntries);
    return parts.Length == 3 && parts.All(part => part.Length > 0)
        ? new ConfiguredSandboxTargetBinding(parts[0], parts[1], parts[2], CanWrite: false)
        : null;
}