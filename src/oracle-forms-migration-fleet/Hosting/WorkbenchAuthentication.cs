// Copyright (c) Microsoft. All rights reserved.

using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace OracleFormsMigrationFleet.Hosting;

/// <summary>
/// How this process establishes who is calling. There are exactly two answers, and the host must name
/// one: a boolean switch cannot express the difference between "no authentication is configured" and
/// "authentication is deliberately the isolated development one".
/// </summary>
public enum WorkbenchAuthenticationMode
{
    /// <summary>Isolated local actors under a non-Azure tenant. Legal only in the Development environment.</summary>
    Development,

    /// <summary>Azure Container Apps built-in authentication, validated claim by claim.</summary>
    ContainerApps,
}

/// <summary>Reads one environment value. Kept as a delegate so the rules are testable without a process.</summary>
public delegate string? WorkbenchConfigurationLookup(string name);

/// <summary>Reads one request header. Kept as a delegate so identity is testable without ASP.NET.</summary>
public delegate string? WorkbenchHeaderLookup(string name);

/// <summary>
/// The authentication configuration this process will run under, after validation.
///
/// Construction is the validation: there is no partially configured instance. A deployment that names
/// <see cref="WorkbenchAuthenticationMode.ContainerApps"/> without the exact tenant, client, audience,
/// and issuer it expects has not configured authentication, and the host refuses to start rather than
/// accepting whatever claims arrive.
/// </summary>
public sealed record WorkbenchAuthenticationOptions
{
    public const string ModeVariable = "WORKBENCH_AUTH_MODE";
    public const string TenantVariable = "WORKBENCH_AUTH_TENANT_ID";
    public const string ClientVariable = "WORKBENCH_AUTH_CLIENT_ID";
    public const string AudienceVariable = "WORKBENCH_AUTH_AUDIENCE";
    public const string IssuerVariable = "WORKBENCH_AUTH_ISSUER";

    /// <summary>Names the one development actor a request with no principal header is treated as.</summary>
    public const string DevelopmentActorVariable = "WORKBENCH_DEV_ACTOR";

    /// <summary>
    /// Tenant identifier development actors live under. It is not a GUID on purpose: nothing that
    /// reaches Azure can be addressed by it, so a development grant can never name a real tenant.
    /// </summary>
    public const string DevelopmentTenantId = "development.localhost";

    /// <summary>Object identifier used when a development request names no actor of its own.</summary>
    public const string DevelopmentObjectId = "local-development";
    /// <summary>
    /// Roles a development actor holds. Sandbox approval is included so the approval state machine is
    /// reachable offline; production approval never is, because a development actor must not be able to
    /// obtain production-write scope by any path.
    /// </summary>
    public static IReadOnlyList<string> DevelopmentRoles { get; } =
        [WorkbenchRoles.MigrationOperator, WorkbenchRoles.SandboxApprover];

    public required WorkbenchAuthenticationMode Mode { get; init; }

    public string ExpectedTenantId { get; init; } = string.Empty;

    public string ExpectedClientId { get; init; } = string.Empty;

    public IReadOnlyList<string> ExpectedAudiences { get; init; } = [];

    public string ExpectedIssuer { get; init; } = string.Empty;

    /// <summary>
    /// The development actor an unnamed request becomes, or empty when no default was configured.
    ///
    /// Empty is the safer default and the one tests run under: a request that names no actor is then
    /// unauthenticated in Development exactly as it would be in a deployment, rather than silently
    /// becoming an operator.
    /// </summary>
    public string DefaultDevelopmentObjectId { get; init; } = string.Empty;

    public bool IsDevelopment => Mode == WorkbenchAuthenticationMode.Development;

    /// <summary>Resolves and validates the configuration, or throws with the exact missing piece.</summary>
    public static WorkbenchAuthenticationOptions Resolve(bool isDevelopmentEnvironment, WorkbenchConfigurationLookup configuration)
    {
        if (!TryResolve(isDevelopmentEnvironment, configuration, out WorkbenchAuthenticationOptions? options, out string error))
        {
            throw new InvalidOperationException(error);
        }

        return options;
    }

    /// <summary>
    /// Validates the configuration without throwing.
    ///
    /// The mode is never inferred from the presence of other settings. An unset mode is only acceptable
    /// in the Development environment, where it means Development; anywhere else an unset mode is a
    /// deployment that forgot to say how it authenticates, which is not a deployment that should serve.
    /// </summary>
    public static bool TryResolve(
        bool isDevelopmentEnvironment,
        WorkbenchConfigurationLookup configuration,
        [NotNullWhen(true)] out WorkbenchAuthenticationOptions? options,
        out string error)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        options = null;
        string? declared = configuration(ModeVariable)?.Trim();

