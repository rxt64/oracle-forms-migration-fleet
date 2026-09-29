using System.Diagnostics;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using OracleFormsMigrationFleet.SourceWorker.Gateway;

namespace OracleFormsMigrationFleet.SourceWorker.Tests;

/// <summary>
/// Temporary input, output and home roots plus the registry document that binds them to one source
/// environment identifier. Everything a gateway is willing to touch comes from here, so a test that wants
/// to widen the surface has to widen this the way an operator would.
/// </summary>
public sealed class GatewayWorkspace : IDisposable
{
    public const string SourceId = "legacy-hrms-6i";
    public const string OtherSourceId = "legacy-payroll-6i";
    public const string Release = "6.0.8.27.0";
    public const string TenantId = "11111111-1111-1111-1111-111111111111";
    public const string OtherTenantId = "99999999-9999-9999-9999-999999999999";
    public const string ProjectId = "project-a";
    public const string OtherProjectId = "project-b";
    public const string CallerAppId = "22222222-2222-2222-2222-222222222222";
    public const string UnlistedAppId = "33333333-3333-3333-3333-333333333333";
    public const string Audience = "api://source-gateway";

    public GatewayWorkspace(bool oracleConnection = true)
    {
        Root = Directory.CreateTempSubdirectory("ofm-gateway-").FullName;
        InputRoot = Directory.CreateDirectory(Path.Combine(Root, "input")).FullName;
        OutputRoot = Directory.CreateDirectory(Path.Combine(Root, "artifacts")).FullName;
        FormsHome = Directory.CreateDirectory(Path.Combine(Root, "orant")).FullName;
        Elsewhere = Directory.CreateDirectory(Path.Combine(Root, "elsewhere")).FullName;
        RegistryPath = Path.Combine(Root, "source-registry.json");
        File.WriteAllText(RegistryPath, RegistryJson(InputRoot, OutputRoot, FormsHome, oracleConnection));
    }

    public string Root { get; }

    public string InputRoot { get; }

    public string OutputRoot { get; }

    public string FormsHome { get; }

    /// <summary>A directory that is not any registered root, used to prove containment is enforced.</summary>
    public string Elsewhere { get; }

    public string RegistryPath { get; }

    public static string LibrarySha256 => new('a', 64);

    /// <summary>The variable the registry points at. Its value lives only in the fixture's own map.</summary>
    public const string OracleConnectionVariable = "OFM_GATEWAY_ORACLE_LEGACY_HRMS";

    public const string OracleConnectionValue = "Dsn=HRMS9I;Uid=ofm_reader;Pwd=n0t-in-any-output";

