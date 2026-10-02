// Copyright (c) Microsoft. All rights reserved.

using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Azure.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using OracleFormsMigrationFleet.Fleet.Execution;

namespace OracleFormsMigrationFleet.Hosting;

/// <summary>
/// Everything the host must be told before it will talk to a source gateway.
///
/// None of it is a credential and none of it comes from a caller. The authority is one fixed https
/// origin; the scope names the audience a managed-identity token is requested for. A host that cannot
/// answer both questions has no gateway, and the honest result is no registration at all, which leaves
/// the extraction capabilities <see cref="SourceEnvironmentProbeStatus.BlockedPrerequisite"/> exactly as
/// they are today.
/// </summary>
public sealed record SourceGatewayOptions
{
    /// <summary>Origin of the gateway. Path, query, and fragment are refused: the protocol fixes the path.</summary>
    public required Uri Authority { get; init; }

    /// <summary>Entra scope the runtime identity requests a token for, for example <c>api://&lt;id&gt;/.default</c>.</summary>
    public required string TokenScope { get; init; }

    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromMinutes(4);

    public static bool TryRead(
        IConfiguration configuration,
        out SourceGatewayOptions? options,
        out IReadOnlyList<string> missing)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        options = null;
        List<string> absent = [];

        string? authority = configuration["SourceGateway:Authority"];
        string? scope = configuration["SourceGateway:TokenScope"];

        if (string.IsNullOrWhiteSpace(authority)) absent.Add("SourceGateway:Authority");
        if (string.IsNullOrWhiteSpace(scope)) absent.Add("SourceGateway:TokenScope");

        if (absent.Count > 0)
        {
            missing = absent;
            return false;
        }

        if (!Uri.TryCreate(authority!.Trim(), UriKind.Absolute, out Uri? parsed) ||
            !string.Equals(parsed.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal) ||
            !string.IsNullOrEmpty(parsed.UserInfo) ||
            !string.IsNullOrEmpty(parsed.Query) ||
            !string.IsNullOrEmpty(parsed.Fragment) ||
            parsed.AbsolutePath != "/")
        {
            absent.Add("SourceGateway:Authority must be an https origin with no path, query, fragment, or sign-in details.");
        }

        // A scope is an audience, never a token. Anything that looks like one is a misconfiguration
        // serious enough to refuse the whole registration.
        if (scope!.Trim() is not { Length: > 0 and <= 256 } trimmedScope ||
            trimmedScope.Any(char.IsWhiteSpace) ||
            Fleet.FleetGuardrails.ContainsPotentialSecret(trimmedScope))
        {
            absent.Add("SourceGateway:TokenScope must be a single Entra scope and must not carry credential material.");
        }

        if (absent.Count > 0)
        {
            missing = absent;
            return false;
        }

        options = new SourceGatewayOptions { Authority = parsed!, TokenScope = scope.Trim() };
        missing = [];
        return true;
    }
}

/// <summary>
/// One call to the gateway: either a document to validate, or the reason there is none.
///
/// <paramref name="DiagnosticCode"/> is the stable <see cref="SourceGatewayDiagnosticCode"/> that was
/// also logged, so an operator reading the refusal text and an engineer reading telemetry are looking at
/// the same event. It is a code and never a detail: it adds no information a caller could not already
/// infer from the prose.
/// </summary>
public sealed record SourceGatewayCall(
    SourceGatewayFormsModuleResponse? Response,
    string? TransportError,
    string? DiagnosticCode = null);

/// <summary>One schema call: either a document to validate, or the reason there is none.</summary>
public sealed record SourceGatewaySchemaCall(
    SourceGatewayOracleSchemaResponse? Response,
    string? TransportError,
    string? DiagnosticCode = null);

/// <summary>One probe call: either a document to validate, or the reason there is none.</summary>
public sealed record SourceGatewayProbeCall(
    SourceGatewayProbeResponse? Response,
    string? TransportError,
    string? DiagnosticCode = null);

public interface ISourceGatewayClient
{
    /// <summary>Non-secret description of where extraction would go, safe to show an operator.</summary>
    string Description { get; }

    Task<SourceGatewayCall> ExtractFormsModuleAsync(
        SourceGatewayFormsModuleRequest request,
        CancellationToken cancellationToken);

