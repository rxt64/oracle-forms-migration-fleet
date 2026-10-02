// Copyright (c) Microsoft. All rights reserved.

using OracleFormsMigrationFleet.Fleet.Execution;
using OracleFormsMigrationFleet.Hosting;

namespace OracleFormsMigrationFleet.Tests;

/// <summary>
/// The gateway-backed source-environment probe, which is the first probe in this product that can report
/// a capability as actually exercised rather than as untried.
///
/// Every test here is a way a confused or compromised gateway could make this host record something about
/// a customer's environment that is not true of it. The verified case is last and is the only one that
/// produces a verified readiness, and it only does so on a response that matches the request in every
/// correlation field and observes exactly the releases the profile declares.
/// </summary>
public sealed class GatewaySourceEnvironmentProbeTests
{
    private static readonly DateTimeOffset s_created = new(2026, 9, 30, 11, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset s_now = new(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Without_a_configured_gateway_the_host_keeps_the_probe_that_reports_nothing_was_tried() =>
        Assert.IsType<UnavailableSourceEnvironmentProbe>(SourceEnvironmentProbeSelection.Create(null));

    [Fact]
    public void With_a_configured_gateway_the_host_probes_through_it() =>
        Assert.IsType<GatewaySourceEnvironmentProbe>(SourceEnvironmentProbeSelection.Create(new StubProbeGateway()));

    [Fact]
    public async Task The_request_carries_only_server_owned_identifiers_and_the_profile_scope()
    {
        StubProbeGateway gateway = new();
        SourceEnvironmentProfile profile = Profile();

        await Probe(gateway).ProbeAsync(profile, CancellationToken.None);

        SourceGatewayProbeRequest request = Assert.Single(gateway.Requests);
        Assert.Equal(SourceGatewayProtocol.SchemaVersion, request.SchemaVersion);
        Assert.Equal(profile.SourceEnvironmentId, request.SourceEnvironmentId);
        Assert.Equal(profile.Version, request.ProfileVersion);
        Assert.Equal(profile.CanonicalHash, request.ProfileHash);
        Assert.Equal(profile.ExpectedFormsVersion, request.ExpectedFormsRelease);
        Assert.Equal(profile.ExpectedDatabaseVersion, request.ExpectedDatabaseRelease);
        Assert.Equal(profile.TenantId, request.Scope.TenantId);
        Assert.Equal(profile.ProjectId, request.Scope.ProjectId);

        // A request shape with nowhere to put a path, a connect string or a credential is the point: the
        // gateway resolves every location from its own registry, so the untrusted profile cannot redirect it.
        Assert.Equal(7, typeof(SourceGatewayProbeRequest).GetProperties().Length);
    }

    [Fact]
    public async Task A_transport_failure_is_recorded_as_a_refusal_and_claims_nothing_about_the_source()
    {
        StubProbeGateway gateway = new() { TransportError = "The source gateway could not be reached." };

        SourceEnvironmentProbeResult result = await Probe(gateway).ProbeAsync(Profile(), CancellationToken.None);

        Assert.Equal(SourceEnvironmentProbeStatus.Rejected, result.Status);
        Assert.Null(result.Observed.Forms);
        Assert.Null(result.Observed.Database);
        Assert.Equal("source.environment.probe", Assert.Single(result.Capabilities).Id);
        Assert.Contains("could not be reached", Assert.Single(result.Contradictions), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("environment")]
    [InlineData("forms-release")]
    [InlineData("database-release")]
    [InlineData("profile-version")]
    [InlineData("profile-hash")]
    [InlineData("tenant")]
    [InlineData("project")]
    public async Task A_response_bound_to_another_source_profile_or_scope_is_refused(string drift)
    {
        SourceEnvironmentProfile profile = Profile();
        StubProbeGateway gateway = new()
        {
            Rewrite = response => drift switch
            {
                "environment" => response with { SourceEnvironmentId = "another-environment" },
                "forms-release" => response with { ExpectedFormsRelease = "10g" },
                "database-release" => response with { ExpectedDatabaseRelease = "11g" },
                "profile-version" => response with { ProfileVersion = response.ProfileVersion + 1 },
                "profile-hash" => response with { ProfileHash = new string('a', 64) },
                "tenant" => response with { Scope = new SourceGatewayAuthorizationScope("other-tenant", profile.ProjectId) },
                _ => response with { Scope = new SourceGatewayAuthorizationScope(profile.TenantId, "other-project") },
            },
        };

        SourceEnvironmentProbeResult result = await Probe(gateway).ProbeAsync(profile, CancellationToken.None);

        Assert.Equal(SourceEnvironmentProbeStatus.Rejected, result.Status);
        Assert.Contains("not correlated", Assert.Single(result.Contradictions), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_response_from_outside_the_clock_skew_window_is_refused()
    {
        StubProbeGateway gateway = new()
        {
            Rewrite = response => response with { ProbedUtc = s_now.AddHours(-2) },
        };

        SourceEnvironmentProbeResult result = await Probe(gateway).ProbeAsync(Profile(), CancellationToken.None);

        Assert.Equal(SourceEnvironmentProbeStatus.Rejected, result.Status);
        Assert.Contains("clock-skew", Assert.Single(result.Contradictions), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("state")]
    [InlineData("prerequisite")]
    [InlineData("identifier")]
    [InlineData("duplicate")]
    public async Task A_capability_this_build_cannot_read_refuses_the_whole_response(string defect)
    {
        StubProbeGateway gateway = new()
        {
            Rewrite = response => response with
            {
                Capabilities = defect switch
                {
                    "state" => [Capability("forms.installation", "Working", "OracleFormsInstallation")],
                    "prerequisite" => [Capability("forms.installation", "Verified", "AnythingAtAll")],
                    "identifier" => [Capability("Forms Installation", "Verified", "OracleFormsInstallation")],
                    _ =>
                    [
                        Capability("forms.installation", "Verified", "OracleFormsInstallation"),
                        Capability("forms.installation", "Verified", "OracleFormsInstallation"),
                    ],
                },
            },
        };

        SourceEnvironmentProbeResult result = await Probe(gateway).ProbeAsync(Profile(), CancellationToken.None);

        Assert.Equal(SourceEnvironmentProbeStatus.Rejected, result.Status);
        Assert.Contains("capability", Assert.Single(result.Contradictions), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_verified_claim_a_capability_does_not_support_is_refused_rather_than_repeated()
    {
        StubProbeGateway gateway = new()
        {
            Rewrite = response => response with
            {
                Status = SourceGatewayProbeStatus.Verified,
                ObservedFormsRelease = "6i",
                ObservedDatabaseRelease = "9i",
                Capabilities =
                [
                    Capability("forms.installation", "Verified", "OracleFormsInstallation"),
                    Capability("forms.openapi.load", "Verified", "OracleFormsOpenApiLibraries"),
                    Capability("oracle.schema.extract", "BlockedPrerequisite", "OracleClientConnectivity"),
                ],
            },
        };

        SourceEnvironmentProbeResult result = await Probe(gateway).ProbeAsync(Profile(), CancellationToken.None);

        Assert.Equal(SourceEnvironmentProbeStatus.Rejected, result.Status);
        Assert.Contains("verified", Assert.Single(result.Contradictions), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_verified_claim_with_an_unobserved_release_is_refused()
    {
        StubProbeGateway gateway = new()
        {
            Rewrite = response => response with
            {
                Status = SourceGatewayProbeStatus.Verified,
                ObservedFormsRelease = "6i",
                ObservedDatabaseRelease = null,
                Capabilities =
                [
                    Capability("forms.installation", "Verified", "OracleFormsInstallation"),
                    Capability("forms.openapi.load", "Verified", "OracleFormsOpenApiLibraries"),
                    Capability("oracle.schema.extract", "Verified", "OracleClientConnectivity"),
                ],
            },
        };

        SourceEnvironmentProbeResult result = await Probe(gateway).ProbeAsync(Profile(), CancellationToken.None);

        Assert.Equal(SourceEnvironmentProbeStatus.Rejected, result.Status);
    }

    [Fact]
    public async Task A_blocked_claim_that_names_no_prerequisite_is_refused()
    {
        StubProbeGateway gateway = new()
        {
            Rewrite = response => response with
            {
                Status = SourceGatewayProbeStatus.BlockedPrerequisite,
                Capabilities = [Capability("forms.installation", "Verified", "OracleFormsInstallation")],
            },
        };

        SourceEnvironmentProbeResult result = await Probe(gateway).ProbeAsync(Profile(), CancellationToken.None);

        Assert.Equal(SourceEnvironmentProbeStatus.Rejected, result.Status);
        Assert.Contains("prerequisite", Assert.Single(result.Contradictions), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_missing_Oracle_connection_is_reported_as_the_blocked_prerequisite_it_is()
    {
        StubProbeGateway gateway = new();

        SourceEnvironmentProbeResult result = await Probe(gateway).ProbeAsync(Profile(), CancellationToken.None);

        Assert.Equal(SourceEnvironmentProbeStatus.BlockedPrerequisite, result.Status);
        Assert.Equal(SourcePrerequisite.OracleClientConnectivity, Assert.Single(result.BlockedPrerequisites));

        SourceCapabilityResult forms = result.Capabilities.Single(capability => capability.Id == "forms.installation");
        Assert.Equal(SourceEnvironmentProbeStatus.Verified, forms.State);

        // Nothing opened an Oracle connection, so nothing may claim to have observed a database release.
        Assert.Null(result.Observed.Database);
        Assert.Null(result.Observed.Forms);
    }

    [Fact]
    public async Task A_blocked_probe_is_storable_against_the_profile_it_was_asked_about()
    {
        SourceEnvironmentProfile profile = Profile();

        SourceEnvironmentProbeResult result = await Probe(new StubProbeGateway()).ProbeAsync(profile, CancellationToken.None);
        SourceEnvironmentProfile stored = SourceEnvironmentProfiles.RecordProbe(profile, result, s_now);

        Assert.Equal(SourceEnvironmentReadiness.BlockedPrerequisite, stored.Readiness);
        Assert.Null(stored.LastVerifiedUtc);
        Assert.Equal(profile.Version + 1, stored.Version);
    }

    [Fact]
    public async Task A_refused_probe_is_storable_and_records_the_refusal()
    {
        SourceEnvironmentProfile profile = Profile();
        StubProbeGateway gateway = new() { TransportError = "The source gateway could not be reached." };

        SourceEnvironmentProbeResult result = await Probe(gateway).ProbeAsync(profile, CancellationToken.None);
        SourceEnvironmentProfile stored = SourceEnvironmentProfiles.RecordProbe(profile, result, s_now);

        Assert.Equal(SourceEnvironmentReadiness.Rejected, stored.Readiness);
        Assert.Single(stored.LastContradictions);
    }

    [Fact]
    public async Task A_cancelled_probe_never_reaches_the_gateway()
    {
        StubProbeGateway gateway = new();
        using CancellationTokenSource cancellation = new();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Probe(gateway).ProbeAsync(Profile(), cancellation.Token));

        Assert.Empty(gateway.Requests);
    }

    [Fact]
    public async Task A_verified_claim_that_covers_only_the_Forms_half_is_refused()
    {
        SourceEnvironmentProfile profile = Profile();
        StubProbeGateway gateway = new()
        {
            Rewrite = response => response with
            {
                Status = SourceGatewayProbeStatus.Verified,
                ObservedFormsRelease = profile.ExpectedFormsVersion,
                ObservedDatabaseRelease = profile.ExpectedDatabaseVersion,
                Capabilities =
                [
                    Capability("forms.installation", "Verified", "OracleFormsInstallation"),
                    Capability("forms.openapi.load", "Verified", "OracleFormsOpenApiLibraries"),
                ],
            },
        };

        SourceEnvironmentProbeResult result = await Probe(gateway).ProbeAsync(profile, CancellationToken.None);

        // Nothing read the Oracle catalog, so a release the profile declares is not evidence it was reached.
        Assert.Equal(SourceEnvironmentProbeStatus.Rejected, result.Status);
        Assert.Contains("oracle.schema.extract", Assert.Single(result.Contradictions), StringComparison.Ordinal);
        Assert.Null(result.Observed.Forms);
        Assert.Null(result.Observed.Database);

        SourceEnvironmentProfile stored = SourceEnvironmentProfiles.RecordProbe(profile, result, s_now);
        Assert.Equal(SourceEnvironmentReadiness.Rejected, stored.Readiness);
        Assert.Null(stored.LastVerifiedUtc);
    }

    [Theory]
    [InlineData("RegisteredSourceEnvironment", "serves no source environment")]
    [InlineData("AuthorizedSourceEnvironmentScope", "does not authorize this tenant and project")]
    [InlineData("ApprovedSourceProfileVersion", "has not approved this source profile version")]
    public async Task A_gateway_refusal_keeps_its_actionable_prerequisite_without_echoing_the_gateways_prose(
        string prerequisite,
        string expected)
    {
        StubProbeGateway gateway = new()
        {
            Rewrite = response => response with
            {
                Status = SourceGatewayProbeStatus.Rejected,
                Capabilities =
                [
                    new("source.environment.probe", "Rejected", prerequisite, "6i", null, null,
                        "operator.register.source.environment.on.gateway", null),
                ],
                Findings = ["entry=legacy-order-entry;callerTenant=other-tenant;registeredTenant=tenant-a"],
            },
        };

        SourceEnvironmentProbeResult result = await Probe(gateway).ProbeAsync(Profile(), CancellationToken.None);

        Assert.Equal(SourceEnvironmentProbeStatus.Rejected, result.Status);
        string contradiction = Assert.Single(result.Contradictions);
        Assert.Contains(expected, contradiction, StringComparison.Ordinal);
        Assert.DoesNotContain("registeredTenant", contradiction, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_matching_verified_response_is_the_only_one_that_verifies_the_environment()
    {
        SourceEnvironmentProfile profile = Profile();
        StubProbeGateway gateway = new()
        {
            Rewrite = response => response with
            {
                Status = SourceGatewayProbeStatus.Verified,
                ObservedFormsRelease = profile.ExpectedFormsVersion,
                ObservedDatabaseRelease = profile.ExpectedDatabaseVersion,
                Capabilities =
                [
                    Capability("forms.installation", "Verified", "OracleFormsInstallation"),
                    Capability("forms.openapi.load", "Verified", "OracleFormsOpenApiLibraries"),
                    Capability("oracle.schema.extract", "Verified", "OracleClientConnectivity"),
                ],
            },
        };

        SourceEnvironmentProbeResult result = await Probe(gateway).ProbeAsync(profile, CancellationToken.None);

        Assert.Equal(SourceEnvironmentProbeStatus.Verified, result.Status);
        Assert.Equal(profile.ExpectedFormsVersion, result.Observed.Forms);
        Assert.Equal(profile.ExpectedDatabaseVersion, result.Observed.Database);
        Assert.Empty(result.BlockedPrerequisites);
        Assert.Empty(result.Contradictions);
        Assert.All(result.Capabilities, capability =>
            Assert.Equal(SourceEnvironmentProbeStatus.Verified, capability.State));

        SourceEnvironmentProfile stored = SourceEnvironmentProfiles.RecordProbe(profile, result, s_now);
        Assert.Equal(SourceEnvironmentReadiness.Verified, stored.Readiness);
    }

    private static GatewaySourceEnvironmentProbe Probe(StubProbeGateway gateway) => new(gateway, () => s_now);

    private static SourceEnvironmentProfile Profile() => SourceEnvironmentProfiles.Create(
        "tenant-a",
        "project-a",
        "legacy-order-entry",
        1,
        "Legacy order entry",
        SourceConnector.FormsBuilderWorker,
        "6i",
        "9i",
        "legacy-order-entry",
        ["HRMS"],
        [],
        s_created);

    private static SourceGatewayCapability Capability(string id, string state, string prerequisite) =>
        new(id, state, prerequisite, "6i", "x86", "WindowsWorker", "operator.install.forms.6i.worker", null);

    /// <summary>A gateway that answers exactly what a test tells it to, so admission is what is measured.</summary>
    private sealed class StubProbeGateway : ISourceGatewayClient
    {
        public List<SourceGatewayProbeRequest> Requests { get; } = [];

        public string? TransportError { get; init; }

        /// <summary>Applied to the default blocked answer, which is what a real unprovisioned host reports.</summary>
        public Func<SourceGatewayProbeResponse, SourceGatewayProbeResponse>? Rewrite { get; init; }

        public string Description => "Stub source gateway for offline tests.";

        public Task<SourceGatewayProbeCall> ProbeSourceEnvironmentAsync(
            SourceGatewayProbeRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);

            if (TransportError is { } error)
            {
                return Task.FromResult(new SourceGatewayProbeCall(null, error));
            }

            SourceGatewayProbeResponse response = new(
                SourceGatewayProtocol.SchemaVersion,
                SourceGatewayProbeStatus.BlockedPrerequisite,
                request.SourceEnvironmentId,
                request.ExpectedFormsRelease,
                request.ExpectedDatabaseRelease,
                request.ProfileVersion,
                request.ProfileHash,
                s_now,
                null,
                null,
                null,
                [
                    Capability("forms.installation", "Verified", "OracleFormsInstallation"),
                    Capability("oracle.schema.extract", "BlockedPrerequisite", "OracleClientConnectivity"),
                ],
                [],
                request.Scope);

            return Task.FromResult(new SourceGatewayProbeCall(Rewrite is null ? response : Rewrite(response), null));
        }

        public Task<SourceGatewayCall> ExtractFormsModuleAsync(
            SourceGatewayFormsModuleRequest request,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("A probe must never extract a module.");

        public Task<SourceGatewaySchemaCall> ExtractOracleSchemaAsync(
            SourceGatewayOracleSchemaRequest request,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("A probe must never read a schema.");
    }
}