        WorkbenchAuthenticationMode mode;
        if (string.IsNullOrEmpty(declared))
        {
            if (!isDevelopmentEnvironment)
            {
                error =
                    $"{ModeVariable} must be set to '{nameof(WorkbenchAuthenticationMode.ContainerApps)}' outside the " +
                    "Development environment. This host will not serve requests without knowing how it authenticates them.";
                return false;
            }

            mode = WorkbenchAuthenticationMode.Development;
        }
        else if (!Enum.TryParse(declared, ignoreCase: true, out mode) || !Enum.IsDefined(mode))
        {
            error =
                $"{ModeVariable} was '{declared}'. The only supported values are " +
                $"'{nameof(WorkbenchAuthenticationMode.Development)}' and '{nameof(WorkbenchAuthenticationMode.ContainerApps)}'.";
            return false;
        }

        if (mode == WorkbenchAuthenticationMode.Development)
        {
            if (!isDevelopmentEnvironment)
            {
                error =
                    $"{ModeVariable}={nameof(WorkbenchAuthenticationMode.Development)} is only valid when the ASP.NET " +
                    "environment is Development. Development actors are self-asserted, so honouring them anywhere else " +
                    "would make every caller an operator.";
                return false;
            }

            options = new WorkbenchAuthenticationOptions
            {
                Mode = WorkbenchAuthenticationMode.Development,
                DefaultDevelopmentObjectId = configuration(DevelopmentActorVariable)?.Trim() ?? string.Empty,
            };
            error = string.Empty;
            return true;
        }

        string tenant = configuration(TenantVariable)?.Trim() ?? string.Empty;
        string client = configuration(ClientVariable)?.Trim() ?? string.Empty;
        string issuer = configuration(IssuerVariable)?.Trim() ?? string.Empty;
        string audience = configuration(AudienceVariable)?.Trim() ?? string.Empty;

        if (!Guid.TryParse(tenant, out _))
        {
            error = $"{TenantVariable} must be the GUID of the one tenant this deployment accepts sign-ins from.";
            return false;
        }

        if (!Guid.TryParse(client, out _))
        {
            error = $"{ClientVariable} must be the GUID of the Entra application Container Apps authentication uses.";
            return false;
        }

        if (!Uri.TryCreate(issuer, UriKind.Absolute, out Uri? issuerUri) ||
            !string.Equals(issuerUri.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal))
        {
            error = $"{IssuerVariable} must be the absolute https issuer URL the platform validates tokens against.";
            return false;
        }

        // Container Apps presents either form depending on how the application exposes itself, so both
        // are expected values rather than a choice the deployment has to get right twice.
        List<string> audiences = string.IsNullOrEmpty(audience)
            ? [client, $"api://{client}"]
            : [.. audience.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];

        if (audiences.Count == 0)
        {
            error = $"{AudienceVariable} was set but named no audience.";
            return false;
        }

        options = new WorkbenchAuthenticationOptions
        {
            Mode = WorkbenchAuthenticationMode.ContainerApps,
            ExpectedTenantId = tenant,
            ExpectedClientId = client,
            ExpectedAudiences = audiences,
            ExpectedIssuer = issuer,
        };

        error = string.Empty;
        return true;
    }
}

/// <summary>Whether a request carried an identity this host trusts, and the reason when it did not.</summary>
public sealed record WorkbenchIdentityResult(bool IsAuthenticated, WorkbenchActor? Actor, string Reason)
{
    public static WorkbenchIdentityResult Deny(string reason) => new(false, null, reason);

    public static WorkbenchIdentityResult Allow(WorkbenchActor actor) => new(true, actor, "Authenticated.");
}

/// <summary>Establishes the caller from request headers and from nothing else.</summary>
public interface IWorkbenchIdentityProvider
{
    WorkbenchAuthenticationMode Mode { get; }

    WorkbenchIdentityResult Authenticate(WorkbenchHeaderLookup header);
}

