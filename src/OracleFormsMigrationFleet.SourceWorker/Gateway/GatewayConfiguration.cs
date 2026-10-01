using System.Text.Json;

namespace OracleFormsMigrationFleet.SourceWorker.Gateway;

/// <summary>
/// How this gateway reaches the Oracle instance behind one registered source environment.
///
/// It holds a variable NAME, never a value. The connect string lives in the gateway host's own protected
/// environment under a name that must start with <see cref="RequiredPrefix"/>, so a registry document —
/// the one thing in this design an operator hand-edits — can neither carry a credential nor point the
/// worker at an unrelated variable such as a cloud client secret. The value is read once, at the moment a
/// child worker is started, and is written only into that child's environment.
/// </summary>
public sealed record GatewayOracleCredential(string EnvironmentVariable, string ProviderAlias)
{
    /// <summary>Every readable variable name must start with this, which is the whole allowlist.</summary>
    public const string RequiredPrefix = "OFM_GATEWAY_ORACLE_";

    public static bool IsReadableVariable(string? name) =>
        name is { Length: > 0 and <= 96 } &&
        name.StartsWith(RequiredPrefix, StringComparison.Ordinal) &&
        name.Length > RequiredPrefix.Length &&
        name.All(character => char.IsAsciiLetterUpper(character) || char.IsAsciiDigit(character) || character == '_');
}

/// <summary>
/// One registered source environment. This is the whole surface a caller can reach: the caller names an
/// identifier that must already appear here, and every root, home, release and binary digest used to serve
/// it is read from this entry rather than from the request.
///
/// Nothing here is a secret. Roots and homes are filesystem locations on the gateway host, the digests pin
/// the approved native binary, the schema allowlist is the set of Oracle schemas the operator agreed may
/// be read, and the Oracle credential is a variable name rather than a credential. A caller that is fully
/// compromised can still only ask for a module alias an operator already placed under
/// <see cref="InputRoot"/>, or a subset of <see cref="SchemaAllowlist"/>.
///
/// <see cref="AuthorizedTenantId"/> and <see cref="AuthorizedProjectIds"/> are the answer to "whose source
/// is this". They are mandatory because the bearer token cannot answer it: the workbench calls with one
/// application identity for every project it hosts, so without this binding any project operator in the
/// tenant could name any registered source environment and read it through the gateway's own Forms
/// installation and Oracle credential. The identifier is not a secret either, so guessing one buys
/// nothing once the project must also match.
///
/// <see cref="AuthorizedProfileHash"/> and <see cref="AuthorizedProfileVersion"/> are optional and pin
/// the exact immutable source-environment version the operator approved reading. Leaving them unset means
/// any version the authorized project publishes is served, which is the normal case; setting them means a
/// caller that republished its own profile has to be re-approved here before the gateway will act on it.
/// </summary>
public sealed record GatewaySourceEntry(
    string SourceEnvironmentId,
    string SupportedFormsRelease,
    string InputRoot,
    string OutputRoot,
    string FormsHome,
    string ApprovedLibrarySha256,
    string ApprovedLibraryFileVersion,
    IReadOnlyList<string> SchemaAllowlist,
    string AuthorizedTenantId,
    IReadOnlyList<string> AuthorizedProjectIds,
    GatewayOracleCredential? OracleCredential = null,
    string? AuthorizedProfileHash = null,
    int? AuthorizedProfileVersion = null)
{
    /// <summary>Projects one entry may be read for. A registry is an operator document, not a directory.</summary>
    public const int MaxAuthorizedProjects = 32;

    /// <summary>
    /// Why this entry refuses the scope, or null when it serves it.
    ///
    /// <paramref name="callerTenantId"/> is the tenant claim out of the validated token, so a scope that
    /// names a tenant the caller did not authenticate in is refused before the registry is consulted at
    /// all. The request's own tenant is never trusted on its own.
    /// </summary>
    public string? Refuses(GatewayAuthorizationScope? scope, string? callerTenantId)
    {
        if (scope is null ||
            !GatewayText.IsScopePart(scope.TenantId) ||
            !GatewayText.IsScopePart(scope.ProjectId))
        {
            return "The request carries no well-formed tenant and project authorization scope, so nothing was opened.";
        }

        if (!string.Equals(callerTenantId, scope.TenantId, StringComparison.OrdinalIgnoreCase))
        {
            return "The request names a tenant other than the one its token was issued in, so nothing was opened.";
        }

        if (!string.Equals(AuthorizedTenantId, scope.TenantId, StringComparison.OrdinalIgnoreCase) ||
            !AuthorizedProjectIds.Contains(scope.ProjectId, StringComparer.Ordinal))
        {
            return
                "This gateway does not serve that source environment to the tenant and project the request names, so " +
                "nothing was opened.";
        }

        return null;
    }

    /// <summary>Why the pinned immutable profile refuses this request, or null when none is pinned.</summary>
    public string? RefusesProfile(int profileVersion, string? profileHash) =>
        (AuthorizedProfileVersion is { } version && version != profileVersion) ||
        (AuthorizedProfileHash is { Length: > 0 } hash && !string.Equals(hash, profileHash, StringComparison.OrdinalIgnoreCase))
            ? "This gateway serves that source environment at a different approved profile version, so nothing was opened."
            : null;
}

