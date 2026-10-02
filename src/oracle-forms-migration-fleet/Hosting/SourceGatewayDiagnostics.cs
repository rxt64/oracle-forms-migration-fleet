// Copyright (c) Microsoft. All rights reserved.

using Microsoft.Extensions.Logging;

namespace OracleFormsMigrationFleet.Hosting;

/// <summary>
/// The operation a source-gateway diagnostic belongs to. It is a fixed enum rather than free text so a
/// log query can group by it without matching on prose that a future edit might reword.
/// </summary>
public enum SourceGatewayOperation
{
    Configuration,
    FormsModuleExtract,
    OracleSchemaExtract,
    SourceEnvironmentProbe,
}

/// <summary>
/// Stable identifiers for every way a source-gateway call can end.
///
/// These are the only failure vocabulary that leaves this process. The client already refuses to return
/// a remote error body, an exception message, a stack trace, a URL, a header, or any part of a request
/// payload to its caller; these codes exist so that refusing to repeat the detail no longer means
/// refusing to say anything at all. A code plus the operation plus an HTTP status plus an exception TYPE
/// name is enough to tell two different failures apart, and none of those four can carry a secret.
/// </summary>
public static class SourceGatewayDiagnosticCode
{
    public const string NotConfigured = "OFM-SGW-0001";
    public const string Configured = "OFM-SGW-0002";
    public const string MissingScope = "OFM-SGW-0003";
    public const string RequestTooLarge = "OFM-SGW-0004";
    public const string TokenUnavailable = "OFM-SGW-0005";
    public const string Timeout = "OFM-SGW-0006";
    public const string Unreachable = "OFM-SGW-0007";
    public const string Redirected = "OFM-SGW-0008";
    public const string UnexpectedStatus = "OFM-SGW-0009";
    public const string OriginMismatch = "OFM-SGW-0010";
    public const string UnacceptableMediaType = "OFM-SGW-0011";
    public const string ResponseTooLarge = "OFM-SGW-0012";
    public const string ResponseTruncated = "OFM-SGW-0013";
    public const string UnreadableDocument = "OFM-SGW-0014";
    public const string EmptyDocument = "OFM-SGW-0015";
    public const string Answered = "OFM-SGW-0016";

    /// <summary>Every code this build can emit, in declaration order, for tests and for documentation.</summary>
    public static readonly IReadOnlyList<string> All =
    [
        NotConfigured,
        Configured,
        MissingScope,
        RequestTooLarge,
        TokenUnavailable,
        Timeout,
        Unreachable,
        Redirected,
        UnexpectedStatus,
        OriginMismatch,
        UnacceptableMediaType,
        ResponseTooLarge,
        ResponseTruncated,
        UnreadableDocument,
        EmptyDocument,
        Answered,
    ];
}

/// <summary>
/// Structured source-gateway telemetry, emitted through <see cref="ILogger"/> and therefore through
/// whatever exporter the host already registered. Nothing here constructs an exporter, a connection
/// string, or a second telemetry pipeline: a duplicate exporter would double-bill and could disagree
/// with the host's own sampling.
///
/// WHAT MAY BE LOGGED is deliberately a closed set: the operation, one of
/// <see cref="SourceGatewayDiagnosticCode"/>, the gateway's host name as configured on this server, an
/// HTTP status code, and the TYPE name of an exception. Anything derived from the remote answer, the
/// request payload, the caller, a header, a query, or a credential is excluded by construction — there
/// is no parameter on these methods that could carry one.
/// </summary>
public static class SourceGatewayDiagnostics
{
    public static readonly EventId GatewayConfiguration = new(6100, nameof(GatewayConfiguration));
    public static readonly EventId GatewayCallFailed = new(6101, nameof(GatewayCallFailed));
    public static readonly EventId GatewayCallAnswered = new(6102, nameof(GatewayCallAnswered));

    private static readonly Action<ILogger, string, string, string, Exception?> s_configuration =
        LoggerMessage.Define<string, string, string>(
            LogLevel.Information,
            GatewayConfiguration,
            "Source gateway {Code}: authority {AuthorityHost}, unmet host configuration keys {MissingKeys}.");

    private static readonly Action<ILogger, string, string, string, int, string, Exception?> s_failed =
        LoggerMessage.Define<string, string, string, int, string>(
            LogLevel.Warning,
            GatewayCallFailed,
            "Source gateway call failed {Code} during {Operation} to {AuthorityHost}; status {StatusCode}, fault {FaultType}.");

    private static readonly Action<ILogger, string, string, string, Exception?> s_answered =
        LoggerMessage.Define<string, string, string>(
            LogLevel.Information,
            GatewayCallAnswered,
            "Source gateway call answered {Code} during {Operation} to {AuthorityHost}.");

    /// <summary>
    /// Records whether this host has a gateway at all. A host without one is not an error — it is the
    /// supported default — but an operator looking at an all-blocked probe needs to be able to tell
    /// "no gateway was configured" apart from "the gateway refused", and until now neither reached
    /// telemetry at all.
    /// </summary>
    public static void Configuration(ILogger logger, string code, string authorityHost, IReadOnlyList<string> missingKeys)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(missingKeys);
        s_configuration(logger, code, authorityHost, string.Join(", ", missingKeys), null);
    }

    public static void CallFailed(
        ILogger logger,
        SourceGatewayOperation operation,
        string code,
        string authorityHost,
        int statusCode = 0,
        Exception? fault = null)
    {
        ArgumentNullException.ThrowIfNull(logger);
        s_failed(logger, code, operation.ToString(), authorityHost, statusCode, FaultType(fault), null);
    }

    public static void CallAnswered(ILogger logger, SourceGatewayOperation operation, string authorityHost)
    {
        ArgumentNullException.ThrowIfNull(logger);
        s_answered(logger, SourceGatewayDiagnosticCode.Answered, operation.ToString(), authorityHost, null);
    }

    /// <summary>
    /// The only thing an exception is allowed to contribute: its type name.
    ///
    /// The exception object itself is never passed to the logger, because a logged exception carries its
    /// message and its stack trace, and a transport exception's message routinely contains the full
    /// destination URL and sometimes the inner credential failure. The type name tells a reader whether
    /// the call failed at DNS, at TLS, at the socket, or at the token, which is what triage needs.
    /// </summary>
    public static string FaultType(Exception? fault) => fault?.GetType().Name ?? "None";
}