    Task<SourceGatewaySchemaCall> ExtractOracleSchemaAsync(
        SourceGatewayOracleSchemaRequest request,
        CancellationToken cancellationToken);

    /// <summary>
    /// Asks the gateway what its own host can do for one registered source environment. It is the only
    /// gateway call that opens nothing, so it is the one an operator may make before approving anything.
    /// </summary>
    Task<SourceGatewayProbeCall> ProbeSourceEnvironmentAsync(
        SourceGatewayProbeRequest request,
        CancellationToken cancellationToken);
}

/// <summary>
/// The authenticated client for the separately deployed source gateway.
///
/// NATIVE WORKERS DO NOT RUN IN THIS PROCESS. The web host starts no process, loads no Forms library,
/// and opens no Oracle connection; it posts one bounded document to one configured origin and reads one
/// bounded document back. That separation is the whole reason this type exists, and any change that puts
/// a native invocation behind it removes the isolation it was built for.
///
/// The caller never influences the destination. The origin comes from configuration, the path comes from
/// the protocol, redirects are not followed and are refused if offered, and the response's own origin is
/// re-checked after the fact. A gateway that answers with a redirect to somewhere else therefore cannot
/// move this host's credential or its trust to that somewhere else.
/// </summary>
public sealed class HttpSourceGatewayClient(
    HttpClient client,
    TokenCredential credential,
    SourceGatewayOptions options,
    Func<DateTimeOffset>? clock = null,
    ILogger<HttpSourceGatewayClient>? logger = null) : ISourceGatewayClient
{
    private readonly Func<DateTimeOffset> _clock = clock ?? (() => DateTimeOffset.UtcNow);

    public string Description =>
        $"Source gateway at {options.Authority.IdnHost}, called with this service's own managed identity.";

    public async Task<SourceGatewayCall> ExtractFormsModuleAsync(
        SourceGatewayFormsModuleRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!SourceGatewayAuthorizationScope.IsWellFormed(request.Scope))
        {
            Failed(SourceGatewayOperation.FormsModuleExtract, SourceGatewayDiagnosticCode.MissingScope);
            return new SourceGatewayCall(null, MissingScope, SourceGatewayDiagnosticCode.MissingScope);
        }

        (SourceGatewayFormsModuleResponse? response, string? error, string? code) =
            await PostAsync<SourceGatewayFormsModuleRequest, SourceGatewayFormsModuleResponse>(
                SourceGatewayOperation.FormsModuleExtract,
                SourceGatewayProtocol.FormsModuleExtractPath,
                request,
                cancellationToken).ConfigureAwait(false);

        return new SourceGatewayCall(response, error, code);
    }

    /// <summary>
    /// Why no call is made when the caller could not be resolved to a tenant and a project.
    ///
    /// The gateway's registry binds each source environment to the tenant and projects an operator
    /// approved, and the token this client sends proves only the application and the tenant. Sending a
    /// scope-less request would therefore ask the gateway to decide who the call is for, which is exactly
    /// the deputy problem the scope exists to close.
    /// </summary>
    internal const string MissingScope =
        "This service did not derive a server-owned authorization scope for the source gateway call, so nothing was " +
        "contacted and nothing was written.";

    /// <summary>
    /// The Oracle half. It is the same authenticated, non-redirecting, origin-rechecked, byte-bounded
    /// call as the Forms half: the request carries identifiers, a profile digest and the schema names the
    /// SERVER stored on the profile, and never a host, a DSN, a user or a password. The gateway holds its
    /// own connection identity, which is the reason no credential ever has to leave this process.
    /// </summary>
    public async Task<SourceGatewaySchemaCall> ExtractOracleSchemaAsync(
        SourceGatewayOracleSchemaRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!SourceGatewayAuthorizationScope.IsWellFormed(request.Scope))
        {
            Failed(SourceGatewayOperation.OracleSchemaExtract, SourceGatewayDiagnosticCode.MissingScope);
            return new SourceGatewaySchemaCall(null, MissingScope, SourceGatewayDiagnosticCode.MissingScope);
        }

        (SourceGatewayOracleSchemaResponse? response, string? error, string? code) =
            await PostAsync<SourceGatewayOracleSchemaRequest, SourceGatewayOracleSchemaResponse>(
                SourceGatewayOperation.OracleSchemaExtract,
                SourceGatewayProtocol.OracleSchemaExtractPath,
                request,
                cancellationToken).ConfigureAwait(false);

        return new SourceGatewaySchemaCall(response, error, code);
    }

    /// <summary>
    /// The probe half. It is the same authenticated, non-redirecting, origin-rechecked, byte-bounded call
    /// as the two extraction halves, and it reaches the same bounded protocol: identifiers, the declared
    /// releases, the immutable profile digest and the server-derived scope. Nothing a caller supplied and
    /// no credential travels, and the gateway resolves every location from its own registry.
    /// </summary>
    public async Task<SourceGatewayProbeCall> ProbeSourceEnvironmentAsync(
        SourceGatewayProbeRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!SourceGatewayAuthorizationScope.IsWellFormed(request.Scope))
        {
            Failed(SourceGatewayOperation.SourceEnvironmentProbe, SourceGatewayDiagnosticCode.MissingScope);
            return new SourceGatewayProbeCall(null, MissingScope, SourceGatewayDiagnosticCode.MissingScope);
        }

        (SourceGatewayProbeResponse? response, string? error, string? code) =
            await PostAsync<SourceGatewayProbeRequest, SourceGatewayProbeResponse>(
                SourceGatewayOperation.SourceEnvironmentProbe,
                SourceGatewayProtocol.SourceEnvironmentProbePath,
                request,
                cancellationToken).ConfigureAwait(false);

        return new SourceGatewayProbeCall(response, error, code);
    }

    /// <summary>
    /// Emits one failure event. The caller supplies a code and, at most, a status and an exception; this
    /// is the only path from a transport fault to telemetry, so there is no second place that could
    /// start logging a remote message or a URL.
    /// </summary>
    private string Failed(SourceGatewayOperation operation, string code, int statusCode = 0, Exception? fault = null)
    {
        if (logger is not null)
        {
            SourceGatewayDiagnostics.CallFailed(logger, operation, code, options.Authority.IdnHost, statusCode, fault);
        }

        return code;
    }

    private async Task<(TResponse? Response, string? TransportError, string? DiagnosticCode)> PostAsync<TRequest, TResponse>(
        SourceGatewayOperation operation,
        string path,
        TRequest request,
        CancellationToken cancellationToken)
        where TResponse : class
    {
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(request, SourceGatewayProtocol.Json);
        if (body.Length > SourceGatewayProtocol.MaxRequestBytes)
        {
            return (null, "The extraction request exceeded the protocol's request size limit.",
                Failed(operation, SourceGatewayDiagnosticCode.RequestTooLarge));
        }

        Uri destination = new(options.Authority, path);

        string token;
        try
        {
            AccessToken issued = await credential
                .GetTokenAsync(new TokenRequestContext([options.TokenScope]), cancellationToken)
                .ConfigureAwait(false);
            token = issued.Token;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return (null, "This service could not obtain a token for the configured source gateway scope.",
                Failed(operation, SourceGatewayDiagnosticCode.TokenUnavailable, fault: exception));
        }

        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(options.RequestTimeout);

        using HttpRequestMessage message = new(HttpMethod.Post, destination)
        {
            Content = new ByteArrayContent(body)
            {
                Headers = { ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" } },
            },
        };
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        HttpResponseMessage response;
        try
        {
            response = await client
                .SendAsync(message, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            return (null, "The source gateway did not answer within the configured timeout.",
                Failed(operation, SourceGatewayDiagnosticCode.Timeout, fault: exception));
        }
        catch (HttpRequestException exception)
        {
            return (null, "The source gateway could not be reached.",
                Failed(operation, SourceGatewayDiagnosticCode.Unreachable, fault: exception));
        }

        using (response)
        {
            if ((int)response.StatusCode is >= 300 and < 400)
            {
                return (null, "The source gateway answered with a redirect, which is never followed.",
                    Failed(operation, SourceGatewayDiagnosticCode.Redirected, (int)response.StatusCode));
            }

            if (response.StatusCode != HttpStatusCode.OK)
            {
                return (null, $"The source gateway answered {(int)response.StatusCode}.",
                    Failed(operation, SourceGatewayDiagnosticCode.UnexpectedStatus, (int)response.StatusCode));
            }

            if (response.RequestMessage?.RequestUri is { } answered &&
                (!string.Equals(answered.IdnHost, options.Authority.IdnHost, StringComparison.OrdinalIgnoreCase) ||
                 answered.Port != options.Authority.Port ||
                 !string.Equals(answered.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal)))
            {
                return (null, "The source gateway response came from an origin other than the configured one.",
                    Failed(operation, SourceGatewayDiagnosticCode.OriginMismatch));
            }

            if (!string.Equals(response.Content.Headers.ContentType?.MediaType, "application/json", StringComparison.OrdinalIgnoreCase))
            {
                return (null, "The source gateway answered with a media type this protocol does not accept.",
                    Failed(operation, SourceGatewayDiagnosticCode.UnacceptableMediaType));
            }

            if (response.Content.Headers.ContentLength is > SourceGatewayProtocol.MaxResponseBytes)
            {
                return (null, "The source gateway response is larger than the protocol allows.",
                    Failed(operation, SourceGatewayDiagnosticCode.ResponseTooLarge));
            }

            byte[] payload;
            try
            {
                await using Stream stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
                payload = await ReadBoundedAsync(stream, SourceGatewayProtocol.MaxResponseBytes, timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
            {
                return (null, "The source gateway stopped responding while its answer was being read.",
                    Failed(operation, SourceGatewayDiagnosticCode.Timeout, fault: exception));
            }
            catch (Exception exception) when (exception is HttpRequestException or IOException)
            {
                return (null, "The source gateway response ended before it was complete.",
                    Failed(operation, SourceGatewayDiagnosticCode.ResponseTruncated, fault: exception));
            }
            catch (InvalidDataException exception)
            {
                return (null, "The source gateway response is larger than the protocol allows.",
                    Failed(operation, SourceGatewayDiagnosticCode.ResponseTooLarge, fault: exception));
            }

            try
            {
                TResponse? parsed = JsonSerializer.Deserialize<TResponse>(payload, SourceGatewayProtocol.Json);
                if (parsed is null)
                {
                    return (null, "The source gateway answered with an empty document.",
                        Failed(operation, SourceGatewayDiagnosticCode.EmptyDocument));
                }

                if (logger is not null)
                {
                    SourceGatewayDiagnostics.CallAnswered(logger, operation, options.Authority.IdnHost);
                }

                return (parsed, null, SourceGatewayDiagnosticCode.Answered);
            }
            catch (JsonException exception)
            {
                return (null, "The source gateway answered with a document this protocol cannot read.",
                    Failed(operation, SourceGatewayDiagnosticCode.UnreadableDocument, fault: exception));
            }
        }
    }

    /// <summary>
    /// Reads at most <paramref name="limit"/> bytes and throws when there are more, so a gateway that
    /// declares no length — or lies about it — cannot stream this process out of memory.
    /// </summary>
    internal static async Task<byte[]> ReadBoundedAsync(Stream stream, int limit, CancellationToken cancellationToken)
    {
        using MemoryStream buffer = new();
        byte[] chunk = new byte[64 * 1024];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > limit)
            {
                throw new InvalidDataException("The response exceeded the protocol's response size limit.");
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    /// <summary>Server clock used for the freshness window, exposed so preparation and validation agree.</summary>
    public DateTimeOffset Now() => _clock();
}

/// <summary>
/// Text an operator sees when no gateway is configured. It is not an error: this is the supported
/// default, and it is the reason the extraction capabilities stay blocked rather than pretending.
/// </summary>
public static class SourceGatewayUnavailable
{
    public const string Reason =
        "No authorized source gateway is configured on this server, so no Forms module can be extracted. " +
        "Nothing was contacted and nothing was written.";
}

/// <summary>
/// Converts UTF-8 gateway text into the string a console may show, with control characters removed so a
/// remote answer cannot rewrite the operator's display.
/// </summary>
internal static class SourceGatewayText
{
    public static string Safe(string value, int maxCharacters)
    {
        StringBuilder builder = new(Math.Min(value.Length, maxCharacters));
        foreach (char character in value)
        {
            if (builder.Length == maxCharacters)
            {
                break;
            }

            builder.Append(char.IsControl(character) ? ' ' : character);
        }

        return builder.ToString().Trim();
    }
}
