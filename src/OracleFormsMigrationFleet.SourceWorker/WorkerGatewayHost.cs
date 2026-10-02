using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.IdentityModel.Tokens;
using OracleFormsMigrationFleet.SourceWorker.Gateway;

namespace OracleFormsMigrationFleet.SourceWorker;

/// <summary>A built host, or the reason the gateway refused to listen. Exactly one of the two is set.</summary>
public sealed record GatewayHostBuild(WebApplication? Application, string? Failure)
{
    public static GatewayHostBuild Refused(string failure) => new(null, failure);
}

/// <summary>
/// Overrides a deployment never sets. The runner is replaced only so orchestration can be tested without
/// spawning a native worker, <see cref="TestSigningKeys"/> exists so authentication can be tested
/// against real token validation without reaching Entra, and <see cref="Certificate"/> lets the https
/// path be exercised without installing anything in a machine store. All are null in every production
/// path, and when they are null the host uses the standard JWT bearer handler pointed at the configured
/// tenant and the certificate the machine store selector approved.
/// </summary>
public sealed record GatewayHostDependencies(
    IGatewayExtractionRunner? Runner = null,
    Func<DateTimeOffset>? Clock = null,
    IReadOnlyList<SecurityKey>? TestSigningKeys = null,
    IReadOnlyList<string>? TestValidIssuers = null,
    IGatewaySchemaExtractionRunner? SchemaRunner = null,
    X509Certificate2? Certificate = null,
    IGatewayProbeRunner? ProbeRunner = null);

/// <summary>
/// The source gateway server.
///
/// This is the only component in the fleet permitted to run a native Forms worker, and it is deliberately
/// small: authenticate the caller with Entra, check it is an allowlisted application in the configured
/// tenant, look the requested source environment up in this server's own registry, run one bounded child
/// process, and return the artifact bytes.
///
/// What it will not do, by construction rather than by policy text:
///
/// * It takes no path, command line, library name, URL or connect string from a caller. Every location is
///   read from the registry this server was configured with.
/// * It accepts no identity from a header. A request is authenticated by a signed Entra token this process
///   validates itself, or it is refused; there is no anonymous write and no trusted proxy claim.
/// * It listens on https unless an operator explicitly asked for a loopback development listener.
/// * It never returns a filesystem path. The worker's artifact is validated against the registered output
///   root, hashed here, and inlined as bytes.
/// * It issues no attestation, approval or normalization claim. It reports what the worker observed.
/// </summary>
public static class WorkerGatewayHost
{
    public const string CallerPolicy = "ofm-source-gateway-caller";

    /// <summary>
    /// Builds the host, or refuses with the reason it will not listen.
    ///
    /// Refusal is a first-class result rather than an exception because every reason here is an operator
    /// mistake — a certificate that is not in the store, a pin nothing matches, a store this account
    /// cannot read — and the service has to report it as a startup message, not a stack trace.
    /// </summary>
    public static GatewayHostBuild TryBuild(GatewayOptions options, GatewayHostDependencies? dependencies = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        X509Certificate2? certificate = null;
        if (options.Tls is { } tls)
        {
            GatewayCertificateSelection selection = dependencies?.Certificate is { } supplied
                ? new GatewayCertificateSelection(supplied, null)
                : GatewayServerCertificate.SelectFromLocalMachineStore(tls, (dependencies?.Clock ?? (() => DateTimeOffset.UtcNow))());

            if (selection.Certificate is null)
            {
                return GatewayHostBuild.Refused(selection.Failure!);
            }

            certificate = selection.Certificate;
        }

        return new GatewayHostBuild(Build(options, dependencies, certificate), null);
    }

    public static WebApplication Build(GatewayOptions options, GatewayHostDependencies? dependencies = null) =>
        Build(options, dependencies, certificate: null);

