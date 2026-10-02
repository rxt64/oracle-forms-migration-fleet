// Copyright (c) Microsoft. All rights reserved.

using System.Diagnostics;
using System.Net;
using System.Text;
using Azure.Core;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Trace;
using OracleFormsMigrationFleet.Fleet.Execution;
using OracleFormsMigrationFleet.Hosting;

namespace OracleFormsMigrationFleet.Tests;

/// <summary>
/// What a source-gateway failure is allowed to say, and where it is allowed to say it.
///
/// Before this, a gateway call that failed produced one sentence in an HTTP response body and nothing
/// else: no event, no code, no correlation. These tests pin the replacement from both sides — that a
/// failure now reaches <see cref="ILogger"/> with a stable code, and that what reaches it still carries
/// none of the material the client was built to withhold.
/// </summary>
public sealed class SourceGatewayDiagnosticsTests
{
    private const string SecretLikeMessage =
        "Connect to https://gateway.internal.example/source?sig=abc with password Hunter2-Not-A-Real-Secret";

    private const string TelemetrySourceName = "OracleFormsMigrationFleet.Tests.SourceGateway";

    private static readonly SourceGatewayOptions s_options = new()
    {
        Authority = new Uri("https://gateway.contoso.example/"),
        TokenScope = "api://gateway/.default",
        RequestTimeout = TimeSpan.FromSeconds(5),
    };

    private static readonly SourceGatewayAuthorizationScope s_scope = new("tenant-a", "project-a");

    [Fact]
    public async Task A_call_without_a_server_derived_scope_is_logged_with_its_code_and_contacts_nothing()
    {
        RecordingLogger<HttpSourceGatewayClient> logger = new();
        CountingHandler handler = new(() => throw new InvalidOperationException("the gateway must not be contacted"));
        HttpSourceGatewayClient client = Client(handler, new StubCredential(), logger);

        SourceGatewayCall call = await client.ExtractFormsModuleAsync(
            Request(scope: new SourceGatewayAuthorizationScope(string.Empty, string.Empty)), CancellationToken.None);

        Assert.Null(call.Response);
        Assert.Equal(SourceGatewayDiagnosticCode.MissingScope, call.DiagnosticCode);
        Assert.Equal(0, handler.Sends);

        RecordedLog entry = Assert.Single(logger.Entries);
        Assert.Equal(SourceGatewayDiagnostics.GatewayCallFailed, entry.EventId);
        Assert.Equal(SourceGatewayDiagnosticCode.MissingScope, entry.Values["Code"]);
        Assert.Equal(nameof(SourceGatewayOperation.FormsModuleExtract), entry.Values["Operation"]);
        Assert.Equal("gateway.contoso.example", entry.Values["AuthorityHost"]);
    }

    [Fact]
    public async Task A_token_failure_is_logged_as_a_fault_type_and_never_as_the_exception_text()
    {
        RecordingLogger<HttpSourceGatewayClient> logger = new();
        CountingHandler handler = new(() => throw new InvalidOperationException("the gateway must not be contacted"));
        HttpSourceGatewayClient client = Client(
            handler, new ThrowingCredential(new HttpRequestException(SecretLikeMessage)), logger);

        SourceGatewayCall call = await client.ExtractFormsModuleAsync(Request(), CancellationToken.None);

        Assert.Equal(SourceGatewayDiagnosticCode.TokenUnavailable, call.DiagnosticCode);
        Assert.Equal(0, handler.Sends);

        RecordedLog entry = Assert.Single(logger.Entries);
        Assert.Equal(nameof(HttpRequestException), entry.Values["FaultType"]);
        Assert.Null(entry.Exception);
        AssertNothingSensitive(entry);
    }

    [Fact]
    public async Task A_refused_status_is_logged_with_the_status_code_and_no_response_body()
    {
        RecordingLogger<HttpSourceGatewayClient> logger = new();
        CountingHandler handler = new(() => new HttpResponseMessage(HttpStatusCode.Forbidden)
        {
            Content = new StringContent(SecretLikeMessage, Encoding.UTF8, "application/json"),
        });
        HttpSourceGatewayClient client = Client(handler, new StubCredential(), logger);

        SourceGatewayCall call = await client.ExtractFormsModuleAsync(Request(), CancellationToken.None);

        Assert.Equal(SourceGatewayDiagnosticCode.UnexpectedStatus, call.DiagnosticCode);

        RecordedLog entry = Assert.Single(logger.Entries);
        Assert.Equal(403, Assert.IsType<int>(entry.Values["StatusCode"]));
        AssertNothingSensitive(entry);
    }

