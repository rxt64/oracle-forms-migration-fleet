// Copyright (c) Microsoft. All rights reserved.

using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Azure.Core;
using OracleFormsMigrationFleet.Fleet;
using OracleFormsMigrationFleet.Fleet.Execution;
using OracleFormsMigrationFleet.Hosting;

namespace OracleFormsMigrationFleet.Tests;

/// <summary>
/// What the deployment gateway does against a real HTTP surface.
///
/// These are not policy tests. Every case here drives the gateway through an injected
/// <see cref="HttpMessageHandler"/> and an injected credential and then asserts on the requests that
/// actually left it, because the defects they pin are all defects of sequence and of wire format: an
/// authorization captured before an awaited upload, a claim advanced by a blind write, a run looked up by
/// the wrong JSON field, and a result document bound with a serializer contract that matches none of its
/// properties. None of those is visible in a pure function, and none of them fails a test that stubs the
/// gateway out.
/// </summary>
public sealed class GitHubActionsTargetDeploymentGatewayTests : IDisposable
{
    private const string Subscription = "d4394e57-c076-4c92-a870-5de6bf44f255";
    private const string ResourceGroup = "rg-oracle-forms-migration-fleet-dev-b9f0e875";
    private const string TargetName = "ca-ofmfleet-dotnet-dev-ykbpnrpd";
    private const string WorkbenchSha = "cd78d878e9d8b1eeabd3b50aa0d74cf83b8aefea";
    private const string ApplicationUrl = "https://ca-ofmfleet-dotnet-dev-ykbpnrpd.jollyground-7a57bcec.eastus2.azurecontainerapps.io";

    private const string ResourceId =
        $"/subscriptions/{Subscription}/resourceGroups/{ResourceGroup}/providers/Microsoft.App/containerApps/{TargetName}";

    private static readonly string s_imageDigest = "sha256:" + new string('c', 64);

    /// <summary>
    /// Fixed for the lifetime of one test, because the approval expiry is part of the destination digest
    /// and therefore of the operation identity. Re-reading the clock per call would make every call to
    /// the fixtures below name a different operation.
    /// </summary>
    private readonly DateTimeOffset _approvalExpires = DateTimeOffset.UtcNow.AddHours(4);