    public static string RegistryJson(
        string inputRoot,
        string outputRoot,
        string formsHome,
        bool oracleConnection = true,
        string? authorizedProfileHash = null,
        int? authorizedProfileVersion = null)
    {
        Dictionary<string, object?> entry = new(StringComparer.Ordinal)
        {
            ["sourceEnvironmentId"] = SourceId,
            ["supportedFormsRelease"] = Release,
            ["inputRoot"] = inputRoot,
            ["outputRoot"] = outputRoot,
            ["formsHome"] = formsHome,
            ["approvedLibrarySha256"] = LibrarySha256,
            ["approvedLibraryFileVersion"] = "6.0.8.7.3",
            ["schemaAllowlist"] = new[] { "HRMS", "HRMS_AUDIT" },
            ["authorizedTenantId"] = TenantId,
            ["authorizedProjectIds"] = new[] { ProjectId },
        };

        if (authorizedProfileHash is not null)
        {
            entry["authorizedProfileHash"] = authorizedProfileHash;
        }

        if (authorizedProfileVersion is not null)
        {
            entry["authorizedProfileVersion"] = authorizedProfileVersion;
        }

        if (oracleConnection)
        {
            entry["oracleConnection"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["environmentVariable"] = OracleConnectionVariable,
                ["providerAlias"] = "odbc-oracle",
            };
        }

        return JsonSerializer.Serialize(
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["schemaVersion"] = 1,
                ["sources"] = new[] { entry },
            },
            GatewayProtocol.Json);
    }

    public Dictionary<string, string?> Environment(string? url = null) => new(StringComparer.Ordinal)
    {
        [GatewayOptions.UrlVariable] = url ?? "http://127.0.0.1:0",
        [GatewayOptions.LoopbackHttpVariable] = "true",
        [GatewayOptions.TenantVariable] = TenantId,
        [GatewayOptions.AudienceVariable] = Audience,
        [GatewayOptions.CallerAppIdsVariable] = CallerAppId,
        [GatewayOptions.RegistryVariable] = RegistryPath,
    };

    public GatewayOptions Options(Action<Dictionary<string, string?>>? adjust = null)
    {
        Dictionary<string, string?> values = Environment();
        adjust?.Invoke(values);

        bool read = GatewayOptions.TryRead(
            key => values.TryGetValue(key, out string? value) ? value : null,
            path => File.Exists(path) ? File.ReadAllText(path) : null,
            out GatewayOptions? options,
            out IReadOnlyList<string> errors);

        Assert.True(read, string.Join(" ", errors));
        return options!;
    }

    public GatewaySourceEntry Entry() => Options().Registry.Find(SourceId)!;

    /// <summary>Places a module under the registered input root and returns the digest a request must pin.</summary>
    public string WriteModule(string alias, string content)
    {
        File.WriteAllText(Path.Combine(InputRoot, alias), content);
        return Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(InputRoot, alias))));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

/// <summary>
/// A runner that returns a canned worker result. It stands in for the child process so the orchestration,
/// the correlation checks and the artifact inlining can be exercised exhaustively; it is never evidence
/// that the native path works.
/// </summary>
public sealed class StubExtractionRunner(Func<GatewaySourceEntry, WorkerExtractionRequest, GatewayRunOutcome> respond)
    : IGatewayExtractionRunner
{
    public int Invocations { get; private set; }

    public TaskCompletionSource Gate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public bool HoldUntilGated { get; init; }

    public async Task<GatewayRunOutcome> RunAsync(
        GatewaySourceEntry entry,
        WorkerExtractionRequest request,
        CancellationToken cancellationToken)
    {
        Invocations++;
        if (HoldUntilGated)
        {
            await Gate.Task.WaitAsync(cancellationToken);
        }

        return respond(entry, request);
    }

    /// <summary>Writes a plausible extraction artifact into <paramref name="directory"/> and reports it.</summary>
    public static WorkerExtractionResult Extracted(
        WorkerExtractionRequest request,
        string directory,
        string moduleIdentity = "HRMS_EMPLOYEE")
    {
        byte[] content = JsonSerializer.SerializeToUtf8Bytes(
            new FormsIrDocument(
                WorkerProtocol.IrGenerator,
                WorkerProtocol.IrSchemaVersion,
                request.SourceEnvironmentId,
                request.ProfileVersion,
                request.ProfileHash,
                request.ModuleAlias,
                request.ExpectedContentSha256,
                request.ExpectedFormsRelease,
                $"{WorkerConfiguration.LibraryAlias}@6.0.8.7.3",
                [new NeutralFormsModule(moduleIdentity, moduleIdentity, [], [], [], [])]),
            WorkerProtocol.IrJson);

        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, $"{moduleIdentity}.forms-ir.json");
        File.WriteAllBytes(path, content);

        return new WorkerExtractionResult(
            WorkerProtocol.SchemaVersion,
            request.SourceEnvironmentId,
            "Extracted",
            DateTimeOffset.UtcNow,
            "Windows",
            "X86",
            request.ExpectedFormsRelease,
            moduleIdentity,
            request.ExpectedContentSha256,
            path,
            ContentHash.OfBytes(content),
            FakeNativeFormsProvider.Evidence,
            [new WorkerCapability("forms.module.extract", CapabilityState.Verified, "OracleFormsOpenApiLibraries",
                request.ExpectedFormsRelease, "x86", "WindowsWorker", "none", "blocks=0;triggers=0;units=0")],
            []);
    }

    public static WorkerExtractionResult Blocked(WorkerExtractionRequest request, string detail) =>
        new(WorkerProtocol.SchemaVersion,
            request.SourceEnvironmentId,
            CapabilityState.BlockedPrerequisite,
            DateTimeOffset.UtcNow,
            "Linux",
            "X64",
            request.ExpectedFormsRelease,
            null,
            null,
            null,
            null,
            null,
            [new WorkerCapability("forms.worker.architecture", CapabilityState.BlockedPrerequisite,
                "WorkerHostArchitecture", request.ExpectedFormsRelease, "x86", "WindowsWorker",
                "operator.provision.windows.x86.worker")],
            [detail]);
}