/// <summary>The registered source environments, keyed by identifier. An unlisted identifier is refused.</summary>
public sealed class GatewaySourceRegistry
{
    private readonly Dictionary<string, GatewaySourceEntry> _sources;

    private GatewaySourceRegistry(Dictionary<string, GatewaySourceEntry> sources) => _sources = sources;

    public static GatewaySourceRegistry Empty { get; } = new([]);

    public int Count => _sources.Count;

    public IReadOnlyCollection<string> RegisteredIds => _sources.Keys;

    public GatewaySourceEntry? Find(string? sourceEnvironmentId) =>
        sourceEnvironmentId is not null && _sources.TryGetValue(sourceEnvironmentId, out GatewaySourceEntry? entry)
            ? entry
            : null;

    /// <summary>
    /// Parses the operator's registry document. Every field is validated here rather than at use, so a
    /// gateway either starts with a registry it can defend or does not start.
    /// </summary>
    public static bool TryParse(string json, out GatewaySourceRegistry registry, out IReadOnlyList<string> errors)
    {
        registry = Empty;
        List<string> failures = [];

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            errors = ["The source registry is not valid JSON."];
            return false;
        }

        using (document)
        {
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                errors = ["The source registry is not a JSON object."];
                return false;
            }

            if (!root.TryGetProperty("schemaVersion", out JsonElement version) ||
                version.ValueKind != JsonValueKind.Number ||
                !version.TryGetInt32(out int declared) ||
                declared != GatewayProtocol.SchemaVersion)
            {
                errors = [$"The source registry must declare schemaVersion {GatewayProtocol.SchemaVersion}."];
                return false;
            }

            if (!root.TryGetProperty("sources", out JsonElement sources) || sources.ValueKind != JsonValueKind.Array)
            {
                errors = ["The source registry declares no 'sources' array."];
                return false;
            }

            Dictionary<string, GatewaySourceEntry> parsed = new(StringComparer.Ordinal);
            int index = 0;
            foreach (JsonElement element in sources.EnumerateArray())
            {
                index++;
                if (TryReadEntry(element, index, parsed, failures) is { } entry)
                {
                    parsed[entry.SourceEnvironmentId] = entry;
                }
            }

            if (failures.Count > 0)
            {
                errors = failures;
                return false;
            }

            if (parsed.Count == 0)
            {
                errors = ["The source registry registers no source environment, so the gateway would serve nothing."];
                return false;
            }

