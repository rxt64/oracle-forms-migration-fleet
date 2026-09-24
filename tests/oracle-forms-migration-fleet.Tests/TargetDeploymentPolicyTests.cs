// Copyright (c) Microsoft. All rights reserved.

using OracleFormsMigrationFleet.Fleet;
using OracleFormsMigrationFleet.Fleet.Execution;

namespace OracleFormsMigrationFleet.Tests;

/// <summary>
/// The deterministic rules a deployment is held to.
///
/// These are the checks that decide what a build agent is told to fetch and what counts as a deployment
/// afterwards, so each one is pinned here rather than left to the shell inside a workflow. A rule that
/// exists only in YAML is a rule nobody can run.
/// </summary>
public sealed class TargetDeploymentPolicyTests
{
    private const string DigestA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string DigestB = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string Subscription = "d4394e57-c076-4c92-a870-5de6bf44f255";
    private const string ResourceGroup = "rg-oracle-forms-migration-fleet-dev-b9f0e875";
    private const string ApplicationName = "ca-ofmfleet-dotnet-dev-ykbpnrpd";

    private static readonly DateTimeOffset s_now = new(2026, 9, 22, 12, 0, 0, TimeSpan.Zero);

    private static TargetDeploymentAuthority Authority(
        string? resourceId = null,
        DateTimeOffset? expires = null) => new()
        {
            TargetProfileId = "profile-1",
            TargetProfileVersion = 3,
            TargetProfileHash = DigestB,
            AzureTenantId = "72f988bf-86f1-41af-91ab-2d7cd011db47",
            SubscriptionId = Subscription,
            ResourceGroup = ResourceGroup,
            TargetResourceId = resourceId ??
                $"/subscriptions/{Subscription}/resourceGroups/{ResourceGroup}/providers/Microsoft.App/containerApps/{ApplicationName}",
            ExecutionIdentity = "id-ofmfleet-dotnet-dev-ykbpnrpd",
            EnvironmentName = "dev",
            ApprovalId = "approval-1",
            ApprovalExpiresUtc = expires ?? s_now.AddHours(2),
        };

    private static TargetDeploymentBinding Binding(
        string scopeDigest = DigestA,
        TargetDeploymentAuthority? authority = null) => new(
        "run-1",
        "tenant-1",
        "project-1",
        "ledger-1",
        "ENG-1",
        "Meridian Order Entry",
        DigestA,
        DigestB,
        DigestA,
        scopeDigest)
    {
        Authority = authority ?? Authority(),
    };

    private static TargetDeploymentBundle Bundle(params (string Path, string Sha)[] files)
    {
        TargetDeploymentFile[] entries = [.. files.Select(file => new TargetDeploymentFile(file.Path, file.Sha, 10))];

        return new TargetDeploymentBundle(
            OperatingSystem.IsWindows() ? @"C:\workspace" : "/workspace",
            "out/pilot",
            entries,
            GenerationCoverage.OutputSetDigest(entries.Select(file => (file.Path, file.ContentSha256))));
    }