/// <summary>
/// A real Kestrel listener on loopback with real JWT bearer validation. Signing keys are supplied in
/// process so no tenant metadata is fetched, but issuer, audience, lifetime, signature and the caller
/// application allowlist are all validated by the shipping code paths.
/// </summary>
public sealed class GatewayTestServer : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly RSA _rsa;
    private readonly RSA _otherRsa;

    private GatewayTestServer(WebApplication app, RSA rsa, RSA otherRsa, string baseAddress)
    {
        _app = app;
        _rsa = rsa;
        _otherRsa = otherRsa;
        BaseAddress = baseAddress;
    }

    public string BaseAddress { get; }

    public static async Task<GatewayTestServer> StartAsync(GatewayOptions options, IGatewayExtractionRunner runner)
    {
        RSA rsa = RSA.Create(2048);
        RSA otherRsa = RSA.Create(2048);

        WebApplication app = WorkerGatewayHost.Build(options, new GatewayHostDependencies(
            Runner: runner,
            TestSigningKeys: [new RsaSecurityKey(rsa) { KeyId = "gateway-test" }],
            TestValidIssuers: options.ValidIssuers));

        await app.StartAsync();
        string address = app.Urls.First();
        return new GatewayTestServer(app, rsa, otherRsa, address);
    }

    public HttpClient Client() => new() { BaseAddress = new Uri(BaseAddress) };

    public string Token(
        string tenantId = GatewayWorkspace.TenantId,
        string? appId = GatewayWorkspace.CallerAppId,
        string audience = GatewayWorkspace.Audience,
        string? issuer = null,
        bool expired = false,
        bool wrongKey = false)
    {
        Dictionary<string, object> claims = new(StringComparer.Ordinal)
        {
            ["tid"] = tenantId,
            ["oid"] = "44444444-4444-4444-4444-444444444444",
        };

        if (appId is not null)
        {
            claims["azp"] = appId;
        }

        SecurityTokenDescriptor descriptor = new()
        {
            Issuer = issuer ?? $"https://login.microsoftonline.com/{GatewayWorkspace.TenantId}/v2.0",
            Audience = audience,
            NotBefore = DateTime.UtcNow.AddMinutes(-20),
            Expires = expired ? DateTime.UtcNow.AddMinutes(-10) : DateTime.UtcNow.AddMinutes(10),
            Claims = claims,
            SigningCredentials = new SigningCredentials(
                new RsaSecurityKey(wrongKey ? _otherRsa : _rsa) { KeyId = "gateway-test" },
                SecurityAlgorithms.RsaSha256),
        };

        return new JsonWebTokenHandler().CreateToken(descriptor);
    }

    public static HttpRequestMessage Post(string path, object body, string? token)
    {
        HttpRequestMessage message = new(HttpMethod.Post, path)
        {
            Content = new StringContent(JsonSerializer.Serialize(body, GatewayProtocol.Json), Encoding.UTF8, "application/json"),
        };

        if (token is not null)
        {
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return message;
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
        _rsa.Dispose();
        _otherRsa.Dispose();
    }
}