            registry = new GatewaySourceRegistry(parsed);
            errors = [];
            return true;
        }
    }

    private static GatewaySourceEntry? TryReadEntry(
        JsonElement element,
        int index,
        Dictionary<string, GatewaySourceEntry> seen,
        List<string> failures)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            failures.Add($"Source registry entry {index} is not a JSON object.");
            return null;
        }

        string? id = Text(element, "sourceEnvironmentId");
        string? release = Text(element, "supportedFormsRelease");
        string? inputRoot = GatewayText.Directory(Text(element, "inputRoot"));
        string? outputRoot = GatewayText.Directory(Text(element, "outputRoot"));
        string? formsHome = GatewayText.Directory(Text(element, "formsHome"));
        string? libraryHash = Text(element, "approvedLibrarySha256")?.Trim().ToLowerInvariant();
        string? libraryVersion = GatewayText.Version(Text(element, "approvedLibraryFileVersion"));

        int before = failures.Count;
        if (!GatewayText.IsIdentifier(id, 63)) failures.Add($"Source registry entry {index} has no usable sourceEnvironmentId.");
        else if (seen.ContainsKey(id!)) failures.Add($"Source registry entry {index} repeats sourceEnvironmentId '{id}'.");
        if (!GatewayText.IsIdentifier(release, 40)) failures.Add($"Source registry entry {index} has no usable supportedFormsRelease.");
        if (inputRoot is null) failures.Add($"Source registry entry {index} has no fully qualified inputRoot.");
        if (outputRoot is null) failures.Add($"Source registry entry {index} has no fully qualified outputRoot.");
        if (formsHome is null) failures.Add($"Source registry entry {index} has no fully qualified formsHome.");
        if (!ContentHash.IsSha256(libraryHash)) failures.Add($"Source registry entry {index} has no approvedLibrarySha256 digest.");
        if (libraryVersion is null)
        {
            failures.Add(
                $"Source registry entry {index} has no approvedLibraryFileVersion. It must be a dotted release, or " +
                $"'{WorkerConfiguration.UnversionedSentinel}' when the approved binary carries no file version at all.");
        }
        else if (string.Equals(libraryVersion, WorkerConfiguration.UnversionedSentinel, StringComparison.Ordinal) &&
                 !ContentHash.IsSha256(libraryHash))
        {
            // The sentinel gives up version as a check, so the digest is the only thing left pinning the binary.
            failures.Add(
                $"Source registry entry {index} declares approvedLibraryFileVersion " +
                $"'{WorkerConfiguration.UnversionedSentinel}' without an approvedLibrarySha256 digest to pin the binary.");
        }

        // Input and output must be distinct trees. Sharing them would let an artifact this gateway wrote be
        // offered back as an operator-supplied source module on the next request.
        if (inputRoot is not null && outputRoot is not null &&
            (GatewayText.SamePath(inputRoot, outputRoot) ||
             TrustedInput.IsWithin(inputRoot, outputRoot) ||
             TrustedInput.IsWithin(outputRoot, inputRoot)))
        {
            failures.Add($"Source registry entry {index} nests its inputRoot and outputRoot; they must be separate trees.");
        }

        List<string> schemas = [];
        if (element.TryGetProperty("schemaAllowlist", out JsonElement allowlist))
        {
            if (allowlist.ValueKind != JsonValueKind.Array || allowlist.GetArrayLength() > GatewayProtocol.MaxSchemaAllowlistEntries)
            {
                failures.Add($"Source registry entry {index} has a schemaAllowlist that is not an array of at most {GatewayProtocol.MaxSchemaAllowlistEntries} names.");
            }
            else
            {
                foreach (JsonElement schema in allowlist.EnumerateArray())
                {
                    string? name = schema.ValueKind == JsonValueKind.String ? schema.GetString() : null;
                    if (!GatewayText.IsIdentifier(name, 30))
                    {
                        failures.Add($"Source registry entry {index} lists a schema name that is not a plain identifier.");
                        break;
                    }

                    schemas.Add(name!.ToUpperInvariant());
                }
            }
        }

        (string? authorizedTenant, List<string> authorizedProjects, string? pinnedHash, int? pinnedVersion) =
            ReadAuthorizedScope(element, index, failures);

        return failures.Count > before
            ? null
            : new GatewaySourceEntry(id!, release!, inputRoot!, outputRoot!, formsHome!, libraryHash!, libraryVersion!, schemas,
                authorizedTenant!, authorizedProjects,
                ReadOracleCredential(element, index, failures), pinnedHash, pinnedVersion);
    }

    /// <summary>
    /// Reads the tenant and project this entry may be served to, and the optional pinned profile.
    ///
    /// All of it is mandatory except the pin. An older registry that predates this binding fails to parse
    /// with a message naming what to add rather than defaulting to "any project in the tenant": a default
    /// there would silently keep the gateway serving every project the workbench hosts, which is the
    /// exact posture this binding exists to end. Failing the whole registry is deliberate — a gateway
    /// that cannot say whose source it holds does not listen.
    /// </summary>
    private static (string? Tenant, List<string> Projects, string? PinnedHash, int? PinnedVersion) ReadAuthorizedScope(
        JsonElement element,
        int index,
        List<string> failures)
    {
        string? tenant = Text(element, "authorizedTenantId")?.Trim();
        if (!GatewayText.IsGuid(tenant))
        {
            failures.Add(
                $"Source registry entry {index} has no authorizedTenantId. Name the Entra tenant GUID whose workbench may " +
                "read this source environment; there is no default, because a shared caller identity cannot establish it.");
        }

        List<string> projects = [];
        if (!element.TryGetProperty("authorizedProjectIds", out JsonElement declared) ||
            declared.ValueKind != JsonValueKind.Array)
        {
            failures.Add(
                $"Source registry entry {index} has no authorizedProjectIds array. List the workbench project identifiers " +
                "an operator approved reading this source environment for; an empty or absent list serves nobody.");
        }
        else if (declared.GetArrayLength() is 0 or > GatewaySourceEntry.MaxAuthorizedProjects)
        {
            failures.Add(
                $"Source registry entry {index} must list between 1 and {GatewaySourceEntry.MaxAuthorizedProjects} " +
                "authorizedProjectIds.");
        }
        else
        {
            foreach (JsonElement project in declared.EnumerateArray())
            {
                string? name = project.ValueKind == JsonValueKind.String ? project.GetString() : null;
                if (!GatewayText.IsScopePart(name))
                {
                    failures.Add($"Source registry entry {index} lists a project identifier that is not a plain identifier.");
                    break;
                }

                projects.Add(name!);
            }
        }

        string? pinnedHash = Text(element, "authorizedProfileHash")?.Trim().ToLowerInvariant();
        if (pinnedHash is { Length: > 0 } && !ContentHash.IsSha256(pinnedHash))
        {
            failures.Add($"Source registry entry {index} has an authorizedProfileHash that is not a SHA-256 digest.");
            pinnedHash = null;
        }

        int? pinnedVersion = null;
        if (element.TryGetProperty("authorizedProfileVersion", out JsonElement version))
        {
            if (version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out int parsed) || parsed <= 0)
            {
                failures.Add($"Source registry entry {index} has an authorizedProfileVersion that is not a positive integer.");
            }
            else
            {
                pinnedVersion = parsed;
            }
        }

        return (tenant, projects, pinnedHash is { Length: > 0 } ? pinnedHash : null, pinnedVersion);
    }

    /// <summary>
    /// Reads the Oracle credential reference, which is optional: an entry that declares none simply has no
    /// schema path and says so at request time. What is not optional is the shape — a value that looks like
    /// a connect string rather than a variable name fails the whole registry rather than being ignored,
    /// because an operator who put a credential in this document must be told, not quietly served.
    /// </summary>
    private static GatewayOracleCredential? ReadOracleCredential(JsonElement element, int index, List<string> failures)
    {
        if (!element.TryGetProperty("oracleConnection", out JsonElement connection))
        {
            return null;
        }

        if (connection.ValueKind != JsonValueKind.Object)
        {
            failures.Add($"Source registry entry {index} has an oracleConnection that is not a JSON object.");
            return null;
        }

        foreach (JsonProperty property in connection.EnumerateObject())
        {
            if (property.Name is not ("environmentVariable" or "providerAlias"))
            {
                failures.Add(
                    $"Source registry entry {index} declares oracleConnection.{property.Name}. Only a variable name and a " +
                    "provider alias are read here; a connect string must live in the gateway host's environment.");
                return null;
            }
        }

        string? variable = Text(connection, "environmentVariable")?.Trim();
        string? provider = Text(connection, "providerAlias")?.Trim();

        if (!GatewayOracleCredential.IsReadableVariable(variable))
        {
            failures.Add(
                $"Source registry entry {index} must name an Oracle connection environment variable beginning " +
                $"'{GatewayOracleCredential.RequiredPrefix}'. The registry holds the variable name only.");
            return null;
        }

        if (provider is not null && !GatewayText.IsIdentifier(provider, 40))
        {
            failures.Add($"Source registry entry {index} has an oracleConnection.providerAlias that is not a plain identifier.");
            return null;
        }

        return new GatewayOracleCredential(variable!, provider ?? "odbc");
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}