    [Theory]
    [InlineData("http://store.blob.core.windows.net/c/b.zip", "https")]
    [InlineData("https://store.blob.core.windows.net/c/b.zip?sv=2024&sig=secret", "query string")]
    [InlineData("https://store.blob.core.windows.net/c/b.zip#frag", "fragment")]
    [InlineData("https://user:pass@store.blob.core.windows.net/c/b.zip", "user information")]
    [InlineData("https://attacker.example.com/c/b.zip", "not one this product is allowed")]
    [InlineData("https://store.blob.core.windows.net:8443/c/b.zip", "default https port")]
    [InlineData("https://store.blob.core.windows.net/c/../../secret", "well-formed blob path")]
    [InlineData("https://store.blob.core.windows.net/c/%2e%2e/secret", "well-formed blob path")]
    [InlineData("https://store.blob.core.windows.net/onlycontainer", "well-formed blob path")]
    [InlineData("https://store.blob.core.windows.net/c/b/", "well-formed blob path")]
    [InlineData("not-a-uri", "absolute URI")]
    [InlineData("", "No artifact location")]
    public void An_artifact_location_that_could_become_a_forged_request_is_refused(string candidate, string expected)
    {
        string? rejection = TargetDeploymentPolicy.RejectArtifactUri(candidate);

        Assert.NotNull(rejection);
        Assert.Contains(expected, rejection, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void An_https_blob_on_an_allowlisted_host_with_no_query_is_accepted() =>
        Assert.Null(TargetDeploymentPolicy.RejectArtifactUri(
            "https://stofmfleetdev.blob.core.windows.net/generated-target/operations/abc/generated-application.zip"));

    /// <summary>
    /// A shared-access signature in a dispatch input would put a credential into the workflow inputs, the
    /// run's own audit record, and every log line that echoes one. The rule is the absence of a query
    /// string, not the absence of the literal 'sig', so a future SAS parameter name cannot slip past it.
    /// </summary>
    [Fact]
    public void A_shared_access_signature_can_never_travel_as_a_dispatch_input()
    {
        string? rejection = TargetDeploymentPolicy.RejectArtifactUri(
            "https://stofmfleetdev.blob.core.windows.net/generated-target/b.zip?se=2027-01-01&sp=r&sig=abc");

        Assert.Contains("shared-access signature", rejection!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_same_bytes_under_the_same_decisions_always_carry_the_same_operation_identity()
    {
        string first = TargetDeploymentPolicy.OperationId(Binding(), DigestA);
        string second = TargetDeploymentPolicy.OperationId(Binding(), DigestA);

        Assert.Equal(first, second);
        Assert.True(TargetDeploymentPolicy.IsContentDigest(first));
    }

    [Fact]
    public void A_changed_decision_or_a_changed_output_produces_a_different_operation()
    {
        string baseline = TargetDeploymentPolicy.OperationId(Binding(), DigestA);

        Assert.NotEqual(baseline, TargetDeploymentPolicy.OperationId(Binding(scopeDigest: DigestB), DigestA));
        Assert.NotEqual(baseline, TargetDeploymentPolicy.OperationId(Binding(), DigestB));
    }

    [Fact]
    public void A_bundle_whose_digest_does_not_describe_its_files_is_refused()
    {
        TargetDeploymentBundle honest = Bundle(("application/a.cs", DigestA), ("application/b.cs", DigestB));
        Assert.Empty(TargetDeploymentPolicy.RejectBundle(honest));

        // The digest is recomputed rather than trusted, so an asserted one that does not describe the
        // listed files is refused instead of becoming the provenance of whatever was actually shipped.
        TargetDeploymentBundle forged = honest with { OutputSetSha256 = DigestB };

        Assert.Contains(
            TargetDeploymentPolicy.RejectBundle(forged),
            rejection => rejection.Contains("does not match the files it lists", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("/etc/passwd")]
    [InlineData("../escape.cs")]
    [InlineData(@"C:\windows\system32\x.cs")]
    public void A_bundle_path_that_leaves_the_workspace_is_refused(string path) =>
        Assert.Contains(
            TargetDeploymentPolicy.RejectBundle(Bundle((path, DigestA))),
            rejection => rejection.Contains("workspace-relative", StringComparison.OrdinalIgnoreCase));

    [Fact]
    public void An_empty_bundle_is_refused_rather_than_published_as_an_empty_application() =>
        Assert.Contains(
            TargetDeploymentPolicy.RejectBundle(Bundle()),
            rejection => rejection.Contains("holds no files", StringComparison.Ordinal));

    [Fact]
    public void A_binding_missing_any_identity_or_digest_is_refused()
    {
        Assert.Empty(TargetDeploymentPolicy.RejectBinding(Binding(), s_now));

        Assert.Contains(
            TargetDeploymentPolicy.RejectBinding(Binding() with { RunId = "  " }, s_now),
            rejection => rejection.Contains("run identifier", StringComparison.OrdinalIgnoreCase));

        Assert.Contains(
            TargetDeploymentPolicy.RejectBinding(Binding() with { SourceSnapshotHash = "not-a-digest" }, s_now),
            rejection => rejection.Contains("lowercase hex digest", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void A_binding_without_a_ledger_identity_is_refused() =>
        Assert.Contains(
            TargetDeploymentPolicy.RejectBinding(Binding() with { LedgerId = "  " }, s_now),
            rejection => rejection.Contains("ledger identifier", StringComparison.OrdinalIgnoreCase));

    [Theory]
    [InlineData("profile")]
    [InlineData("tenant")]
    [InlineData("environment")]
    public void An_approved_target_missing_any_identity_is_refused(string missing)
    {
        TargetDeploymentAuthority authority = missing switch
        {
            "profile" => Authority() with { TargetProfileId = "  " },
            "tenant" => Authority() with { AzureTenantId = "  " },
            "environment" => Authority() with { EnvironmentName = "  " },
            _ => throw new ArgumentOutOfRangeException(nameof(missing)),
        };

        Assert.Contains(
            TargetDeploymentPolicy.RejectAuthority(authority, s_now),
            rejection => rejection.Contains(missing, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void An_approved_target_with_a_non_guid_tenant_is_refused() =>
        Assert.Contains(
            TargetDeploymentPolicy.RejectAuthority(
                Authority() with { AzureTenantId = "tenant-not-a-guid" }, s_now),
            rejection => rejection.Contains("tenant identifier is not a GUID", StringComparison.Ordinal));

    [Fact]
    public void A_binding_carrying_credential_material_is_refused() =>
        Assert.Contains(
            TargetDeploymentPolicy.RejectBinding(
                Binding() with { EngagementId = "password=hunter2-not-a-real-secret" }, s_now),
            rejection => rejection.Contains("credential material", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Host configuration naming a container app says where a deployment could go. It does not say that
    /// this run's project was approved to send one there, so a binding that resolved no destination is a
    /// refusal rather than a deployment performed under whatever the host happened to be pointed at.
    /// </summary>
    [Fact]
    public void A_binding_with_no_resolved_destination_is_refused() =>
        Assert.Contains(
            TargetDeploymentPolicy.RejectBinding(Binding() with { Authority = null }, s_now),
            rejection => rejection.Contains("Host configuration naming a container app is not an approval", StringComparison.Ordinal));

    /// <summary>
    /// A subscription, resource group, or managed-environment identifier all look like legitimate ARM
    /// identifiers and would all authorize far more than one application, so the shape is asserted exactly.
    /// </summary>
    [Theory]
    [InlineData("/subscriptions/" + Subscription, "single Azure Container App")]
    [InlineData("/subscriptions/" + Subscription + "/resourceGroups/" + ResourceGroup, "single Azure Container App")]
    [InlineData(
        "/subscriptions/" + Subscription + "/resourceGroups/" + ResourceGroup + "/providers/Microsoft.App/managedEnvironments/cae-ofmfleet-dev",
        "single Azure Container App")]
    [InlineData(
        "/subscriptions/" + Subscription + "/resourceGroups/" + ResourceGroup + "/providers/Microsoft.App/containerApps/",
        "single Azure Container App")]
    [InlineData(
        "subscriptions/" + Subscription + "/resourceGroups/" + ResourceGroup + "/providers/Microsoft.App/containerApps/app",
        "absolute ARM resource identifier")]
    public void A_destination_wider_than_one_application_is_refused(string resourceId, string expected) =>
        Assert.Contains(
            TargetDeploymentPolicy.RejectAuthority(Authority(resourceId), s_now),
            rejection => rejection.Contains(expected, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The identifier and the profile's own coordinates have to agree. A resource in another subscription
    /// or another resource group is a destination the approved profile does not describe, however
    /// well-formed the identifier is.
    /// </summary>
    [Fact]
    public void A_destination_outside_the_profile_subscription_or_resource_group_is_refused()
    {
        Assert.Contains(
            TargetDeploymentPolicy.RejectAuthority(
                Authority($"/subscriptions/2f9e1b4c-0000-4000-8000-000000000000/resourceGroups/{ResourceGroup}/providers/Microsoft.App/containerApps/{ApplicationName}"),
                s_now),
            rejection => rejection.Contains("different subscription", StringComparison.OrdinalIgnoreCase));

        Assert.Contains(
            TargetDeploymentPolicy.RejectAuthority(
                Authority($"/subscriptions/{Subscription}/resourceGroups/rg-somewhere-else/providers/Microsoft.App/containerApps/{ApplicationName}"),
                s_now),
            rejection => rejection.Contains("different resource group", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// A build can easily outlive the approval that started it, so the expiry is compared at the moment it
    /// is asked about. It is never extended: a fresh approval is a different destination digest and
    /// therefore a different operation.
    /// </summary>
    [Fact]
    public void An_expired_approval_never_publishes_and_is_never_extended()
    {
        Assert.Empty(TargetDeploymentPolicy.RejectAuthority(Authority(expires: s_now.AddMinutes(1)), s_now));

        Assert.Contains(
            TargetDeploymentPolicy.RejectAuthority(Authority(expires: s_now), s_now),
            rejection => rejection.Contains("expired", StringComparison.OrdinalIgnoreCase));

        Assert.Contains(
            TargetDeploymentPolicy.RejectAuthority(Authority(expires: s_now.AddMinutes(-1)), s_now),
            rejection => rejection.Contains("fresh approval produces a new operation", StringComparison.Ordinal));
    }

    [Fact]
    public void The_operation_identity_moves_with_the_destination_and_the_approval()
    {
        string baseline = TargetDeploymentPolicy.OperationId(Binding(), DigestA);

        Assert.NotEqual(
            baseline,
            TargetDeploymentPolicy.OperationId(
                Binding(authority: Authority($"/subscriptions/{Subscription}/resourceGroups/{ResourceGroup}/providers/Microsoft.App/containerApps/ca-somewhere-else")),
                DigestA));

        Assert.NotEqual(
            baseline,
            TargetDeploymentPolicy.OperationId(Binding(authority: Authority(expires: s_now.AddDays(1))), DigestA));
    }

    /// <summary>
    /// The builder recomputes this from the inputs it received. A dispatch with any field rewritten in
    /// transit no longer matches what the product derived from what it bound.
    /// </summary>
    [Fact]
    public void The_binding_digest_covers_every_dispatch_input()
    {
        const string Uri = "https://stofmfleetdev.blob.core.windows.net/generated-target/operations/x/a.zip";
        string workbench = new('c', 40);

        string baseline = TargetDeploymentPolicy.BindingDigest(Binding(), DigestA, Uri, DigestB, workbench);

        Assert.True(TargetDeploymentPolicy.IsContentDigest(baseline));
        Assert.Equal(baseline, TargetDeploymentPolicy.BindingDigest(Binding(), DigestA, Uri, DigestB, workbench));

        Assert.NotEqual(baseline, TargetDeploymentPolicy.BindingDigest(Binding(), DigestB, Uri, DigestB, workbench));
        Assert.NotEqual(baseline, TargetDeploymentPolicy.BindingDigest(Binding(), DigestA, Uri + "x", DigestB, workbench));
        Assert.NotEqual(baseline, TargetDeploymentPolicy.BindingDigest(Binding(), DigestA, Uri, DigestA, workbench));
        Assert.NotEqual(baseline, TargetDeploymentPolicy.BindingDigest(Binding(), DigestA, Uri, DigestB, new string('d', 40)));
        Assert.NotEqual(
            baseline,
            TargetDeploymentPolicy.BindingDigest(
                Binding(authority: Authority(expires: s_now.AddDays(1))), DigestA, Uri, DigestB, workbench));
    }

    /// <summary>
    /// A tag can be moved after it was verified. Only a digest names the bytes a revision is running, so a
    /// builder that reports success without one has not shown that anything of this run's is serving.
    /// </summary>
    [Theory]
    [InlineData(null, "https://app.azurecontainerapps.io", "immutable image digest")]
    [InlineData("latest", "https://app.azurecontainerapps.io", "immutable image digest")]
    [InlineData("sha256:not-hex", "https://app.azurecontainerapps.io", "immutable image digest")]
    [InlineData("sha256:" + DigestA, "http://app.azurecontainerapps.io", "https URL")]
    [InlineData("sha256:" + DigestA, "https://attacker.example.com", "https URL")]
    public void A_deployment_claim_without_a_digest_or_an_expected_host_is_refused(
        string? digest,
        string url,
        string expected)
    {
        TargetDeploymentResult reported = new(
            TargetDeploymentState.Deployed,
            "12345",
            digest,
            "rev-0000001",
            url,
            [],
            null);

        Assert.Contains(
            TargetDeploymentPolicy.RejectResult(reported),
            rejection => rejection.Contains(expected, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void A_complete_deployment_claim_is_accepted()
    {
        TargetDeploymentResult reported = new(
            TargetDeploymentState.Deployed,
            "12345",
            "sha256:" + DigestA,
            ApplicationName + "--0000001",
            $"https://{ApplicationName}.jollyground.eastus2.azurecontainerapps.io",
            [],
            null)
        {
            DeployedResourceId = Authority().TargetResourceId,
        };

        Assert.Empty(TargetDeploymentPolicy.RejectResult(reported, Authority()));
    }

    /// <summary>
    /// The builder answering successfully is not the same fact as this run's application being the one it
    /// updated. A report that names no resource, or a different one, is recorded as unverified rather than
    /// as a deployment carrying this run's provenance.
    /// </summary>
    [Fact]
    public void A_deployment_that_landed_on_another_resource_is_not_this_runs_deployment()
    {
        TargetDeploymentResult reported = new(
            TargetDeploymentState.Deployed,
            "12345",
            "sha256:" + DigestA,
            "rev-0000001",
            $"https://{ApplicationName}.jollyground.eastus2.azurecontainerapps.io",
            [],
            null);

        Assert.Contains(
            TargetDeploymentPolicy.RejectResult(reported, Authority()),
            rejection => rejection.Contains("without naming the Azure resource it updated", StringComparison.Ordinal));

        TargetDeploymentResult elsewhere = reported with
        {
            DeployedResourceId =
                $"/subscriptions/{Subscription}/resourceGroups/{ResourceGroup}/providers/Microsoft.App/containerApps/ca-somewhere-else",
        };

        Assert.Contains(
            TargetDeploymentPolicy.RejectResult(elsewhere, Authority()),
            rejection => rejection.Contains("updated a different Azure resource", StringComparison.Ordinal));
    }

    /// <summary>A failure carries no digest by definition, so it is not held to the deployed-state rules.</summary>
    [Fact]
    public void A_reported_failure_is_not_re_judged_as_an_incomplete_deployment() =>
        Assert.Empty(TargetDeploymentPolicy.RejectResult(
            TargetDeploymentResult.Refused(TargetDeploymentState.BuildFailed, "The generated tier did not build.")));

    [Fact]
    public void The_plan_digest_moves_with_the_decisions_and_the_mapping()
    {
        string baseline = TargetDeploymentPolicy.PlanDigest(Binding());

        Assert.True(TargetDeploymentPolicy.IsContentDigest(baseline));
        Assert.Equal(baseline, TargetDeploymentPolicy.PlanDigest(Binding()));
        Assert.NotEqual(baseline, TargetDeploymentPolicy.PlanDigest(Binding(scopeDigest: DigestB)));
    }

    [Fact]
    public void The_deployment_phase_is_planned_under_the_sandbox_gate_and_never_under_plan_approval()
    {
        PhaseOwnership owned = MigrationRunPlanner.Lifecycle
            .Single(phase => phase.Phase == MigrationPhase.TargetApplicationDeployment);

        Assert.Equal(MutationClass.SandboxDatabaseWrite, owned.Mutation);
        Assert.Equal(ExecutionMode.SandboxMigration, owned.RequiredMode);
        Assert.True(owned.RequiresApproval);
    }
}
