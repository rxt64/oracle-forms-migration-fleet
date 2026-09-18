// Copyright (c) Microsoft. All rights reserved.

using System.Text;
using System.Text.Json;
using OracleFormsMigrationFleet.Hosting;

namespace OracleFormsMigrationFleet.Tests;

/// <summary>
/// The identity boundary, exercised through the exact types the host constructs.
///
/// None of these tests reach Azure or ASP.NET. The providers read headers through a delegate and the
/// options read configuration through a delegate, so what is under test here is the decision itself:
/// which envelopes this host is willing to call an authenticated operator, and which configurations it
/// is willing to start under.
/// </summary>
public class WorkbenchAuthenticationTests
{
    private const string Tenant = "8f1e2b42-6a1d-4a24-9ad2-6c1b6a8f3d71";
    private const string Client = "0ff0fa49-fce8-4801-ab93-e862a62fd6ab";
    private const string Issuer = "https://login.microsoftonline.com/8f1e2b42-6a1d-4a24-9ad2-6c1b6a8f3d71/v2.0";
    private const string ObjectId = "3b4c9a10-7d42-4f0e-9d51-2a61f0c4b8e3";

    private static WorkbenchConfigurationLookup Configuration(params (string Name, string Value)[] values) =>
        name => values.FirstOrDefault(entry => entry.Name == name).Value;

    private static WorkbenchAuthenticationOptions ContainerApps() =>
        WorkbenchAuthenticationOptions.Resolve(
            isDevelopmentEnvironment: false,
            Configuration(
                (WorkbenchAuthenticationOptions.ModeVariable, "ContainerApps"),
                (WorkbenchAuthenticationOptions.TenantVariable, Tenant),
                (WorkbenchAuthenticationOptions.ClientVariable, Client),
                (WorkbenchAuthenticationOptions.IssuerVariable, Issuer)));

    /// <summary>A platform envelope, with every claim overridable so one fact at a time can be wrong.</summary>
    private static string Envelope(
        string? authType = "Bearer",
        string? tenant = Tenant,
        string? audience = Client,
        string? issuer = Issuer,
        string? objectId = ObjectId,
        IEnumerable<string>? roles = null,
        bool malformedClaim = false,
        bool includeAuthType = true)
    {
        List<object> claims = [];

        void Add(string type, string? value)
        {
            if (value is not null)
            {
                claims.Add(new { typ = type, val = value });
            }
        }

        Add("tid", tenant);
        Add("aud", audience);
        Add("iss", issuer);
        Add("oid", objectId);

        foreach (string role in roles ?? [WorkbenchRoles.MigrationOperator])
        {
            claims.Add(new { typ = "roles", val = role });
        }

        if (malformedClaim)
        {
            claims.Add(new { typ = "roles", val = 42 });
        }

        Dictionary<string, object?> envelope = new()
        {
            ["name_typ"] = "name",
            ["role_typ"] = "roles",
            ["claims"] = claims,
        };
        if (includeAuthType)
        {
            envelope["auth_typ"] = authType;
        }

        string json = JsonSerializer.Serialize(envelope);
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
    }

    private static WorkbenchHeaderLookup Headers(string? principal, string? principalId = ObjectId, string? idp = "aad") =>
        name => name switch
        {
            WorkbenchPrincipalHeaders.Principal => principal,
            WorkbenchPrincipalHeaders.PrincipalId => principalId,
            WorkbenchPrincipalHeaders.IdentityProvider => idp,
            _ => null,
        };

    [Fact]
    public void Development_mode_is_the_default_only_inside_the_development_environment()
    {
        Assert.True(WorkbenchAuthenticationOptions.TryResolve(
            isDevelopmentEnvironment: true, Configuration(), out WorkbenchAuthenticationOptions? options, out _));
        Assert.Equal(WorkbenchAuthenticationMode.Development, options!.Mode);

        Assert.False(WorkbenchAuthenticationOptions.TryResolve(
            isDevelopmentEnvironment: false, Configuration(), out _, out string error));
        Assert.Contains(WorkbenchAuthenticationOptions.ModeVariable, error, StringComparison.Ordinal);
    }

    [Fact]
    public void Development_mode_is_refused_outside_the_development_environment()
    {
        Assert.False(WorkbenchAuthenticationOptions.TryResolve(
            isDevelopmentEnvironment: false,
            Configuration((WorkbenchAuthenticationOptions.ModeVariable, "Development")),
            out _,
            out string error));

        Assert.Contains("Development", error, StringComparison.Ordinal);

        Assert.Throws<InvalidOperationException>(() => WorkbenchAuthenticationOptions.Resolve(
            isDevelopmentEnvironment: false,
            Configuration((WorkbenchAuthenticationOptions.ModeVariable, "Development"))));
    }