/// <summary>
/// Which certificate an https listener presents. There is no PFX path and no password here by design:
/// the private key stays non-exportable in <c>LocalMachine\My</c>, and this names the certificate only.
/// </summary>
public sealed record GatewayTlsOptions(string? PinnedThumbprint, string SubjectHost)
{
    public const string ThumbprintVariable = "OFM_GATEWAY_TLS_CERTIFICATE_THUMBPRINT";
    public const string SubjectVariable = "OFM_GATEWAY_TLS_CERTIFICATE_SUBJECT";

    /// <summary>
    /// Reads the certificate selection for <paramref name="listenerHost"/>. The subject defaults to the
    /// host the listener was configured to bind, because presenting a certificate for a different name
    /// than the URI callers use is a misconfiguration the gateway should not help an operator into.
    /// </summary>
    public static GatewayTlsOptions? TryRead(Func<string, string?> read, string listenerHost, List<string> failures)
    {
        string? thumbprint = read(ThumbprintVariable)?.Trim();
        if (thumbprint is { Length: > 0 })
        {
            string normalized = GatewayServerCertificate.Normalize(thumbprint);
            if (normalized.Length is not (40 or 64) || !normalized.All(char.IsAsciiHexDigit))
            {
                failures.Add($"{ThumbprintVariable} must be a SHA-1 or SHA-256 certificate thumbprint in hexadecimal.");
                return null;
            }

            thumbprint = normalized.ToUpperInvariant();
        }

        string? subject = read(SubjectVariable)?.Trim();
        if (subject is { Length: > 0 } && !GatewayText.IsIdentifier(subject, 253))
        {
            failures.Add($"{SubjectVariable} must be the DNS host name the approved certificate was issued for.");
            return null;
        }

        return new GatewayTlsOptions(
            thumbprint is { Length: > 0 } ? thumbprint : null,
            subject is { Length: > 0 } ? subject : listenerHost);
    }
}