    [Fact]
    public async Task A_redirect_is_refused_under_its_own_code_rather_than_being_followed()
    {
        RecordingLogger<HttpSourceGatewayClient> logger = new();
        HttpResponseMessage redirect = new(HttpStatusCode.Found);
        redirect.Headers.Location = new Uri("https://elsewhere.example/source/forms-module/extract");
        CountingHandler handler = new(() => redirect);
        HttpSourceGatewayClient client = Client(handler, new StubCredential(), logger);

        SourceGatewayCall call = await client.ExtractFormsModuleAsync(Request(), CancellationToken.None);

        Assert.Null(call.Response);
        Assert.Equal(SourceGatewayDiagnosticCode.Redirected, call.DiagnosticCode);
        Assert.Equal(1, handler.Sends);
        Assert.DoesNotContain("elsewhere.example", Rendered(Assert.Single(logger.Entries)), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_probe_is_bounded_and_logged_as_its_own_operation()
    {
        RecordingLogger<HttpSourceGatewayClient> logger = new();
        HttpResponseMessage redirect = new(HttpStatusCode.Found);
        redirect.Headers.Location = new Uri("https://elsewhere.example/source/environment/probe");
        CountingHandler handler = new(() => redirect);
        HttpSourceGatewayClient client = Client(handler, new StubCredential(), logger);

        SourceGatewayProbeCall call = await client.ProbeSourceEnvironmentAsync(ProbeRequest(), CancellationToken.None);

        Assert.Null(call.Response);
        Assert.Equal(SourceGatewayDiagnosticCode.Redirected, call.DiagnosticCode);
        Assert.Equal(1, handler.Sends);

        RecordedLog entry = Assert.Single(logger.Entries);
        Assert.Equal(nameof(SourceGatewayOperation.SourceEnvironmentProbe), entry.Values["Operation"]);
        Assert.DoesNotContain("elsewhere.example", Rendered(entry), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_probe_without_a_server_derived_scope_contacts_nothing()
    {
        RecordingLogger<HttpSourceGatewayClient> logger = new();
        CountingHandler handler = new(() => throw new InvalidOperationException("the gateway must not be contacted"));
        HttpSourceGatewayClient client = Client(handler, new StubCredential(), logger);

        SourceGatewayProbeCall call = await client.ProbeSourceEnvironmentAsync(
            ProbeRequest(new SourceGatewayAuthorizationScope(string.Empty, "project-a")), CancellationToken.None);

        Assert.Null(call.Response);
        Assert.Equal(SourceGatewayDiagnosticCode.MissingScope, call.DiagnosticCode);
        Assert.Equal(0, handler.Sends);
        Assert.Equal(
            nameof(SourceGatewayOperation.SourceEnvironmentProbe),
            Assert.Single(logger.Entries).Values["Operation"]);
    }

    [Fact]
    public void Every_diagnostic_code_is_unique_and_carries_the_product_prefix()
    {
        Assert.Equal(
            SourceGatewayDiagnosticCode.All.Count,
            SourceGatewayDiagnosticCode.All.Distinct(StringComparer.Ordinal).Count());
        Assert.All(SourceGatewayDiagnosticCode.All, code => Assert.StartsWith("OFM-SGW-", code, StringComparison.Ordinal));
    }

    /// <summary>
    /// The published values themselves, not merely their shape. A code that an operator has written into a
    /// saved log query or a runbook is a wire contract: renumbering one silently retires that query, and a
    /// uniqueness-and-prefix check would not notice. Pinning the literals makes any renumbering a failing
    /// test and therefore a deliberate decision.
    /// </summary>
    [Fact]
    public void The_diagnostic_codes_are_exactly_the_sixteen_published_values_in_order()
    {
        Assert.Equal(
            [
                "OFM-SGW-0001",
                "OFM-SGW-0002",
                "OFM-SGW-0003",
                "OFM-SGW-0004",
                "OFM-SGW-0005",
                "OFM-SGW-0006",
                "OFM-SGW-0007",
                "OFM-SGW-0008",
                "OFM-SGW-0009",
                "OFM-SGW-0010",
                "OFM-SGW-0011",
                "OFM-SGW-0012",
                "OFM-SGW-0013",
                "OFM-SGW-0014",
                "OFM-SGW-0015",
                "OFM-SGW-0016",
            ],
            SourceGatewayDiagnosticCode.All);

        Assert.Equal("OFM-SGW-0001", SourceGatewayDiagnosticCode.NotConfigured);
        Assert.Equal("OFM-SGW-0002", SourceGatewayDiagnosticCode.Configured);
        Assert.Equal("OFM-SGW-0003", SourceGatewayDiagnosticCode.MissingScope);
        Assert.Equal("OFM-SGW-0004", SourceGatewayDiagnosticCode.RequestTooLarge);
        Assert.Equal("OFM-SGW-0005", SourceGatewayDiagnosticCode.TokenUnavailable);
        Assert.Equal("OFM-SGW-0006", SourceGatewayDiagnosticCode.Timeout);
        Assert.Equal("OFM-SGW-0007", SourceGatewayDiagnosticCode.Unreachable);
        Assert.Equal("OFM-SGW-0008", SourceGatewayDiagnosticCode.Redirected);
        Assert.Equal("OFM-SGW-0009", SourceGatewayDiagnosticCode.UnexpectedStatus);
        Assert.Equal("OFM-SGW-0010", SourceGatewayDiagnosticCode.OriginMismatch);
        Assert.Equal("OFM-SGW-0011", SourceGatewayDiagnosticCode.UnacceptableMediaType);
        Assert.Equal("OFM-SGW-0012", SourceGatewayDiagnosticCode.ResponseTooLarge);
        Assert.Equal("OFM-SGW-0013", SourceGatewayDiagnosticCode.ResponseTruncated);
        Assert.Equal("OFM-SGW-0014", SourceGatewayDiagnosticCode.UnreadableDocument);
        Assert.Equal("OFM-SGW-0015", SourceGatewayDiagnosticCode.EmptyDocument);
        Assert.Equal("OFM-SGW-0016", SourceGatewayDiagnosticCode.Answered);
    }

    /// <summary>The numeric event ids are queried directly by exporters, so they are pinned by number too.</summary>
    [Fact]
    public void The_event_ids_are_exactly_the_published_numbers_and_names()
    {
        Assert.Equal(6100, SourceGatewayDiagnostics.GatewayConfiguration.Id);
        Assert.Equal(6101, SourceGatewayDiagnostics.GatewayCallFailed.Id);
        Assert.Equal(6102, SourceGatewayDiagnostics.GatewayCallAnswered.Id);

        Assert.Equal(nameof(SourceGatewayDiagnostics.GatewayConfiguration), SourceGatewayDiagnostics.GatewayConfiguration.Name);
        Assert.Equal(nameof(SourceGatewayDiagnostics.GatewayCallFailed), SourceGatewayDiagnostics.GatewayCallFailed.Name);
        Assert.Equal(nameof(SourceGatewayDiagnostics.GatewayCallAnswered), SourceGatewayDiagnostics.GatewayCallAnswered.Name);

        Assert.Equal(
            3,
            new[]
            {
                SourceGatewayDiagnostics.GatewayConfiguration.Id,
                SourceGatewayDiagnostics.GatewayCallFailed.Id,
                SourceGatewayDiagnostics.GatewayCallAnswered.Id,
            }.Distinct().Count());
    }

    /// <summary>
    /// The same failure, observed the way production observes it: through the real OpenTelemetry logger
    /// provider and a real <see cref="LogRecord"/>, not through a hand-written <see cref="ILogger"/>.
    ///
    /// The earlier tests proved the client calls <c>ILogger</c> correctly. They could not prove that what
    /// an exporter receives is the same thing, because the test logger was the one deciding what a record
    /// looks like. This runs the genuine SDK pipeline instead and asserts on the exported record, so the
    /// redaction guarantee holds against the type that actually leaves the process.
    ///
    /// Correlation is asserted, never constructed: the test starts an <see cref="Activity"/> on a real
    /// <see cref="TracerProvider"/> and then compares the record's trace and span ids to that activity's.
    /// No identifier is handed to the logging call. Nothing here registers an exporter that leaves the
    /// process or reads a connection string; the in-memory exporter is the whole destination.
    /// </summary>
    [Fact]
    public async Task A_real_opentelemetry_log_record_carries_the_ambient_trace_and_still_withholds_the_detail()
    {
        CollectingExporter exporter = new();
        using ActivitySource source = new(TelemetrySourceName);
        using TracerProvider tracer = Sdk.CreateTracerProviderBuilder()
            .AddSource(TelemetrySourceName)
            .SetSampler(new AlwaysOnSampler())
            .Build()!;
        using ILoggerFactory factory = LoggerFactory.Create(builder => builder
            .SetMinimumLevel(LogLevel.Trace)
            .AddOpenTelemetry(options =>
            {
                options.IncludeFormattedMessage = true;
                options.IncludeScopes = false;
                options.AddProcessor(new SimpleLogRecordExportProcessor(exporter));
            }));

        CountingHandler handler = new(() => throw new InvalidOperationException("the gateway must not be contacted"));
        HttpSourceGatewayClient client = Client(
            handler,
            new ThrowingCredential(new HttpRequestException(SecretLikeMessage)),
            factory.CreateLogger<HttpSourceGatewayClient>());

        using Activity activity = source.StartActivity("source-gateway-call")
            ?? throw new InvalidOperationException("the tracer provider did not sample the test activity");

        SourceGatewayCall call = await client.ExtractFormsModuleAsync(Request(), CancellationToken.None);

        Assert.Equal(SourceGatewayDiagnosticCode.TokenUnavailable, call.DiagnosticCode);
        Assert.Equal(0, handler.Sends);

        ExportedRecord record = Assert.Single(exporter.Records);
        Assert.Equal(activity.TraceId, record.TraceId);
        Assert.Equal(activity.SpanId, record.SpanId);
        Assert.NotEqual(default, record.TraceId);

        Assert.Equal(SourceGatewayDiagnostics.GatewayCallFailed.Id, record.EventId.Id);
        Assert.Equal(LogLevel.Warning, record.Level);
        Assert.Null(record.Exception);

        Assert.Equal(SourceGatewayDiagnosticCode.TokenUnavailable, record.Attributes["Code"]);
        Assert.Equal(nameof(SourceGatewayOperation.FormsModuleExtract), record.Attributes["Operation"]);
        Assert.Equal("gateway.contoso.example", record.Attributes["AuthorityHost"]);
        Assert.Equal(nameof(HttpRequestException), record.Attributes["FaultType"]);

        AssertNothingSensitive(record.Rendered);
    }

    [Fact]
    public void An_unset_gateway_is_reported_as_configuration_state_naming_only_the_keys()
    {
        RecordingLogger<SourceGatewayStartupDiagnostics> logger = new();
        SourceGatewayDiagnostics.Configuration(
            logger, SourceGatewayDiagnosticCode.NotConfigured, "none", ["SourceGateway:Authority", "SourceGateway:TokenScope"]);

        RecordedLog entry = Assert.Single(logger.Entries);
        Assert.Equal(SourceGatewayDiagnostics.GatewayConfiguration, entry.EventId);
        Assert.Equal(SourceGatewayDiagnosticCode.NotConfigured, entry.Values["Code"]);
        Assert.Equal("SourceGateway:Authority, SourceGateway:TokenScope", entry.Values["MissingKeys"]);
    }

    /// <summary>A logged event may name the fault's type, never anything the fault or the remote said.</summary>
    private static void AssertNothingSensitive(RecordedLog entry) => AssertNothingSensitive(Rendered(entry));

    private static void AssertNothingSensitive(string rendered)
    {
        Assert.DoesNotContain("Hunter2", rendered, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sig=", rendered, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("gateway.internal.example", rendered, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("password", rendered, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("not-a-real-token", rendered, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ORDERS.fmb", rendered, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("tenant-a", rendered, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("project-a", rendered, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("/source/", rendered, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("api://", rendered, StringComparison.OrdinalIgnoreCase);
    }

    private static string Rendered(RecordedLog entry) =>
        entry.Message + "|" + string.Join("|", entry.Values.Select(pair => $"{pair.Key}={pair.Value}"));

    private static HttpSourceGatewayClient Client(
        CountingHandler handler, TokenCredential credential, ILogger<HttpSourceGatewayClient> logger) =>
        new(new HttpClient(handler), credential, s_options, clock: null, logger: logger);

    private static SourceGatewayFormsModuleRequest Request(SourceGatewayAuthorizationScope? scope = null) =>
        new(
            SourceGatewayProtocol.SchemaVersion,
            "legacy-order-entry",
            "6i",
            1,
            new string('a', 64),
            "ORDERS.fmb",
            new string('b', 64),
            scope ?? s_scope);

    private static SourceGatewayProbeRequest ProbeRequest(SourceGatewayAuthorizationScope? scope = null) =>
        new(
            SourceGatewayProtocol.SchemaVersion,
            "legacy-order-entry",
            "6i",
            "9i",
            1,
            new string('a', 64),
            scope ?? s_scope);

    private sealed class CountingHandler(Func<HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int Sends { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Sends++;
            HttpResponseMessage response = respond();
            response.RequestMessage = request;
            return Task.FromResult(response);
        }
    }

    private sealed class StubCredential : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            new("not-a-real-token", DateTimeOffset.UtcNow.AddMinutes(30));

        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            new(GetToken(requestContext, cancellationToken));
    }

    private sealed class ThrowingCredential(Exception fault) : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            throw fault;

        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            throw fault;
    }

    private sealed record RecordedLog(
        EventId EventId,
        LogLevel Level,
        string Message,
        IReadOnlyDictionary<string, object?> Values,
        Exception? Exception);

    /// <summary>
    /// A snapshot of an exported <see cref="LogRecord"/>. The SDK pools and reuses record instances after
    /// <c>Export</c> returns, so holding one and asserting on it later reads whatever the pipeline put
    /// there next. Everything an assertion needs is copied out while the record is still owned by us.
    /// </summary>
    private sealed record ExportedRecord(
        ActivityTraceId TraceId,
        ActivitySpanId SpanId,
        EventId EventId,
        LogLevel Level,
        string Rendered,
        IReadOnlyDictionary<string, object?> Attributes,
        Exception? Exception);

    /// <summary>
    /// The entire telemetry destination for these tests. It derives from the real
    /// <see cref="BaseExporter{T}"/>, so the records it sees arrive through the same processor and
    /// provider path a production exporter would use — and it sends nothing anywhere, holds no connection
    /// string, and needs no credential.
    /// </summary>
    private sealed class CollectingExporter : BaseExporter<LogRecord>
    {
        public List<ExportedRecord> Records { get; } = [];

        public override ExportResult Export(in Batch<LogRecord> batch)
        {
            foreach (LogRecord record in batch)
            {
                Dictionary<string, object?> attributes = [];
                if (record.Attributes is not null)
                {
                    foreach (KeyValuePair<string, object?> pair in record.Attributes)
                    {
                        attributes[pair.Key] = pair.Value;
                    }
                }

                string rendered = (record.FormattedMessage ?? record.Body ?? string.Empty)
                    + "|" + string.Join("|", attributes.Select(pair => $"{pair.Key}={pair.Value}"));

                Records.Add(new(
                    record.TraceId,
                    record.SpanId,
                    record.EventId,
                    record.LogLevel,
                    rendered,
                    attributes,
                    record.Exception));
            }

            return ExportResult.Success;
        }
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<RecordedLog> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Dictionary<string, object?> values = [];
            if (state is IReadOnlyList<KeyValuePair<string, object?>> pairs)
            {
                foreach (KeyValuePair<string, object?> pair in pairs)
                {
                    values[pair.Key] = pair.Value;
                }
            }

            Entries.Add(new(eventId, logLevel, formatter(state, exception), values, exception));
        }
    }
}