/// <summary>
/// Isolated actors for local work.
///
/// The actor identifier is taken from the same header the platform would set, so a developer can drive
/// two distinct operators without a sign-in. It is deliberately not a production identity: the tenant is
/// a reserved non-Azure name, so every grant a development actor obtains is scoped to a tenant that does
/// not exist in Azure and can never match a Container Apps principal.
/// </summary>
public sealed class DevelopmentIdentityProvider(string? defaultObjectId = null) : IWorkbenchIdentityProvider
{
    public WorkbenchAuthenticationMode Mode => WorkbenchAuthenticationMode.Development;

    public WorkbenchIdentityResult Authenticate(WorkbenchHeaderLookup header)
    {
        ArgumentNullException.ThrowIfNull(header);

        string declared = header(WorkbenchPrincipalHeaders.PrincipalId)?.Trim() ?? string.Empty;
        string objectId = declared.Length > 0 ? declared : (defaultObjectId ?? string.Empty).Trim();

        // No named actor is unauthenticated here for the same reason it is in a deployment: an operator
        // who was never identified cannot own a workspace or hold a grant.
        if (objectId.Length == 0)
        {
            return WorkbenchIdentityResult.Deny("The request named no development actor.");
        }

        if (objectId.Length > 128 || objectId.Any(character => char.IsControl(character) || character == ':'))
        {
            return WorkbenchIdentityResult.Deny("The development actor identifier is not a usable name.");
        }

        return WorkbenchIdentityResult.Allow(WorkbenchActor.ForTenant(
            WorkbenchAuthenticationOptions.DevelopmentTenantId,
            objectId,
            WorkbenchAuthenticationOptions.DevelopmentRoles));
    }
}

/// <summary>Header names Azure Container Apps built-in authentication sets on every inbound request.</summary>
public static class WorkbenchPrincipalHeaders
{
    public const string PrincipalId = "X-MS-CLIENT-PRINCIPAL-ID";
    public const string Principal = "X-MS-CLIENT-PRINCIPAL";
    public const string IdentityProvider = "X-MS-CLIENT-PRINCIPAL-IDP";
}

/// <summary>
/// Validates the Container Apps principal envelope claim by claim.
///
/// The platform terminates the token and hands the backend a base64 envelope. This host treats that
/// envelope as an assertion to be checked rather than a fact: the declared authentication type, the
/// issuer, the audience, the tenant, and the object identifier must all be exactly what this deployment
/// was configured to accept, and the separate principal-ID header must agree with the object claim.
/// Anything missing, malformed, or different denies, because an envelope this code cannot fully account
/// for is one that may not have come from the platform at all.
///
/// Raw JWT validation is deliberately absent. The platform proxy already performed it, and a second
/// half-implemented validator that accepts a caller-supplied bearer token would be a way around the
/// proxy rather than a defence in depth.
/// </summary>
public sealed class ContainerAppsIdentityProvider(WorkbenchAuthenticationOptions options) : IWorkbenchIdentityProvider
{
    private const int MaxEnvelopeCharacters = 32_768;

    private static readonly string[] s_tenantClaims =
        ["tid", "http://schemas.microsoft.com/identity/claims/tenantid"];

    private static readonly string[] s_objectClaims =
        ["oid", "http://schemas.microsoft.com/identity/claims/objectidentifier"];

    private static readonly string[] s_roleClaims =
        ["roles", "role", "http://schemas.microsoft.com/ws/2008/06/identity/claims/role"];

    public WorkbenchAuthenticationMode Mode => WorkbenchAuthenticationMode.ContainerApps;