/// <summary>
/// Everything the gateway must be told before it will listen. None of it is a credential: the gateway
/// authenticates callers with Entra tokens it validates itself, and authenticates to nothing outbound.
///
/// The default is no configuration, and no configuration means no listener. Nothing is inferred from the
/// machine, because a gateway that discovers its own tenant, audience or source roots cannot state what an
/// operator approved.
/// </summary>
public sealed record GatewayOptions(
    Uri BindUrl,
    string TenantId,
    string Audience,
    IReadOnlyList<string> AllowedCallerAppIds,
    GatewaySourceRegistry Registry,
    int MaxConcurrentExtractions,
    TimeSpan ExtractionTimeout,
    int MaxWorkerOutputBytes,
    int MaxSchemaWorkerOutputBytes,
    GatewayTlsOptions? Tls = null,
    string? ProtectedCredentialRoot = null)
{
    /// <summary>
    /// The Windows service this host installs as. It is a constant rather than a setting so an operator,
    /// an installer and a log line cannot disagree about which service is the source gateway.
    /// </summary>
    public const string ServiceName = "OFMSourceGateway";

    public const string UrlVariable = "OFM_GATEWAY_URL";
    public const string LoopbackHttpVariable = "OFM_GATEWAY_ALLOW_LOOPBACK_HTTP";
    public const string TenantVariable = "OFM_GATEWAY_TENANT_ID";
    public const string AudienceVariable = "OFM_GATEWAY_AUDIENCE";
    public const string CallerAppIdsVariable = "OFM_GATEWAY_CALLER_APP_IDS";
    public const string RegistryVariable = "OFM_GATEWAY_SOURCE_REGISTRY";
    public const string ConcurrencyVariable = "OFM_GATEWAY_MAX_CONCURRENT_EXTRACTIONS";
    public const string TimeoutVariable = "OFM_GATEWAY_EXTRACTION_TIMEOUT_SECONDS";
    public const string OutputBytesVariable = "OFM_GATEWAY_MAX_WORKER_OUTPUT_BYTES";
    public const string SchemaOutputBytesVariable = "OFM_GATEWAY_MAX_SCHEMA_OUTPUT_BYTES";

    /// <summary>
    /// Directory holding DPAPI-protected credential files. It is a locator, never a credential: the file
    /// within it is named for the variable a registry entry already declares.
    /// </summary>
    public const string CredentialRootVariable = "OFM_GATEWAY_PROTECTED_CREDENTIAL_ROOT";

    public const int MaxCallerAppIds = 8;
    private const int DefaultConcurrency = 1;
    private const int MaximumConcurrency = 8;
    private const int DefaultWorkerOutputBytes = 1024 * 1024;
    private const int MaximumWorkerOutputBytes = 8 * 1024 * 1024;

    // A schema worker returns its artifact INLINE rather than by path, and a 16 MiB artifact is about
    // 22 MiB once base64 has been wrapped in a result document. The Forms ceiling above would truncate
    // that, so the schema path has its own configured ceiling — still bounded, never unlimited.
    private const int DefaultSchemaOutputBytes = 24 * 1024 * 1024;
    private const int MinimumSchemaOutputBytes = 64 * 1024;
    private const int MaximumSchemaOutputBytes = 32 * 1024 * 1024;
    private static readonly TimeSpan s_defaultTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan s_maximumTimeout = TimeSpan.FromMinutes(15);

    /// <summary>True only for an explicit loopback development listener. Any other host must be https.</summary>
    public bool IsLoopbackDevelopmentListener =>
        !string.Equals(BindUrl.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal);

    /// <summary>The issuers an Entra token for <see cref="TenantId"/> may legitimately declare.</summary>
    public IReadOnlyList<string> ValidIssuers =>
    [
        $"https://login.microsoftonline.com/{TenantId}/v2.0",
        $"https://sts.windows.net/{TenantId}/",
    ];

    /// <summary>
    /// The audiences a token for this gateway may carry. A v1 access token names the App ID URI the client
    /// asked for; a v2 token — which is what an app registration with requestedAccessTokenVersion 2 always
    /// mints — names the resource application's own client identifier instead. A gateway configured with
    /// <c>api://{clientId}</c> therefore has to accept that one identifier as well, or it refuses every
    /// token Entra issues for it.
    ///
    /// The second audience is derived from the configured one and from nothing else: only the exact
    /// <c>api://</c> form of a GUID yields it, so no other identifier URI, tenant or wildcard is admitted,
    /// and an operator still states a single audience.
    /// </summary>
    public IReadOnlyList<string> ValidAudiences =>
        Audience.StartsWith(ApplicationIdUriPrefix, StringComparison.Ordinal) &&
        GatewayText.IsGuid(Audience[ApplicationIdUriPrefix.Length..])
            ? [Audience, Audience[ApplicationIdUriPrefix.Length..]]
            : [Audience];

    private const string ApplicationIdUriPrefix = "api://";

    public string Authority => $"https://login.microsoftonline.com/{TenantId}/v2.0";

    public static bool TryRead(
        Func<string, string?> read,
        Func<string, string?> readRegistryFile,
        out GatewayOptions? options,
        out IReadOnlyList<string> errors)
    {
        ArgumentNullException.ThrowIfNull(read);
        ArgumentNullException.ThrowIfNull(readRegistryFile);

        options = null;
        List<string> failures = [];

        Uri? bind = ReadBindUrl(read, failures);
        string? tenant = read(TenantVariable)?.Trim();
        string? audience = read(AudienceVariable)?.Trim();

        if (!GatewayText.IsGuid(tenant))
        {
            failures.Add($"{TenantVariable} must be the Entra tenant identifier as a GUID.");
        }

        if (audience is not { Length: > 0 and <= 256 } || audience.Any(char.IsWhiteSpace))
        {
            failures.Add($"{AudienceVariable} must be the single audience this gateway's app registration exposes.");
        }

        List<string> callers = [];
        foreach (string candidate in (read(CallerAppIdsVariable) ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!GatewayText.IsGuid(candidate))
            {
                failures.Add($"{CallerAppIdsVariable} must be a comma-separated list of caller application GUIDs.");
                break;
            }

            callers.Add(candidate.ToLowerInvariant());
        }

        if (callers.Count is 0)
        {
            failures.Add($"{CallerAppIdsVariable} lists no allowed caller, so no request could ever be authorized.");
        }
        else if (callers.Count > MaxCallerAppIds)
        {
            failures.Add($"{CallerAppIdsVariable} lists more than {MaxCallerAppIds} callers.");
        }

        GatewaySourceRegistry registry = GatewaySourceRegistry.Empty;
        string? registryPath = read(RegistryVariable)?.Trim();

        if (registryPath is not { Length: > 0 and <= 240 } || !Path.IsPathFullyQualified(registryPath))
        {
            failures.Add($"{RegistryVariable} must be the fully qualified path of the source registry document.");
        }
        else
        {
            string? content = readRegistryFile(registryPath);
            if (content is null)
            {
                failures.Add($"The source registry named by {RegistryVariable} could not be read.");
            }
            else if (!GatewaySourceRegistry.TryParse(content, out registry, out IReadOnlyList<string> registryErrors))
            {
                failures.AddRange(registryErrors);
            }
        }

        if (failures.Count > 0)
        {
            errors = failures;
            return false;
        }

        // Only an https listener selects a certificate. A loopback development listener presents none, so
        // reading a thumbprint there would suggest a protection it does not have.
        GatewayTlsOptions? tls = null;
        if (string.Equals(bind!.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal))
        {
            tls = GatewayTlsOptions.TryRead(read, bind.Host, failures);
            if (failures.Count > 0)
            {
                errors = failures;
                return false;
            }
        }

        string? credentialRoot = read(CredentialRootVariable)?.Trim();
        if (credentialRoot is { Length: > 0 })
        {
            credentialRoot = GatewayText.Directory(credentialRoot);
            if (credentialRoot is null || !GatewayCredentialProvider.IsExistingUnlinkedDirectory(credentialRoot))
            {
                errors =
                [
                    $"{CredentialRootVariable} must be the fully qualified path of an existing protected credential " +
                    "directory that is not a filesystem link. Provision and ACL that directory before starting the gateway.",
                ];
                return false;
            }
        }

        options = new GatewayOptions(
            bind!,
            tenant!.ToLowerInvariant(),
            audience!,
            callers,
            registry,
            Bounded(read(ConcurrencyVariable), DefaultConcurrency, 1, MaximumConcurrency),
            TimeSpan.FromSeconds(Bounded(read(TimeoutVariable), (int)s_defaultTimeout.TotalSeconds, 1, (int)s_maximumTimeout.TotalSeconds)),
            Bounded(read(OutputBytesVariable), DefaultWorkerOutputBytes, 4 * 1024, MaximumWorkerOutputBytes),
            Bounded(read(SchemaOutputBytesVariable), DefaultSchemaOutputBytes, MinimumSchemaOutputBytes, MaximumSchemaOutputBytes),
            tls,
            credentialRoot is { Length: > 0 } ? credentialRoot : null);

        errors = [];
        return true;
    }

    private static Uri? ReadBindUrl(Func<string, string?> read, List<string> failures)
    {
        string? raw = read(UrlVariable)?.Trim();
        if (raw is not { Length: > 0 and <= 256 } ||
            !Uri.TryCreate(raw, UriKind.Absolute, out Uri? parsed) ||
            !string.IsNullOrEmpty(parsed.UserInfo) ||
            !string.IsNullOrEmpty(parsed.Query) ||
            !string.IsNullOrEmpty(parsed.Fragment) ||
            parsed.AbsolutePath != "/")
        {
            failures.Add($"{UrlVariable} must be an absolute listener origin with no path, query, fragment or sign-in details.");
            return null;
        }

        if (string.Equals(parsed.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal))
        {
            return parsed;
        }

        // The only cleartext listener the gateway will ever open is an explicitly requested loopback one,
        // because a bearer token on the wire in cleartext is a credential the operator handed to the network.
        bool loopbackRequested = string.Equals(read(LoopbackHttpVariable)?.Trim(), "true", StringComparison.OrdinalIgnoreCase);
        if (string.Equals(parsed.Scheme, Uri.UriSchemeHttp, StringComparison.Ordinal) &&
            loopbackRequested &&
            GatewayText.IsLoopbackHost(parsed.Host))
        {
            return parsed;
        }

        failures.Add(
            $"{UrlVariable} must be https. Cleartext is permitted only on a loopback host and only when " +
            $"{LoopbackHttpVariable} is set to true for local development or testing.");
        return null;
    }

    private static int Bounded(string? value, int fallback, int minimum, int maximum) =>
        int.TryParse(value, out int parsed) ? Math.Clamp(parsed, minimum, maximum) : fallback;
}