    [Theory]
    [InlineData("Enabled")]
    [InlineData("true")]
    [InlineData("AzureAd")]
    public void An_unrecognised_mode_is_refused_rather_than_interpreted(string declared)
    {
        Assert.False(WorkbenchAuthenticationOptions.TryResolve(
            isDevelopmentEnvironment: true,
            Configuration((WorkbenchAuthenticationOptions.ModeVariable, declared)),
            out _,
            out string error));

        Assert.Contains(declared, error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(WorkbenchAuthenticationOptions.TenantVariable)]
    [InlineData(WorkbenchAuthenticationOptions.ClientVariable)]
    [InlineData(WorkbenchAuthenticationOptions.IssuerVariable)]
    public void Container_apps_mode_refuses_to_start_without_every_expected_identity_value(string missing)
    {
        (string, string)[] complete =
        [
            (WorkbenchAuthenticationOptions.ModeVariable, "ContainerApps"),
            (WorkbenchAuthenticationOptions.TenantVariable, Tenant),
            (WorkbenchAuthenticationOptions.ClientVariable, Client),
            (WorkbenchAuthenticationOptions.IssuerVariable, Issuer),
        ];

        Assert.False(WorkbenchAuthenticationOptions.TryResolve(
            isDevelopmentEnvironment: false,
            Configuration([.. complete.Where(entry => entry.Item1 != missing)]),
            out _,
            out string error));

        Assert.Contains(missing, error, StringComparison.Ordinal);
    }

    [Fact]
    public void A_non_https_issuer_is_refused()
    {
        Assert.False(WorkbenchAuthenticationOptions.TryResolve(
            isDevelopmentEnvironment: false,
            Configuration(
                (WorkbenchAuthenticationOptions.ModeVariable, "ContainerApps"),
                (WorkbenchAuthenticationOptions.TenantVariable, Tenant),
                (WorkbenchAuthenticationOptions.ClientVariable, Client),
                (WorkbenchAuthenticationOptions.IssuerVariable, "http://login.microsoftonline.com/common/v2.0")),
            out _,
            out string error));

        Assert.Contains(WorkbenchAuthenticationOptions.IssuerVariable, error, StringComparison.Ordinal);
    }

    [Fact]
    public void A_valid_principal_becomes_a_tenant_qualified_actor()
    {
        WorkbenchIdentityResult result = new ContainerAppsIdentityProvider(ContainerApps())
            .Authenticate(Headers(Envelope(roles: [WorkbenchRoles.MigrationOperator, WorkbenchRoles.SandboxApprover])));

        Assert.True(result.IsAuthenticated);
        Assert.Equal(Tenant, result.Actor!.TenantId);
        Assert.Equal(ObjectId, result.Actor.ObjectId);
        Assert.Equal($"{Tenant}:{ObjectId}", result.Actor.OwnerId);
        Assert.True(result.Actor.HasRole(WorkbenchRoles.MigrationOperator));
        Assert.True(result.Actor.HasRole(WorkbenchRoles.SandboxApprover));
    }

    [Fact]
    public void Entra_guid_casing_is_canonicalized_before_it_becomes_a_persistence_key()
    {
        string upperTenant = Tenant.ToUpperInvariant();
        string upperObjectId = ObjectId.ToUpperInvariant();

        WorkbenchIdentityResult result = new ContainerAppsIdentityProvider(ContainerApps())
            .Authenticate(Headers(
                Envelope(tenant: upperTenant, objectId: upperObjectId),
                principalId: upperObjectId));

        Assert.True(result.IsAuthenticated);
        Assert.Equal(Tenant, result.Actor!.TenantId);
        Assert.Equal(ObjectId, result.Actor.ObjectId);
        Assert.Equal($"{Tenant}:{ObjectId}", result.Actor.OwnerId);
    }

    [Fact]
    public void The_api_form_of_the_audience_is_accepted_without_extra_configuration()
    {
        Assert.True(new ContainerAppsIdentityProvider(ContainerApps())
            .Authenticate(Headers(Envelope(audience: $"api://{Client}")))
            .IsAuthenticated);
    }

    [Fact]
    public void The_container_apps_entra_provider_alias_is_accepted()
    {
        ContainerAppsIdentityProvider provider = new(ContainerApps());

        Assert.True(provider.Authenticate(Headers(
            Envelope(),
            idp: "azureactivedirectory")).IsAuthenticated);
    }

    [Fact]
    public void The_platform_idp_header_identifies_entra_when_the_reduced_envelope_omits_it()
    {
        ContainerAppsIdentityProvider provider = new(ContainerApps());

        WorkbenchIdentityResult empty = provider
            .Authenticate(Headers(
                Envelope(authType: string.Empty),
                idp: "azureactivedirectory"));
        WorkbenchIdentityResult absent = provider
            .Authenticate(Headers(
                Envelope(includeAuthType: false),
                idp: "azureactivedirectory"));
        WorkbenchIdentityResult nullValue = provider
            .Authenticate(Headers(
                Envelope(authType: null),
                idp: "azureactivedirectory"));

        Assert.True(empty.IsAuthenticated);
        Assert.True(absent.IsAuthenticated);
        Assert.True(nullValue.IsAuthenticated);
        Assert.Equal(Tenant, absent.Actor!.TenantId);
        Assert.Equal(ObjectId, absent.Actor.ObjectId);
    }

    [Fact]
    public void A_non_entra_envelope_cannot_be_overridden_by_the_idp_header()
    {
        Assert.False(new ContainerAppsIdentityProvider(ContainerApps())
            .Authenticate(Headers(Envelope(), idp: "github"))
            .IsAuthenticated);
    }

    [Fact]
    public void A_reduced_envelope_without_any_provider_identity_is_refused()
    {
        ContainerAppsIdentityProvider provider = new(ContainerApps());

        Assert.False(provider.Authenticate(Headers(Envelope(authType: string.Empty), idp: null)).IsAuthenticated);
        Assert.False(provider.Authenticate(Headers(Envelope(includeAuthType: false), idp: null)).IsAuthenticated);
        Assert.False(provider.Authenticate(Headers(Envelope(authType: null), idp: null)).IsAuthenticated);
    }

    public static TheoryData<string, string> RejectedEnvelopes() => new()
    {
        { "wrong tenant", Envelope(tenant: "11111111-2222-3333-4444-555555555555") },
        { "missing tenant", Envelope(tenant: null) },
        { "wrong audience", Envelope(audience: "api://some-other-application") },
        { "missing audience", Envelope(audience: null) },
        { "wrong issuer", Envelope(issuer: "https://login.microsoftonline.com/common/v2.0") },
        { "missing issuer", Envelope(issuer: null) },
        { "wrong authentication type", Envelope(authType: "Basic") },
        { "missing object id", Envelope(objectId: null) },
        { "object id that is not a directory identifier", Envelope(objectId: "operator@contoso.example") },
        { "malformed role claim", Envelope(malformedClaim: true) },
    };

    [Theory]
    [MemberData(nameof(RejectedEnvelopes))]
    public void An_envelope_this_deployment_did_not_expect_is_refused(string description, string envelope)
    {
        WorkbenchIdentityResult result = new ContainerAppsIdentityProvider(ContainerApps())
            .Authenticate(Headers(envelope));

        Assert.False(result.IsAuthenticated, description);
        Assert.Null(result.Actor);
        Assert.NotEqual(string.Empty, result.Reason);
    }

    [Fact]
    public void A_missing_or_undecodable_principal_is_refused()
    {
        ContainerAppsIdentityProvider provider = new(ContainerApps());

        Assert.False(provider.Authenticate(Headers(principal: null)).IsAuthenticated);
        Assert.False(provider.Authenticate(Headers(principal: "  ")).IsAuthenticated);
        Assert.False(provider.Authenticate(Headers(principal: "not-base64!!")).IsAuthenticated);
        Assert.False(provider.Authenticate(Headers(principal: Convert.ToBase64String(Encoding.UTF8.GetBytes("[]")))).IsAuthenticated);
        Assert.False(provider.Authenticate(Headers(principal: new string('A', 40_000))).IsAuthenticated);
    }

    /// <summary>
    /// The separate principal-ID header is what most application code reads. If it can disagree with the
    /// signed object claim then an attacker who can set one header and not the other picks the identity.
    /// </summary>
    [Fact]
    public void A_principal_id_header_that_disagrees_with_the_object_claim_is_refused()
    {
        ContainerAppsIdentityProvider provider = new(ContainerApps());

        Assert.False(provider.Authenticate(Headers(Envelope(), principalId: "someone-else")).IsAuthenticated);
        Assert.False(provider.Authenticate(Headers(Envelope(), principalId: null)).IsAuthenticated);
    }

    [Fact]
    public void A_declared_identity_provider_other_than_entra_is_refused()
    {
        Assert.False(new ContainerAppsIdentityProvider(ContainerApps())
            .Authenticate(Headers(Envelope(), idp: "github"))
            .IsAuthenticated);
    }

    /// <summary>
    /// A repeated identity claim is ambiguous. Taking the first one would let an envelope carry both the
    /// expected tenant and another one and be accepted on the strength of claim ordering.
    /// </summary>
    [Fact]
    public void A_repeated_and_contradictory_tenant_claim_is_refused()
    {
        string json = JsonSerializer.Serialize(new
        {
            auth_typ = "aad",
            claims = new object[]
            {
                new { typ = "tid", val = Tenant },
                new { typ = "tid", val = "11111111-2222-3333-4444-555555555555" },
                new { typ = "aud", val = Client },
                new { typ = "iss", val = Issuer },
                new { typ = "oid", val = ObjectId },
            },
        });

        Assert.False(new ContainerAppsIdentityProvider(ContainerApps())
            .Authenticate(Headers(Convert.ToBase64String(Encoding.UTF8.GetBytes(json))))
            .IsAuthenticated);
    }

    [Fact]
    public void Development_actors_are_isolated_from_each_other_and_from_any_azure_tenant()
    {
        DevelopmentIdentityProvider provider = new(WorkbenchAuthenticationOptions.DevelopmentObjectId);

        WorkbenchActor first = provider.Authenticate(name =>
            name == WorkbenchPrincipalHeaders.PrincipalId ? "operator-a" : null).Actor!;
        WorkbenchActor second = provider.Authenticate(name =>
            name == WorkbenchPrincipalHeaders.PrincipalId ? "operator-b" : null).Actor!;
        WorkbenchActor fallback = provider.Authenticate(_ => null).Actor!;

        Assert.NotEqual(first.OwnerId, second.OwnerId);
        Assert.Equal(WorkbenchAuthenticationOptions.DevelopmentTenantId, first.TenantId);
        Assert.False(Guid.TryParse(first.TenantId, out _));
        Assert.Equal(WorkbenchAuthenticationOptions.DevelopmentObjectId, fallback.ObjectId);
    }

    /// <summary>
    /// Development is still an identity boundary. A request that names no actor is unauthenticated,
    /// exactly as it would be in a deployment, unless the host was explicitly configured with a default.
    /// </summary>
    [Fact]
    public void A_development_request_that_names_no_actor_is_unauthenticated_by_default()
    {
        Assert.False(new DevelopmentIdentityProvider().Authenticate(_ => null).IsAuthenticated);
        Assert.False(new DevelopmentIdentityProvider()
            .Authenticate(name => name == WorkbenchPrincipalHeaders.PrincipalId ? "   " : null)
            .IsAuthenticated);

        Assert.True(WorkbenchAuthenticationOptions.TryResolve(
            isDevelopmentEnvironment: true,
            Configuration((WorkbenchAuthenticationOptions.DevelopmentActorVariable, "local-development")),
            out WorkbenchAuthenticationOptions? options,
            out _));
        Assert.Equal("local-development", options!.DefaultDevelopmentObjectId);
    }

    /// <summary>
    /// A development actor can reach the sandbox approval flow and can never reach production. There is
    /// no configuration that adds the production role to a development actor.
    /// </summary>
    [Fact]
    public void Development_actors_never_hold_production_scope()
    {
        WorkbenchActor actor = new DevelopmentIdentityProvider("local-development").Authenticate(_ => null).Actor!;

        Assert.True(actor.HasRole(WorkbenchRoles.MigrationOperator));
        Assert.True(actor.HasRole(WorkbenchRoles.SandboxApprover));
        Assert.False(actor.HasRole(WorkbenchRoles.ProductionApprover));
        Assert.DoesNotContain(
            WorkbenchAuthenticationOptions.DevelopmentRoles,
            role => string.Equals(role, WorkbenchRoles.ProductionApprover, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void A_development_actor_identifier_cannot_forge_a_tenant_qualified_owner()
    {
        // ':' separates tenant from object in an owner identifier, so allowing it in a development actor
        // name would let one development actor claim to be another tenant's principal.
        Assert.False(new DevelopmentIdentityProvider()
            .Authenticate(name => name == WorkbenchPrincipalHeaders.PrincipalId ? $"{Tenant}:{ObjectId}" : null)
            .IsAuthenticated);
    }

    [Fact]
    public void A_deployed_target_refuses_to_start_with_undeclared_azure_coordinates()
    {
        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() =>
            PlatformTargetProfileEnvironment.Read(Configuration(), "Production"));

        Assert.Contains("SANDBOX_AZURE_TENANT_ID", exception.Message, StringComparison.Ordinal);
        Assert.Contains("SANDBOX_AZURE_RESOURCE_ID", exception.Message, StringComparison.Ordinal);

        PlatformTargetProfileEnvironment development =
            PlatformTargetProfileEnvironment.Read(Configuration(), "Development");
        Assert.Equal(PlatformTargetProfileEnvironment.Undeclared, development.AzureTenantId);

        PlatformTargetProfileEnvironment planningOnly = PlatformTargetProfileEnvironment.Read(
            Configuration(), "Production", requireSandboxCoordinates: false);
        Assert.Equal(PlatformTargetProfileEnvironment.Undeclared, planningOnly.ResourceId);
    }
}