    private readonly string _workspace = Directory.CreateTempSubdirectory("ofm-gateway-").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_workspace, recursive: true);
        }
        catch (IOException)
        {
            // A locked temp file is not a test result.
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Finding 3 — the authorization that matters is the one that holds at the dispatch, not before it.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// The grant is revoked while the bundle is being uploaded.
    ///
    /// Everything the outer wrappers check happened before the upload started, so only a check inside the
    /// gateway can see this. The assertion is on the requests that left: the bundle may be stored, but no
    /// dispatch may be sent, because a build that starts mints an image nothing here can withdraw.
    /// </summary>
    [Fact]
    public async Task A_grant_revoked_while_the_bundle_uploads_dispatches_no_builder()
    {
        FakeEndpoints endpoints = new();
        ScriptedSentinel sentinel = ScriptedSentinel.WithholdingAt(
            TargetDeploymentCheckpoint.BeforeClaim,
            "The operator revoked this run's grant.");

        TargetDeploymentResult result = await PublishAsync(endpoints, sentinel);

        Assert.Equal(TargetDeploymentState.NotAuthorized, result.State);
        Assert.Contains("revoked", result.FailureReason!, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(endpoints.Dispatches);
        Assert.DoesNotContain(endpoints.Blobs.Keys, name => name.EndsWith("dispatch.json", StringComparison.Ordinal));
    }

    /// <summary>
    /// The claim is taken over by a newer worker while the installation token is being minted.
    ///
    /// Reading the signing key out of Key Vault and exchanging it for an installation token are awaited
    /// round trips, and this is the window the previous code spent holding an answer from before them. The
    /// token endpoint is expected to have been reached — that is the point — and the dispatch is not.
    /// </summary>
    [Fact]
    public async Task A_takeover_while_the_builder_token_is_acquired_dispatches_no_builder()
    {
        FakeEndpoints endpoints = new();
        ScriptedSentinel sentinel = ScriptedSentinel.WithholdingAt(
            TargetDeploymentCheckpoint.BeforeDispatch,
            "A newer claim owns this run.");

        TargetDeploymentResult result = await PublishAsync(endpoints, sentinel);

        Assert.Equal(TargetDeploymentState.NotAuthorized, result.State);
        Assert.Contains("newer claim", result.FailureReason!, StringComparison.OrdinalIgnoreCase);
        Assert.True(endpoints.InstallationTokenRequests > 0, "the token was expected to have been minted already");
        Assert.Empty(endpoints.Dispatches);
        Assert.Equal(
            "Claimed",
            endpoints.Claim(Operation())!.RootElement.GetProperty("phase").GetString());
    }

    /// <summary>Nothing is even uploaded when the run has already lost its authorization.</summary>
    [Fact]
    public async Task A_run_that_lost_its_authorization_before_the_upload_stores_nothing()
    {
        FakeEndpoints endpoints = new();
        ScriptedSentinel sentinel = ScriptedSentinel.WithholdingAt(
            TargetDeploymentCheckpoint.BeforeUpload,
            "This run's lease expired.");

        TargetDeploymentResult result = await PublishAsync(endpoints, sentinel);

        Assert.Equal(TargetDeploymentState.NotAuthorized, result.State);
        Assert.Empty(endpoints.Blobs);
        Assert.Empty(endpoints.Dispatches);
    }

    /// <summary>
    /// A builder that was never bound to a run has nothing to re-ask, and refuses rather than assuming.
    ///
    /// The host registers one builder for the whole process. A publish reaching it outside a durable run
    /// has no claim to fence and no lease to lose, so there would be nothing capable of stopping a stale
    /// dispatch at any later point.
    /// </summary>
    [Fact]
    public async Task An_unbound_builder_dispatches_nothing()
    {
        FakeEndpoints endpoints = new();
        GitHubActionsTargetDeploymentGateway gateway = new(Options(), new FakeCredential(), endpoints.Client());

        TargetDeploymentResult result = await gateway.PublishAsync(Request(), CancellationToken.None);

        Assert.Equal(TargetDeploymentState.NotAuthorized, result.State);
        Assert.Contains("never bound to a durable run", result.FailureReason!, StringComparison.Ordinal);
        Assert.Empty(endpoints.Dispatches);
    }

    /// <summary>The whole sequence, cleared at every checkpoint, does dispatch exactly once.</summary>
    [Fact]
    public async Task A_cleared_run_dispatches_once_and_records_the_deployment()
    {
        FakeEndpoints endpoints = new();
        endpoints.PutResultLater(Operation(), Report());

        TargetDeploymentResult result = await PublishAsync(endpoints, ScriptedSentinel.AlwaysClear());

        Assert.Equal(TargetDeploymentState.Deployed, result.State);
        Assert.Equal(s_imageDigest, result.ImageDigest);
        Assert.Equal(ResourceId, result.DeployedResourceId);
        Assert.Single(endpoints.Dispatches);
        Assert.Contains(result.Findings, finding => finding.Contains("answering at", StringComparison.Ordinal));
    }

    /// <summary>
    /// The deployment happened and this run may no longer claim it.
    ///
    /// Refusing silently would be its own defect: a revision really is serving, so the refusal names the
    /// image and the resource rather than reporting that nothing occurred.
    /// </summary>
    [Fact]
    public async Task A_run_that_lost_authorization_while_the_builder_worked_does_not_record_the_deployment()
    {
        FakeEndpoints endpoints = new();
        endpoints.PutResultLater(Operation(), Report());
        ScriptedSentinel sentinel = ScriptedSentinel.WithholdingAt(
            TargetDeploymentCheckpoint.BeforeResult,
            "This run was cancelled while the builder worked.");

        TargetDeploymentResult result = await PublishAsync(endpoints, sentinel);

        Assert.Equal(TargetDeploymentState.NotAuthorized, result.State);
        Assert.Contains(result.Findings, finding => finding.Contains(s_imageDigest, StringComparison.Ordinal));
        Assert.Contains(result.Findings, finding => finding.Contains("did not undo it", StringComparison.OrdinalIgnoreCase));
    }

    // ---------------------------------------------------------------------------------------------
    // Finding 4 — the result document is a wire contract, not whatever the default serializer binds.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// The defect itself: a camelCase document read with the host's default contract binds no property.
    ///
    /// This is the shape the trusted builder actually writes. Before the explicit contract it produced a
    /// record of nulls, which is why the case is driven through real bytes rather than through a record.
    /// </summary>
    [Fact]
    public async Task The_builders_camel_cased_document_binds_every_field_it_carries()
    {
        FakeEndpoints endpoints = new();
        endpoints.PutResult(Operation(), Report());

        TargetDeploymentResult result = await PublishAsync(endpoints, ScriptedSentinel.AlwaysClear());

        Assert.Equal(TargetDeploymentState.Deployed, result.State);
        Assert.Equal("4242", result.BuildRunId);
        Assert.Equal(ApplicationUrl, result.ApplicationUrl);
    }

    /// <summary>A document whose names are Pascal-cased is not this contract and is not a deployment.</summary>
    [Fact]
    public async Task A_pascal_cased_document_is_never_read_as_a_deployment()
    {
        Dictionary<string, object?> report = Report();
        Dictionary<string, object?> pascal = report.ToDictionary(
            pair => char.ToUpperInvariant(pair.Key[0]) + pair.Key[1..],
            pair => pair.Value,
            StringComparer.Ordinal);

        FakeEndpoints endpoints = new();
        endpoints.PutResult(Operation(), pascal);

        TargetDeploymentResult result = await PublishAsync(endpoints, ScriptedSentinel.AlwaysClear());

        Assert.Equal(TargetDeploymentState.VerificationFailed, result.State);
    }

    /// <summary>An empty document is a document with nothing in it, never a deployment with nothing in it.</summary>
    [Fact]
    public async Task An_empty_document_is_never_read_as_a_deployment()
    {
        FakeEndpoints endpoints = new();
        endpoints.PutResult(Operation(), []);

        TargetDeploymentResult result = await PublishAsync(endpoints, ScriptedSentinel.AlwaysClear());

        Assert.Equal(TargetDeploymentState.VerificationFailed, result.State);
        Assert.Contains(result.Findings, finding => finding.Contains("schema", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("schemaVersion", "fleet.target-deployment-result/2", "schema")]
    [InlineData("operationId", "0000000000000000000000000000000000000000000000000000000000000000", "operation identifier")]
    [InlineData("runId", "some-other-run", "run identifier")]
    [InlineData("artifactSha256", "1111111111111111111111111111111111111111111111111111111111111111", "artifact digest")]
    [InlineData("workbenchSha", "0000000000000000000000000000000000000000", "workbench commit")]
    [InlineData("sourceSnapshotSha256", "2222222222222222222222222222222222222222222222222222222222222222", "source snapshot digest")]
    [InlineData("planDigest", "3333333333333333333333333333333333333333333333333333333333333333", "plan digest")]
    [InlineData("targetDigest", "4444444444444444444444444444444444444444444444444444444444444444", "destination digest")]
    [InlineData("bindingDigest", "5555555555555555555555555555555555555555555555555555555555555555", "binding digest")]
    [InlineData("deployedResourceId", "/subscriptions/x/resourceGroups/y/providers/Microsoft.App/containerApps/z", "different Azure resource")]
    [InlineData("imageDigest", "latest", "immutable image digest")]
    [InlineData("revisionName", "", "naming the revision")]
    [InlineData("readinessOutcome", "Skipped", "readiness")]
    [InlineData("readinessProbeUrl", "https://attacker.example.com/healthz", "readiness probe")]
    [InlineData("readinessObservedUtc", "shortly after the build", "time at which it observed")]
    [InlineData("applicationUrl", "https://attacker.example.com", "host this product deploys to")]
    public async Task A_report_that_describes_anything_other_than_this_operation_is_not_a_deployment(
        string field,
        string substituted,
        string expected)
    {
        Dictionary<string, object?> report = Report();
        report[field] = substituted;

        FakeEndpoints endpoints = new();
        endpoints.PutResult(Operation(), report);

        TargetDeploymentResult result = await PublishAsync(endpoints, ScriptedSentinel.AlwaysClear());

        Assert.Equal(TargetDeploymentState.VerificationFailed, result.State);
        Assert.Contains(result.Findings, finding => finding.Contains(expected, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>A missing field is a missing field, not a field this product fills in from the record.</summary>
    [Theory]
    [InlineData("readinessOutcome")]
    [InlineData("readinessProbeUrl")]
    [InlineData("readinessObservedUtc")]
    [InlineData("bindingDigest")]
    [InlineData("deployedResourceId")]
    public async Task A_report_missing_a_required_fact_is_not_a_deployment(string omitted)
    {
        Dictionary<string, object?> report = Report();
        report.Remove(omitted);

        FakeEndpoints endpoints = new();
        endpoints.PutResult(Operation(), report);

        TargetDeploymentResult result = await PublishAsync(endpoints, ScriptedSentinel.AlwaysClear());

        Assert.Equal(TargetDeploymentState.VerificationFailed, result.State);
    }

    /// <summary>
    /// A builder that reports a failure is reporting a failure.
    ///
    /// The previous reading constructed a Deployed result before it had looked at the state at all, so a
    /// failed build was only ever refused as a side effect of missing a digest.
    /// </summary>
    [Fact]
    public async Task A_reported_build_failure_is_never_a_deployment()
    {
        Dictionary<string, object?> report = Report();
        report["state"] = "BuildFailed";

        FakeEndpoints endpoints = new();
        endpoints.PutResult(Operation(), report);

        TargetDeploymentResult result = await PublishAsync(endpoints, ScriptedSentinel.AlwaysClear());

        Assert.Equal(TargetDeploymentState.BuildFailed, result.State);
        Assert.Contains("Nothing of this run's is serving", result.FailureReason!, StringComparison.Ordinal);
    }

    /// <summary>A state the builder invented is not one of the three conclusions it may reach.</summary>
    [Fact]
    public async Task A_state_the_builder_invented_is_refused()
    {
        Dictionary<string, object?> report = Report();
        report["state"] = "Deployed ";

        FakeEndpoints endpoints = new();
        endpoints.PutResult(Operation(), report);

        TargetDeploymentResult result = await PublishAsync(endpoints, ScriptedSentinel.AlwaysClear());

        Assert.Equal(TargetDeploymentState.VerificationFailed, result.State);
        Assert.Contains(result.Findings, finding => finding.Contains("three conclusions", StringComparison.Ordinal));
    }

    // ---------------------------------------------------------------------------------------------
    // Finding 5 — recovery must recognise the run it already started, and claims move by compare-and-swap.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// The defect: GitHub's <c>name</c> is the workflow's name and <c>display_title</c> is the run's.
    ///
    /// Matching on <c>name</c> matched nothing, so every recovery concluded that no build existed and
    /// dispatched a second one. The listing here is shaped exactly as the API returns it.
    /// </summary>
    [Fact]
    public async Task A_run_already_started_for_this_operation_is_recognised_and_not_dispatched_again()
    {
        FakeEndpoints endpoints = new();
        endpoints.PutClaim(Claim("Dispatching", attempts: 1, ageMinutes: 30));
        endpoints.Runs = Listing(("generated-target", $"generated-target {Operation()}"));
        endpoints.PutResultLater(Operation(), Report());

        TargetDeploymentResult result = await PublishAsync(endpoints, ScriptedSentinel.AlwaysClear());

        Assert.Equal(TargetDeploymentState.Deployed, result.State);
        Assert.Empty(endpoints.Dispatches);
    }

    /// <summary>
    /// A title that merely mentions the operation is not this operation's run.
    ///
    /// The match is exact for the same reason the digests are compared rather than searched: a substring
    /// rule lets an unrelated run stop a legitimate recovery, and a rule that can be satisfied by prose is
    /// not a rule.
    /// </summary>
    [Fact]
    public async Task A_run_whose_title_merely_mentions_the_operation_is_not_mistaken_for_it()
    {
        FakeEndpoints endpoints = new();
        endpoints.PutClaim(Claim("Dispatching", attempts: 1, ageMinutes: 30));
        endpoints.Runs = Listing(("generated-target", $"retry of generated-target {Operation()} by hand"));

        TargetDeploymentResult result = await PublishAsync(endpoints, ScriptedSentinel.AlwaysClear());

        Assert.Equal(TargetDeploymentState.NotAttempted, result.State);
        Assert.Empty(endpoints.Dispatches);
    }

    /// <summary>
    /// A dispatch whose response was lost is never sent again.
    ///
    /// GitHub may have accepted it. Re-sending would build and deploy one operation twice under one run's
    /// provenance, so the operation stays ambiguous, keeps its evidence, and is resumed by re-entering the
    /// run — which re-enters this exact operation, because the identifier comes from the bytes.
    /// </summary>
    [Fact]
    public async Task A_dispatch_whose_outcome_is_unknown_is_never_dispatched_again()
    {
        FakeEndpoints endpoints = new();
        endpoints.PutClaim(Claim("Ambiguous", attempts: 1, ageMinutes: 30));
        endpoints.Runs = Listing();

        TargetDeploymentResult result = await PublishAsync(endpoints, ScriptedSentinel.AlwaysClear());

        Assert.Equal(TargetDeploymentState.NotAttempted, result.State);
        Assert.Contains("twice", result.FailureReason!, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(endpoints.Dispatches);
    }

    /// <summary>A lost response is recorded as ambiguous rather than retried inside the same publish.</summary>
    [Fact]
    public async Task A_dispatch_that_fails_without_a_response_leaves_an_ambiguous_claim()
    {
        FakeEndpoints endpoints = new() { FailDispatch = true };

        TargetDeploymentResult result = await PublishAsync(endpoints, ScriptedSentinel.AlwaysClear());

        Assert.Equal(TargetDeploymentState.NotAttempted, result.State);
        Assert.Equal(1, endpoints.DispatchAttempts);
        Assert.Equal("Ambiguous", endpoints.Claim(Operation())!.RootElement.GetProperty("phase").GetString());
    }

    /// <summary>
    /// A claim is advanced only by compare-and-swap, so a concurrent recovery cannot duplicate a dispatch.
    ///
    /// The store is told to refuse the conditional write, standing in for the replica that got there
    /// first. The correct answer is to wait for that replica's build, and specifically not to send a
    /// dispatch of its own.
    /// </summary>
    [Fact]
    public async Task A_claim_lost_to_a_concurrent_recovery_dispatches_nothing()
    {
        FakeEndpoints endpoints = new();
        endpoints.PutClaim(Claim("Claimed", attempts: 0, ageMinutes: 30));
        endpoints.Runs = Listing();
        endpoints.RefuseConditionalClaimWrites = true;
        endpoints.PutResultLater(Operation(), Report());

        TargetDeploymentResult result = await PublishAsync(endpoints, ScriptedSentinel.AlwaysClear());

        Assert.Equal(TargetDeploymentState.Deployed, result.State);
        Assert.Empty(endpoints.Dispatches);
        Assert.All(
            endpoints.ClaimWrites,
            write => Assert.True(
                write.CreateOnly || write.IfMatch is { Length: > 0 },
                "every claim write must be conditional"));
    }

    /// <summary>
    /// An unclaimed operation that was never attempted is taken over, under an entity-tag comparison.
    ///
    /// This is the one recovery that is safe, and the assertion is that it is still conditional: a blind
    /// upsert here would let two replicas that read the same claim both believe they had taken it over.
    /// </summary>
    [Fact]
    public async Task An_operation_claimed_but_never_attempted_is_taken_over_conditionally()
    {
        FakeEndpoints endpoints = new();
        endpoints.PutClaim(Claim("Claimed", attempts: 0, ageMinutes: 30));
        endpoints.Runs = Listing();
        endpoints.PutResultLater(Operation(), Report());

        TargetDeploymentResult result = await PublishAsync(endpoints, ScriptedSentinel.AlwaysClear());

        Assert.Equal(TargetDeploymentState.Deployed, result.State);
        Assert.Single(endpoints.Dispatches);
        Assert.Contains(endpoints.ClaimWrites, write => write.IfMatch is { Length: > 0 });
        Assert.DoesNotContain(endpoints.ClaimWrites, write => !write.CreateOnly && write.IfMatch is null);
    }

    /// <summary>An unreadable run listing is never evidence that no build is running.</summary>
    [Fact]
    public async Task An_unreadable_run_listing_stops_the_operation_rather_than_starting_a_second_build()
    {
        FakeEndpoints endpoints = new() { FailRunListing = true };
        endpoints.PutClaim(Claim("Claimed", attempts: 0, ageMinutes: 30));

        TargetDeploymentResult result = await PublishAsync(endpoints, ScriptedSentinel.AlwaysClear());

        Assert.Equal(TargetDeploymentState.NotAttempted, result.State);
        Assert.Contains("unreadable list", result.FailureReason!, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(endpoints.Dispatches);
    }

    /// <summary>
    /// A bundle already stored under this operation is never overwritten by different bytes.
    ///
    /// The identifier is derived from the bytes, so this cannot arise from a resumption — which is exactly
    /// why it is refused rather than reconciled. A build may already be fetching that artifact.
    /// </summary>
    [Fact]
    public async Task A_stored_bundle_with_a_different_digest_is_never_replaced()
    {
        FakeEndpoints endpoints = new();
        endpoints.PutBlob(
            TargetDeploymentPolicy.ArtifactBlobName(Operation()),
            [1, 2, 3],
            new string('9', 64));

        TargetDeploymentResult result = await PublishAsync(endpoints, ScriptedSentinel.AlwaysClear());

        Assert.Equal(TargetDeploymentState.RefusedByPolicy, result.State);
        Assert.Contains("Different bytes", result.FailureReason!, StringComparison.Ordinal);
        Assert.Empty(endpoints.Dispatches);
        Assert.Equal(
            new byte[] { 1, 2, 3 },
            endpoints.Blobs[TargetDeploymentPolicy.ArtifactBlobName(Operation())].Content);
    }

    /// <summary>
    /// A claim under this operation that is about different bytes stops the publish.
    ///
    /// It should be impossible, because the operation identifier is derived from the bundle and the
    /// binding. Being impossible is not a reason to write over it.
    /// </summary>
    [Fact]
    public async Task A_claim_about_a_different_binding_is_left_alone()
    {
        FakeEndpoints endpoints = new();
        Dictionary<string, object?> foreign = Claim("Claimed", attempts: 0, ageMinutes: 30);
        foreign["bindingDigest"] = new string('7', 64);
        endpoints.PutClaim(foreign);

        TargetDeploymentResult result = await PublishAsync(endpoints, ScriptedSentinel.AlwaysClear());

        Assert.Equal(TargetDeploymentState.RefusedByPolicy, result.State);
        Assert.Contains("different run, artifact, or binding", result.FailureReason!, StringComparison.Ordinal);
        Assert.Empty(endpoints.Dispatches);
    }

    /// <summary>The dispatch carries the binding digest the report is later held to.</summary>
    [Fact]
    public async Task The_dispatch_inputs_carry_the_binding_digest_the_report_is_compared_against()
    {
        FakeEndpoints endpoints = new();
        endpoints.PutResultLater(Operation(), Report());

        await PublishAsync(endpoints, ScriptedSentinel.AlwaysClear());

        using JsonDocument dispatched = JsonDocument.Parse(Assert.Single(endpoints.Dispatches));
        JsonElement inputs = dispatched.RootElement.GetProperty("inputs");

        Assert.Equal(BindingDigest(), inputs.GetProperty("binding_digest").GetString());
        Assert.Equal(Operation(), inputs.GetProperty("operation_id").GetString());
        Assert.Equal(ResourceId, inputs.GetProperty("target_resource_id").GetString());
    }

    // ---------------------------------------------------------------------------------------------
    // Fixtures.
    // ---------------------------------------------------------------------------------------------

    private async Task<TargetDeploymentResult> PublishAsync(FakeEndpoints endpoints, ScriptedSentinel sentinel)
    {
        GitHubActionsTargetDeploymentGateway gateway = new(Options(), new FakeCredential(), endpoints.Client());

        return await gateway.BoundTo(sentinel).PublishAsync(Request(), CancellationToken.None);
    }

    private static GitHubActionsDeploymentOptions Options() => new()
    {
        Owner = "rxt64",
        Repository = "oracle-forms-migration-fleet",
        AppId = "1234",
        InstallationId = "5678",
        PrivateKeySecretUri = new Uri("https://kv-ofmfleet.vault.azure.net/secrets/github-app-key"),
        StorageAccountUri = new Uri("https://stofmfleetdev.blob.core.windows.net"),
        WorkbenchCommitSha = WorkbenchSha,
        TargetName = TargetName,

        // The waiting loop is real, so it is kept short. Nothing here depends on the duration.
        Timeout = TimeSpan.FromSeconds(5),
        PollInterval = TimeSpan.FromMilliseconds(5),
    };

    private TargetDeploymentAuthority Authority() => new()
    {
        TargetProfileId = "profile-1",
        TargetProfileVersion = 3,
        TargetProfileHash = new string('b', 64),
        AzureTenantId = "72f988bf-86f1-41af-91ab-2d7cd011db47",
        SubscriptionId = Subscription,
        ResourceGroup = ResourceGroup,
        TargetResourceId = ResourceId,
        ExecutionIdentity = "id-ofmfleet-dotnet-dev-ykbpnrpd",
        EnvironmentName = "dev",
        ApprovalId = "approval-1",
        ApprovalExpiresUtc = _approvalExpires,
    };

    private TargetDeploymentBinding Binding() => new(
        "run-1",
        "tenant-1",
        "project-1",
        "ledger-1",
        "ENG-1",
        "Meridian Order Entry",
        new string('a', 64),
        new string('b', 64),
        new string('a', 64),
        new string('d', 64))
    {
        Authority = Authority(),
    };

    private TargetDeploymentBundle Bundle()
    {
        string root = Path.Combine(_workspace, "out", "pilot", "application");
        Directory.CreateDirectory(root);
        string file = Path.Combine(root, "Program.cs");

        if (!File.Exists(file))
        {
            File.WriteAllText(file, "// generated\n");
        }

        byte[] content = File.ReadAllBytes(file);
        TargetDeploymentFile[] files =
        [
            new("application/Program.cs", Convert.ToHexStringLower(SHA256.HashData(content)), content.Length),
        ];

        return new TargetDeploymentBundle(
            _workspace,
            "out/pilot",
            files,
            GenerationCoverage.OutputSetDigest(files.Select(entry => (entry.Path, entry.ContentSha256))));
    }

    private TargetDeploymentRequest Request() => new(
        Binding(),
        Bundle(),
        BackEndStack.AspNetCore,
        DatabaseTarget.PostgreSql,
        Operation());

    private string Operation() => TargetDeploymentPolicy.OperationId(Binding(), Bundle().OutputSetSha256);

    private string ArchiveSha() =>
        Convert.ToHexStringLower(SHA256.HashData(GitHubActionsTargetDeploymentGateway.Package(Bundle())));

    private string ArtifactUri() =>
        $"https://stofmfleetdev.blob.core.windows.net/generated-target/{TargetDeploymentPolicy.ArtifactBlobName(Operation())}";

    private string BindingDigest() =>
        TargetDeploymentPolicy.BindingDigest(Binding(), Operation(), ArtifactUri(), ArchiveSha(), WorkbenchSha);

    /// <summary>The document the trusted builder is expected to write, exactly as it goes on the wire.</summary>
    private Dictionary<string, object?> Report() => new(StringComparer.Ordinal)
    {
        ["schemaVersion"] = TargetDeploymentPolicy.ResultSchemaVersion,
        ["state"] = "Deployed",
        ["operationId"] = Operation(),
        ["runId"] = "run-1",
        ["buildRunId"] = "4242",
        ["artifactSha256"] = ArchiveSha(),
        ["workbenchSha"] = WorkbenchSha,
        ["sourceSnapshotSha256"] = new string('a', 64),
        ["planDigest"] = TargetDeploymentPolicy.PlanDigest(Binding()),
        ["targetDigest"] = TargetDeploymentPolicy.TargetDigest(Authority()),
        ["bindingDigest"] = BindingDigest(),
        ["imageDigest"] = s_imageDigest,
        ["revisionName"] = $"{TargetName}--0000001",
        ["applicationUrl"] = ApplicationUrl,
        ["deployedResourceId"] = ResourceId,
        ["readinessProbeUrl"] = $"{ApplicationUrl}/healthz",
        ["readinessOutcome"] = "Passed",
        ["readinessObservedUtc"] = DateTimeOffset.UtcNow.AddMinutes(-1).ToString("O"),
    };

    private Dictionary<string, object?> Claim(string phase, int attempts, int ageMinutes)
    {
        DateTimeOffset claimed = DateTimeOffset.UtcNow.AddMinutes(-ageMinutes);

        return new(StringComparer.Ordinal)
        {
            ["schemaVersion"] = "fleet.target-deployment-claim/2",
            ["phase"] = phase,
            ["operationId"] = Operation(),
            ["runId"] = "run-1",
            ["artifactSha256"] = ArchiveSha(),
            ["bindingDigest"] = BindingDigest(),
            ["claimedUtc"] = claimed.ToString("O"),
            ["dispatchAttempts"] = attempts,
            ["lastAttemptUtc"] = attempts > 0 ? claimed.ToString("O") : null,
            ["dispatchedUtc"] = null,
            ["note"] = null,
        };
    }

    /// <summary>A workflow-run listing shaped as the API returns it: workflow name and per-run title.</summary>
    private static string Listing(params (string Name, string DisplayTitle)[] runs) =>
        JsonSerializer.Serialize(new
        {
            total_count = runs.Length,
            workflow_runs = runs.Select(run => new { name = run.Name, display_title = run.DisplayTitle }).ToArray(),
        });

    private sealed class FakeCredential : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            new("fake-token", DateTimeOffset.UtcNow.AddHours(1));

        public override ValueTask<AccessToken> GetTokenAsync(
            TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            new(GetToken(requestContext, cancellationToken));
    }

    /// <summary>The server's answer, scripted per checkpoint so one window at a time can be closed.</summary>
    private sealed class ScriptedSentinel(TargetDeploymentCheckpoint? withholdAt, string reason)
        : ITargetDeploymentDispatchSentinel
    {
        public List<TargetDeploymentCheckpoint> Asked { get; } = [];

        public static ScriptedSentinel AlwaysClear() => new(null, string.Empty);

        public static ScriptedSentinel WithholdingAt(TargetDeploymentCheckpoint checkpoint, string reason) =>
            new(checkpoint, reason);

        public Task<TargetDeploymentClearance> RevalidateAsync(
            TargetDeploymentCheckpoint checkpoint,
            string operationId,
            CancellationToken cancellationToken)
        {
            Asked.Add(checkpoint);

            return Task.FromResult(checkpoint == withholdAt
                ? TargetDeploymentClearance.Withheld(reason)
                : TargetDeploymentClearance.Cleared("The run is still authorized."));
        }
    }

    private sealed record StoredBlob(byte[] Content, string ETag, string? ArchiveSha256);

    private sealed record ClaimWrite(bool CreateOnly, string? IfMatch);

    /// <summary>
    /// A blob container, a Key Vault secret, and the three GitHub endpoints, over real HTTP messages.
    ///
    /// The conditional-write semantics are the ones being relied on, so they are implemented rather than
    /// assumed: create-only conflicts, entity-tag comparison, and a switch to refuse a comparison so a
    /// concurrent recovery can be reproduced deterministically.
    /// </summary>
    private sealed class FakeEndpoints : HttpMessageHandler
    {
        private static readonly string s_pem = RSA.Create(2048).ExportPkcs8PrivateKeyPem();

        private int _etag;

        private (string Name, byte[] Content)? _pending;

        public Dictionary<string, StoredBlob> Blobs { get; } = new(StringComparer.Ordinal);

        public List<string> Dispatches { get; } = [];

        public List<ClaimWrite> ClaimWrites { get; } = [];

        public int DispatchAttempts { get; private set; }

        public int InstallationTokenRequests { get; private set; }

        public string Runs { get; set; } = """{"total_count":0,"workflow_runs":[]}""";

        public bool FailDispatch { get; init; }

        public bool FailRunListing { get; init; }

        public bool RefuseConditionalClaimWrites { get; set; }

        public HttpClient Client() => new(this) { Timeout = TimeSpan.FromSeconds(30) };

        public void PutBlob(string name, byte[] content, string? archiveSha = null) =>
            Blobs[name] = new StoredBlob(content, $"\"{++_etag}\"", archiveSha);

        /// <summary>A report already present before the publish begins.</summary>
        public void PutResult(string operationId, Dictionary<string, object?> report) =>
            PutBlob(TargetDeploymentPolicy.ResultBlobName(operationId), JsonSerializer.SerializeToUtf8Bytes(report));

        /// <summary>
        /// A report the builder writes while this publish is running.
        ///
        /// Held back until the gateway has either dispatched or looked up the run, because a report that
        /// is already there short-circuits the publish at its first read — which is correct behaviour, and
        /// would make every case about dispatching untestable.
        /// </summary>
        public void PutResultLater(string operationId, Dictionary<string, object?> report) =>
            _pending = (TargetDeploymentPolicy.ResultBlobName(operationId), JsonSerializer.SerializeToUtf8Bytes(report));

        private void Reveal()
        {
            if (_pending is { } pending)
            {
                PutBlob(pending.Name, pending.Content);
                _pending = null;
            }
        }

        public void PutClaim(Dictionary<string, object?> claim) =>
            PutBlob(
                TargetDeploymentPolicy.ClaimBlobName((string)claim["operationId"]!),
                JsonSerializer.SerializeToUtf8Bytes(claim));

        public JsonDocument? Claim(string operationId) =>
            Blobs.TryGetValue(TargetDeploymentPolicy.ClaimBlobName(operationId), out StoredBlob? blob)
                ? JsonDocument.Parse(blob.Content)
                : null;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Uri uri = request.RequestUri!;

            if (uri.IdnHost.EndsWith(".blob.core.windows.net", StringComparison.Ordinal))
            {
                return Blob(request, uri);
            }

            if (uri.IdnHost.EndsWith(".vault.azure.net", StringComparison.Ordinal))
            {
                return Json($$"""{"value": {{JsonSerializer.Serialize(s_pem)}}}""");
            }

            if (uri.AbsolutePath.EndsWith("/access_tokens", StringComparison.Ordinal))
            {
                InstallationTokenRequests++;
                return Json("""{"token":"ghs_installation"}""");
            }

            if (uri.AbsolutePath.EndsWith("/dispatches", StringComparison.Ordinal))
            {
                DispatchAttempts++;

                if (FailDispatch)
                {
                    throw new HttpRequestException("the connection was reset after the request was sent");
                }

                Dispatches.Add(await request.Content!.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
                Reveal();
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }

            if (uri.AbsolutePath.EndsWith("/runs", StringComparison.Ordinal))
            {
                if (FailRunListing)
                {
                    return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
                }

                Reveal();
                return Json(Runs);
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private HttpResponseMessage Blob(HttpRequestMessage request, Uri uri)
        {
            string name = uri.AbsolutePath["/generated-target/".Length..];
            bool isClaim = name.EndsWith("dispatch.json", StringComparison.Ordinal);

            if (request.Method == HttpMethod.Get || request.Method == HttpMethod.Head)
            {
                if (!Blobs.TryGetValue(name, out StoredBlob? found))
                {
                    return new HttpResponseMessage(HttpStatusCode.NotFound);
                }

                HttpResponseMessage read = new(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(request.Method == HttpMethod.Head ? [] : found.Content),
                };
                read.Headers.TryAddWithoutValidation("ETag", found.ETag);

                if (found.ArchiveSha256 is { Length: > 0 })
                {
                    read.Headers.TryAddWithoutValidation("x-ms-meta-archivesha256", found.ArchiveSha256);
                }

                return read;
            }

            bool createOnly = request.Headers.IfNoneMatch.Count > 0;
            string? ifMatch = request.Headers.IfMatch.FirstOrDefault()?.ToString();

            if (isClaim)
            {
                ClaimWrites.Add(new ClaimWrite(createOnly, ifMatch));
            }

            if (createOnly && Blobs.ContainsKey(name))
            {
                return new HttpResponseMessage(HttpStatusCode.Conflict);
            }

            if (!createOnly)
            {
                if (ifMatch is null)
                {
                    return new HttpResponseMessage(HttpStatusCode.PreconditionFailed);
                }

                if (isClaim && RefuseConditionalClaimWrites)
                {
                    return new HttpResponseMessage(HttpStatusCode.PreconditionFailed);
                }

                if (!Blobs.TryGetValue(name, out StoredBlob? current) ||
                    !string.Equals(current.ETag, ifMatch, StringComparison.Ordinal))
                {
                    return new HttpResponseMessage(HttpStatusCode.PreconditionFailed);
                }
            }

            byte[] body = request.Content!.ReadAsByteArrayAsync(CancellationToken.None).GetAwaiter().GetResult();
            string? archiveSha = request.Headers.TryGetValues("x-ms-meta-archivesha256", out IEnumerable<string>? values)
                ? values.FirstOrDefault()
                : null;

            PutBlob(name, body, archiveSha);

            HttpResponseMessage written = new(HttpStatusCode.Created);
            written.Headers.TryAddWithoutValidation("ETag", Blobs[name].ETag);
            return written;
        }

        private static HttpResponseMessage Json(string body) =>
            new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }
}