    private static WebApplication Build(
        GatewayOptions options,
        GatewayHostDependencies? dependencies,
        X509Certificate2? certificate)
    {
        ArgumentNullException.ThrowIfNull(options);

        // The Service Control Manager starts a service with the system directory as its working directory,
        // so the content root is pinned to the directory the approved binary was installed into. Without
        // this the host would resolve configuration and static content relative to System32.
        WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ContentRootPath = AppContext.BaseDirectory,
            ApplicationName = typeof(WorkerGatewayHost).Assembly.GetName().Name,
        });

        // Harmless when the process is a console: the lifetime only engages when the SCM started it.
        builder.Services.AddWindowsService(service => service.ServiceName = GatewayOptions.ServiceName);

        builder.WebHost.UseUrls(options.BindUrl.ToString());
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            // A request carries identifiers and digests. Nothing legitimate approaches these limits, so
            // they are set where an oversized body is refused by the server rather than by the handler.
            kestrel.Limits.MaxRequestBodySize = GatewayProtocol.MaxRequestBytes;
            kestrel.Limits.MaxRequestHeadersTotalSize = 16 * 1024;
            kestrel.Limits.MaxRequestLineSize = 4 * 1024;
            kestrel.Limits.MaxConcurrentConnections = 64;
            kestrel.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(15);
            kestrel.AddServerHeader = false;

            if (certificate is not null)
            {
                // The one certificate the selector approved. No developer certificate, no file fallback,
                // and no SNI callback that could answer for a name an operator never approved.
                kestrel.ConfigureHttpsDefaults(https =>
                {
                    https.ServerCertificate = certificate;
                    https.SslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13;
                    https.ClientCertificateMode = ClientCertificateMode.NoCertificate;
                });
            }
        });

        IGatewayExtractionRunner runner = dependencies?.Runner ?? new ChildProcessExtractionRunner(options);
        IGatewaySchemaExtractionRunner schemaRunner =
            dependencies?.SchemaRunner ?? new ChildProcessSchemaExtractionRunner(options);
        IGatewayProbeRunner probeRunner = dependencies?.ProbeRunner ?? new ChildProcessProbeRunner(options);
        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton(
            new GatewayExtractionCoordinator(options, runner, dependencies?.Clock, schemaRunner, probeRunner));

        builder.Services
            .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(jwt => ConfigureBearer(jwt, options, dependencies));

        builder.Services.AddSingleton<IAuthorizationHandler, GatewayCallerHandler>();
        builder.Services
            .AddAuthorizationBuilder()
            .AddPolicy(CallerPolicy, policy => policy
                .RequireAuthenticatedUser()
                .AddRequirements(new GatewayCallerRequirement(options.TenantId, options.AllowedCallerAppIds)));

        WebApplication app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();

        app.MapPost(GatewayProtocol.FormsModuleExtractPath, ExtractFormsModuleAsync).RequireAuthorization(CallerPolicy);
        app.MapPost(GatewayProtocol.OracleSchemaExtractPath, ExtractOracleSchemaAsync).RequireAuthorization(CallerPolicy);
        app.MapPost(GatewayProtocol.SourceEnvironmentProbePath, ProbeSourceEnvironmentAsync).RequireAuthorization(CallerPolicy);

        return app;
    }

    /// <summary>
    /// Standard bearer validation against the configured tenant. Issuer, audience, lifetime and signature
    /// are all validated, inbound claim names are left alone so <c>tid</c>, <c>appid</c> and <c>azp</c>
    /// arrive as Entra wrote them, and signing keys come from tenant metadata over https.
    /// </summary>
    private static void ConfigureBearer(JwtBearerOptions jwt, GatewayOptions options, GatewayHostDependencies? dependencies)
    {
        jwt.MapInboundClaims = false;
        jwt.RequireHttpsMetadata = true;
        jwt.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuers = dependencies?.TestValidIssuers ?? options.ValidIssuers,
            ValidateAudience = true,
            ValidAudiences = options.ValidAudiences,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            RequireSignedTokens = true,
            RequireExpirationTime = true,
            ClockSkew = TimeSpan.FromMinutes(2),
        };

        if (dependencies?.TestSigningKeys is { Count: > 0 } keys)
        {
            // Keys supplied in process: the same validation runs, it simply does not fetch tenant metadata.
            jwt.TokenValidationParameters.IssuerSigningKeys = keys;
            return;
        }

        jwt.Authority = options.Authority;
        jwt.Audience = options.Audience;
    }

    private static async Task<IResult> ExtractFormsModuleAsync(HttpContext context, GatewayExtractionCoordinator coordinator)
    {
        (byte[]? body, IResult? failure) = await ReadBodyAsync(context).ConfigureAwait(false);
        if (body is null)
        {
            return failure!;
        }

        GatewayFormsModuleRequest? request;
        try
        {
            request = JsonSerializer.Deserialize<GatewayFormsModuleRequest>(body, GatewayProtocol.Json);
        }
        catch (JsonException)
        {
            return Results.StatusCode(StatusCodes.Status400BadRequest);
        }

        if (request is null ||
            request.SourceEnvironmentId is null ||
            request.ExpectedFormsRelease is null ||
            request.ProfileHash is null ||
            request.ModuleAlias is null ||
            request.ExpectedContentSha256 is null)
        {
            return Results.StatusCode(StatusCodes.Status400BadRequest);
        }

        GatewayFormsModuleOutcome outcome = await coordinator
            .ExtractFormsModuleAsync(Caller(context), request, context.RequestAborted)
            .ConfigureAwait(false);

        return outcome.Response is null
            ? Results.StatusCode(outcome.StatusCode)
            : Results.Json(outcome.Response, GatewayProtocol.Json, statusCode: outcome.StatusCode);
    }

    private static async Task<IResult> ProbeSourceEnvironmentAsync(HttpContext context, GatewayExtractionCoordinator coordinator)
    {
        (byte[]? body, IResult? failure) = await ReadBodyAsync(context).ConfigureAwait(false);
        if (body is null)
        {
            return failure!;
        }

        GatewayProbeRequest? request;
        try
        {
            request = JsonSerializer.Deserialize<GatewayProbeRequest>(body, GatewayProtocol.Json);
        }
        catch (JsonException)
        {
            return Results.StatusCode(StatusCodes.Status400BadRequest);
        }

        if (request is null ||
            request.SourceEnvironmentId is null ||
            request.ExpectedFormsRelease is null ||
            request.ExpectedDatabaseRelease is null ||
            request.ProfileHash is null)
        {
            return Results.StatusCode(StatusCodes.Status400BadRequest);
        }

        GatewayProbeOutcome outcome = await coordinator
            .ProbeSourceEnvironmentAsync(Caller(context), request, context.RequestAborted)
            .ConfigureAwait(false);

        return outcome.Response is null
            ? Results.StatusCode(outcome.StatusCode)
            : Results.Json(outcome.Response, GatewayProtocol.Json, statusCode: outcome.StatusCode);
    }

    private static async Task<IResult> ExtractOracleSchemaAsync(HttpContext context, GatewayExtractionCoordinator coordinator)
    {
        (byte[]? body, IResult? failure) = await ReadBodyAsync(context).ConfigureAwait(false);
        if (body is null)
        {
            return failure!;
        }

        GatewayOracleSchemaRequest? request;
        try
        {
            request = JsonSerializer.Deserialize<GatewayOracleSchemaRequest>(body, GatewayProtocol.Json);
        }
        catch (JsonException)
        {
            return Results.StatusCode(StatusCodes.Status400BadRequest);
        }

        if (request is null || request.SourceEnvironmentId is null || request.ProfileHash is null)
        {
            return Results.StatusCode(StatusCodes.Status400BadRequest);
        }

        GatewayOracleSchemaOutcome outcome = await coordinator
            .ExtractOracleSchemaAsync(Caller(context), request, context.RequestAborted)
            .ConfigureAwait(false);

        return outcome.Response is null
            ? Results.StatusCode(outcome.StatusCode)
            : Results.Json(outcome.Response, GatewayProtocol.Json, statusCode: outcome.StatusCode);
    }

    /// <summary>
    /// The caller as the validated token describes it, never as the body claims.
    ///
    /// The policy has already established that both claims are present and allowlisted, so this only
    /// carries them into admission, where the tenant is compared with the tenant the request's own
    /// authorization scope names. Reading them from the body instead would let a caller that holds a
    /// valid token for one tenant assert a scope in another.
    /// </summary>
    private static GatewayCallerIdentity Caller(HttpContext context) =>
        new(
            context.User.FindFirst("tid")?.Value ?? string.Empty,
            context.User.FindFirst("azp")?.Value ?? context.User.FindFirst("appid")?.Value ?? string.Empty);

    private static async Task<(byte[]? Body, IResult? Failure)> ReadBodyAsync(HttpContext context)
    {
        string? mediaType = context.Request.ContentType?.Split(';')[0].Trim();
        if (!string.Equals(mediaType, "application/json", StringComparison.OrdinalIgnoreCase))
        {
            return (null, Results.StatusCode(StatusCodes.Status415UnsupportedMediaType));
        }

        try
        {
            byte[] body = await GatewayStream
                .ReadBoundedAsync(context.Request.Body, GatewayProtocol.MaxRequestBytes, context.RequestAborted)
                .ConfigureAwait(false);

            return body.Length is 0
                ? (null, Results.StatusCode(StatusCodes.Status400BadRequest))
                : (body, null);
        }
        catch (InvalidDataException)
        {
            return (null, Results.StatusCode(StatusCodes.Status413PayloadTooLarge));
        }
        catch (Microsoft.AspNetCore.Http.BadHttpRequestException)
        {
            return (null, Results.StatusCode(StatusCodes.Status413PayloadTooLarge));
        }
    }
}

/// <summary>
/// The caller must be an allowlisted application in the configured tenant.
///
/// Authentication proves a token is genuine; this proves it belongs to the workbench identity an operator
/// named. Both <c>azp</c> and <c>appid</c> are read because v2.0 and v1.0 tokens spell the calling
/// application differently, and a token carrying neither is refused rather than treated as a user.
/// </summary>
internal sealed record GatewayCallerRequirement(string TenantId, IReadOnlyList<string> AllowedCallerAppIds)
    : IAuthorizationRequirement;

internal sealed class GatewayCallerHandler : AuthorizationHandler<GatewayCallerRequirement>
{
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        GatewayCallerRequirement requirement)
    {
        string? tenant = context.User.FindFirst("tid")?.Value;
        string? caller = context.User.FindFirst("azp")?.Value ?? context.User.FindFirst("appid")?.Value;

        if (tenant is { Length: > 0 } &&
            caller is { Length: > 0 } &&
            string.Equals(tenant, requirement.TenantId, StringComparison.OrdinalIgnoreCase) &&
            requirement.AllowedCallerAppIds.Any(allowed => string.Equals(allowed, caller, StringComparison.OrdinalIgnoreCase)))
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}