    public WorkbenchIdentityResult Authenticate(WorkbenchHeaderLookup header)
    {
        ArgumentNullException.ThrowIfNull(header);

        string? encoded = header(WorkbenchPrincipalHeaders.Principal);
        if (string.IsNullOrWhiteSpace(encoded))
        {
            return WorkbenchIdentityResult.Deny("The request carried no platform principal.");
        }

        if (encoded.Length > MaxEnvelopeCharacters)
        {
            return WorkbenchIdentityResult.Deny("The platform principal was larger than this host will read.");
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(Convert.FromBase64String(encoded));
        }
        catch (Exception exception) when (exception is FormatException or JsonException)
        {
            return WorkbenchIdentityResult.Deny("The platform principal could not be decoded.");
        }

        using (document)
        {
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return WorkbenchIdentityResult.Deny("The platform principal was not an identity envelope.");
            }

            if (!root.TryGetProperty("auth_typ", out JsonElement authType) ||
                authType.ValueKind != JsonValueKind.String ||
                !IsAad(authType.GetString()))
            {
                return WorkbenchIdentityResult.Deny("The platform principal did not declare Microsoft Entra authentication.");
            }

            string? providerHeader = header(WorkbenchPrincipalHeaders.IdentityProvider);
            if (!string.IsNullOrWhiteSpace(providerHeader) && !IsAad(providerHeader.Trim()))
            {
                return WorkbenchIdentityResult.Deny("The request named an identity provider this host does not accept.");
            }

            if (!TryReadClaims(root, out List<(string Type, string Value)>? claims))
            {
                return WorkbenchIdentityResult.Deny("The platform principal carried a malformed claim set.");
            }

            string tenant = First(claims, s_tenantClaims);
            if (!Guid.TryParse(tenant, out Guid tenantId) ||
                !string.Equals(tenant, options.ExpectedTenantId, StringComparison.OrdinalIgnoreCase))
            {
                return WorkbenchIdentityResult.Deny("The principal was issued for a tenant this deployment does not accept.");
            }

            string issuer = First(claims, ["iss"]);
            if (!string.Equals(issuer, options.ExpectedIssuer, StringComparison.Ordinal))
            {
                return WorkbenchIdentityResult.Deny("The principal was issued by an authority this deployment does not accept.");
            }

            string audience = First(claims, ["aud"]);
            if (audience.Length == 0 ||
                !options.ExpectedAudiences.Any(expected => string.Equals(expected, audience, StringComparison.OrdinalIgnoreCase)))
            {
                return WorkbenchIdentityResult.Deny("The principal was issued for an audience this deployment does not accept.");
            }

            string objectId = First(claims, s_objectClaims);
            if (!Guid.TryParse(objectId, out Guid directoryObjectId))
            {
                return WorkbenchIdentityResult.Deny("The principal carried no usable object identifier.");
            }

            string principalId = header(WorkbenchPrincipalHeaders.PrincipalId)?.Trim() ?? string.Empty;
            if (!string.Equals(principalId, objectId, StringComparison.OrdinalIgnoreCase))
            {
                return WorkbenchIdentityResult.Deny("The principal identifier header disagreed with the signed object identifier.");
            }

            List<string> roles = [];
            foreach ((string type, string value) in claims)
            {
                if (!s_roleClaims.Contains(type, StringComparer.Ordinal))
                {
                    continue;
                }

                if (value.Length is 0 or > 256)
                {
                    return WorkbenchIdentityResult.Deny("The principal carried a role claim this host will not read.");
                }

                if (!roles.Contains(value, StringComparer.OrdinalIgnoreCase))
                {
                    roles.Add(value);
                }
            }

            return WorkbenchIdentityResult.Allow(WorkbenchActor.ForTenant(
                tenantId.ToString("D"),
                directoryObjectId.ToString("D"),
                roles));
        }
    }

    private static bool IsAad(string? value) =>
        string.Equals(value, "aad", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Reads the claim array whole, refusing any entry that is not a string type and a string value.
    /// A partially readable claim set is treated as unreadable: skipping the parts that do not parse is
    /// how a forged envelope hides the claim it did not want checked.
    /// </summary>
    private static bool TryReadClaims(JsonElement root, [NotNullWhen(true)] out List<(string Type, string Value)>? claims)
    {
        claims = null;
        if (!root.TryGetProperty("claims", out JsonElement array) || array.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        List<(string, string)> read = [];
        foreach (JsonElement claim in array.EnumerateArray())
        {
            if (claim.ValueKind != JsonValueKind.Object ||
                !claim.TryGetProperty("typ", out JsonElement type) || type.ValueKind != JsonValueKind.String ||
                !claim.TryGetProperty("val", out JsonElement value) || value.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            read.Add((type.GetString() ?? string.Empty, value.GetString() ?? string.Empty));
        }

        claims = read;
        return true;
    }

    /// <summary>
    /// The single value for a claim, or empty when it is absent or repeated with different values.
    /// A repeated identity claim is ambiguous, and an ambiguous tenant or audience is a denial.
    /// </summary>
    private static string First(List<(string Type, string Value)> claims, IReadOnlyList<string> names)
    {
        string? found = null;
        foreach ((string type, string value) in claims)
        {
            if (!names.Contains(type, StringComparer.Ordinal))
            {
                continue;
            }

            if (found is not null && !string.Equals(found, value, StringComparison.Ordinal))
            {
                return string.Empty;
            }

            found = value;
        }

        return found ?? string.Empty;
    }
}