internal static class GatewayText
{
    /// <summary>
    /// Identifiers reach log lines, derived file names and child-process environment values, so they are
    /// restricted to characters that cannot carry a separator or a terminal escape.
    /// </summary>
    public static bool IsIdentifier(string? value, int maximumLength) =>
        value is { Length: > 0 } && value.Length <= maximumLength &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-');

    /// <summary>
    /// One half of an authorization scope. It is slightly wider than an identifier because a tenant is a
    /// GUID and a workbench project identifier may be tenant-qualified with a colon, and it is still
    /// narrow enough that no value can carry a path separator, a space, or a terminal escape.
    /// </summary>
    public static bool IsScopePart(string? value) =>
        value is { Length: > 0 and <= 128 } &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-' or ':');

    /// <summary>
    /// The approved release, normalized, or null when it is neither a dotted release nor the explicit
    /// <see cref="WorkerConfiguration.UnversionedSentinel"/> opt-in. An absent value is never the sentinel.
    /// </summary>
    public static string? Version(string? value)
    {
        string? trimmed = value?.Trim();
        if (trimmed is not { Length: > 0 and <= 40 })
        {
            return null;
        }

        if (string.Equals(trimmed, WorkerConfiguration.UnversionedSentinel, StringComparison.OrdinalIgnoreCase))
        {
            return WorkerConfiguration.UnversionedSentinel;
        }

        return trimmed.All(character => char.IsAsciiDigit(character) || character == '.') ? trimmed : null;
    }

    public static bool IsGuid(string? value) => Guid.TryParseExact(value, "D", out _);

    public static bool IsLoopbackHost(string host) =>
        string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) ||
        (System.Net.IPAddress.TryParse(host.Trim('[', ']'), out System.Net.IPAddress? address) &&
         System.Net.IPAddress.IsLoopback(address));

    public static bool SamePath(string left, string right) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    public static string? Directory(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 240)
        {
            return null;
        }

        string trimmed = value.Trim();
        if (!Path.IsPathFullyQualified(trimmed) || trimmed.IndexOfAny(['\0', '*', '?']) >= 0)
        {
            return null;
        }

        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(trimmed));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    /// <summary>Strips control characters and truncates, so a worker finding cannot rewrite a console.</summary>
    public static string Safe(string? value, int maximumCharacters)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        Span<char> buffer = stackalloc char[Math.Min(value.Length, maximumCharacters)];
        for (int index = 0; index < buffer.Length; index++)
        {
            buffer[index] = char.IsControl(value[index]) ? ' ' : value[index];
        }

        return new string(buffer).Trim();
    }
}