/// <summary>
/// A schema runner that returns a canned worker result. It stands in for the child process so admission,
/// correlation and inlining can be exercised without a database; it is never evidence that a real Oracle
/// instance was read.
/// </summary>
public sealed class StubSchemaExtractionRunner(
    Func<GatewaySourceEntry, OracleSchemaExtractionRequest, GatewaySchemaRunOutcome> respond)
    : IGatewaySchemaExtractionRunner
{
    public int Invocations { get; private set; }

    public List<OracleSchemaExtractionRequest> Requests { get; } = [];

    public Task<GatewaySchemaRunOutcome> RunAsync(
        GatewaySourceEntry entry,
        OracleSchemaExtractionRequest request,
        CancellationToken cancellationToken)
    {
        Invocations++;
        Requests.Add(request);
        return Task.FromResult(respond(entry, request));
    }

    /// <summary>A result shaped exactly as the real extraction service writes one.</summary>
    public static OracleSchemaExtractionResult Extracted(
        OracleSchemaExtractionRequest request,
        string? ddl = null,
        IReadOnlyList<string>? allowlistEcho = null)
    {
        byte[] content = JsonSerializer.SerializeToUtf8Bytes(
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["generator"] = OracleSchemaProtocol.Generator,
                ["schemaVersion"] = OracleSchemaProtocol.ArtifactSchemaVersion,
                ["sourceEnvironmentId"] = request.SourceEnvironmentId,
                ["profileVersion"] = request.ProfileVersion,
                ["profileHash"] = request.ProfileHash,
                ["schemas"] = request.SchemaAllowlist,
                ["provider"] = "stub-odbc",
                ["coverage"] = new { tables = 1, columns = 1, constraints = 0, sequences = 0, programUnits = 0 },
                ["objects"] = Array.Empty<object>(),
                ["ddl"] = ddl ?? "CREATE TABLE \"HRMS\".\"EMPLOYEES\" (\n  \"ID\" NUMBER(10,0) NOT NULL\n);\n\n",
            },
            WorkerProtocol.IrJson);

        string digest = ContentHash.OfBytes(content);

        return new OracleSchemaExtractionResult(
            OracleSchemaProtocol.SchemaVersion,
            request.SourceEnvironmentId,
            request.ProfileVersion,
            request.ProfileHash,
            allowlistEcho ?? request.SchemaAllowlist,
            "Extracted",
            DateTimeOffset.UtcNow,
            digest,
            new OracleSchemaArtifact(OracleSchemaProtocol.MediaType, digest, content),
            [new WorkerCapability("oracle.catalog.read", CapabilityState.Verified, "OracleCatalogReadAccess",
                null, null, "SourceGatewayHost", "none", "schemas=1")],
            []);
    }
}

/// <summary>
/// Launches the real worker assembly as a child process, using this test project's own runtime
/// configuration so the process is genuine and needs nothing outside the test output directory.
/// </summary>
public static class WorkerProcess
{
    public static ProcessStartInfo Launcher()
    {
        string bin = AppContext.BaseDirectory;
        string testAssembly = Path.Combine(bin, "OracleFormsMigrationFleet.SourceWorker.Tests.dll");

        ProcessStartInfo start = new(HostPath());
        start.ArgumentList.Add("exec");
        start.ArgumentList.Add("--runtimeconfig");
        start.ArgumentList.Add(Path.ChangeExtension(testAssembly, ".runtimeconfig.json"));
        start.ArgumentList.Add("--depsfile");
        start.ArgumentList.Add(Path.ChangeExtension(testAssembly, ".deps.json"));
        start.ArgumentList.Add(Path.Combine(bin, "OracleFormsMigrationFleet.SourceWorker.dll"));
        start.WorkingDirectory = bin;
        return start;
    }

    public static string HostPath()
    {
        if (Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } declared && File.Exists(declared))
        {
            return declared;
        }

        string executable = OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";
        foreach (string? candidate in new[]
        {
            Environment.ProcessPath,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dotnet", executable),
            Path.Combine("/usr/share/dotnet", executable),
            Path.Combine("/usr/lib/dotnet", executable),
        })
        {
            if (candidate is not null &&
                string.Equals(Path.GetFileName(candidate), executable, StringComparison.OrdinalIgnoreCase) &&
                File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException("No dotnet host was found to launch the worker child process.");
    }
}
