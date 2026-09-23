// Copyright (c) Microsoft. All rights reserved.

using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Azure.Core;
using Microsoft.Extensions.Configuration;
using OracleFormsMigrationFleet.Fleet.Execution;

namespace OracleFormsMigrationFleet.Hosting;

/// <summary>
/// The name of the Container App this deployment builds into, as the host was configured with it.
///
/// It is registered separately from the gateway options because the run's destination has to be
/// resolvable even when no builder is configured: the deployment phase then reports the unmet host
/// prerequisite rather than a destination problem, and an operator sees one gap instead of two.
///
/// A name is not authority. The subscription, resource group, identity, and profile hash that make it a
/// destination come from the run's own target profile, and the trusted builder re-derives the same
/// identifier from its own configuration and refuses when the two disagree.
/// </summary>
public sealed record GeneratedApplicationTargetName(string Name);

/// <summary>
/// Everything the host must be told before the product can deploy a generated application tier.
///
/// None of it is a credential. The GitHub App's private key is named by a Key Vault secret identifier and
/// read at the moment it is needed with the runtime's own managed identity; nothing here, in configuration,
/// or in a log holds the key itself. There is deliberately no path that accepts an operator's token: a
/// deployment the product performs must be attributable to the product, not to whoever was signed in.
/// </summary>
public sealed record GitHubActionsDeploymentOptions
{
    public required string Owner { get; init; }

    public required string Repository { get; init; }

    /// <summary>Workflow file name. Always a file in the trusted repository, never a path a caller chose.</summary>
    public string WorkflowFile { get; init; } = "generated-target.yml";

    /// <summary>The trusted branch the workflow is dispatched on. The workflow re-asserts this itself.</summary>
    public string Ref { get; init; } = "main";

    /// <summary>Numeric GitHub App identifier. The App is the product's own build identity.</summary>
    public required string AppId { get; init; }

    /// <summary>Installation of that App on the trusted repository.</summary>
    public required string InstallationId { get; init; }

    /// <summary>Key Vault secret identifier holding the App's PEM private key. Never the key itself.</summary>
    public required Uri PrivateKeySecretUri { get; init; }

    /// <summary>Blob endpoint of the approved artifact storage account.</summary>
    public required Uri StorageAccountUri { get; init; }

    /// <summary>Container the product uploads bundles to and reads results from.</summary>
    public string Container { get; init; } = "generated-target";

    /// <summary>Commit of the workbench that generated the bundle, carried into the build for provenance.</summary>
    public required string WorkbenchCommitSha { get; init; }

    /// <summary>Approved deployment target name. The workflow resolves the resource group and app itself.</summary>
    public required string TargetName { get; init; }

    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(45);

    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Reads the options from configuration, or reports exactly what is missing.
    ///
    /// A host that cannot answer every question below has no trusted builder, and the honest result is no
    /// registration at all: the phase then refuses and names this as an unmet host prerequisite. Filling a
    /// gap with a default would produce a product that dispatches somewhere nobody approved.
    /// </summary>
    public static bool TryRead(
        IConfiguration configuration,
        out GitHubActionsDeploymentOptions? options,
        out IReadOnlyList<string> missing)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        options = null;
        List<string> absent = [];

        string? Read(string key)
        {
            string? value = configuration[$"TargetDeployment:{key}"];
            if (string.IsNullOrWhiteSpace(value))
            {
                absent.Add($"TargetDeployment:{key}");
                return null;
            }

            return value.Trim();
        }

        string? owner = Read("GitHub:Owner");
        string? repository = Read("GitHub:Repository");
        string? appId = Read("GitHub:AppId");
        string? installationId = Read("GitHub:InstallationId");
        string? privateKeySecret = Read("GitHub:PrivateKeySecretUri");
        string? storage = Read("Storage:AccountUri");
        string? workbenchSha = Read("WorkbenchCommitSha");
        string? targetName = Read("TargetName");

        if (absent.Count > 0)
        {
            missing = absent;
            return false;
        }

        if (!Uri.TryCreate(privateKeySecret, UriKind.Absolute, out Uri? keyUri) ||
            !string.Equals(keyUri.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal))
        {
            absent.Add("TargetDeployment:GitHub:PrivateKeySecretUri must be an https Key Vault secret identifier.");
        }

        if (!Uri.TryCreate(storage, UriKind.Absolute, out Uri? storageUri) ||
            !string.Equals(storageUri.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal) ||
            !storageUri.IdnHost.EndsWith(".blob.core.windows.net", StringComparison.OrdinalIgnoreCase))
        {
            absent.Add("TargetDeployment:Storage:AccountUri must be an https Azure Blob endpoint.");
        }

        if (workbenchSha is not { Length: 40 } || !workbenchSha.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f'))
        {
            absent.Add("TargetDeployment:WorkbenchCommitSha must be a full lowercase 40-character commit SHA.");
        }

        if (!long.TryParse(appId, NumberStyles.None, CultureInfo.InvariantCulture, out _) ||
            !long.TryParse(installationId, NumberStyles.None, CultureInfo.InvariantCulture, out _))
        {
            absent.Add("TargetDeployment:GitHub:AppId and TargetDeployment:GitHub:InstallationId must be numeric identifiers.");
        }

        if (absent.Count > 0)
        {
            missing = absent;
            return false;
        }

        options = new GitHubActionsDeploymentOptions
        {
            Owner = owner!,
            Repository = repository!,
            WorkflowFile = configuration["TargetDeployment:GitHub:WorkflowFile"] is { Length: > 0 } file ? file : "generated-target.yml",
            Ref = configuration["TargetDeployment:GitHub:Ref"] is { Length: > 0 } reference ? reference : "main",
            AppId = appId!,
            InstallationId = installationId!,
            PrivateKeySecretUri = keyUri!,
            StorageAccountUri = storageUri!,
            Container = configuration["TargetDeployment:Storage:Container"] is { Length: > 0 } container ? container : "generated-target",
            WorkbenchCommitSha = workbenchSha!,
            TargetName = targetName!,
        };

        missing = [];
        return true;
    }
}

/// <summary>
/// Deploys a generated application tier by asking the product's own GitHub App to run the trusted builder.
///
/// The run process never builds an image and never holds a registry or subscription credential. It uploads
/// the bundle to the approved storage account with the runtime's managed identity, dispatches the workflow
/// with an installation token minted from a key it reads out of Key Vault, and then waits for the builder
/// to publish a result document to that same container. Everything that touches Azure inside the build is
/// the runner's federated identity, not this process's.
///
/// The operation identifier makes the whole thing idempotent: a result already present is returned as it
/// stands, and a dispatch claim advanced only by compare-and-swap means two replicas racing on the same
/// run produce one build.
///
/// The authorization this publish runs under is re-asked from the server at each checkpoint rather than
/// captured when the gateway was entered. Packaging, uploading, reading a signing key and minting an
/// installation token are all awaited, and an approval that was live before them says nothing about the
/// instant the dispatch is sent — which is the only instant that mints an image.
/// </summary>
public sealed class GitHubActionsTargetDeploymentGateway : IRevalidatingTargetApplicationDeploymentGateway
{
    private const string BlobApiVersion = "2021-12-02";

    /// <summary>Shape of the durable claim this gateway writes. Read back strictly; an older one is refused.</summary>
    private const string ClaimSchemaVersion = "fleet.target-deployment-claim/2";

    /// <summary>Blob metadata name carrying the digest an uploaded bundle was stored under.</summary>
    private const string ArchiveDigestMetadata = "x-ms-meta-archivesha256";

    /// <summary>
    /// How many times one operation may be dispatched before the gateway stops trying.
    ///
    /// A retry exists for exactly one failure: the process died after claiming the operation and before
    /// it ever attempted a dispatch, which would otherwise leave the operation pending until it timed out
    /// on every later attempt. It is not a general retry loop, so it is small and it is counted durably.
    /// </summary>
    private const int MaxDispatchAttempts = 3;

    private static readonly string[] s_vaultScope = ["https://vault.azure.net/.default"];
    private static readonly string[] s_storageScope = ["https://storage.azure.com/.default"];

    /// <summary>How long an unconfirmed claim is left alone before recovery considers taking it over.</summary>
    private static readonly TimeSpan s_recoveryGrace = TimeSpan.FromMinutes(2);

    /// <summary>Fixed entry timestamp so the same generated tier always produces the same archive digest.</summary>
    private static readonly DateTimeOffset s_epoch = new(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>How this gateway's own durable claim is written and read. Not a contract with the builder.</summary>
    private static readonly JsonSerializerOptions s_claimJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>
    /// The explicit contract the builder's result document is bound with.
    ///
    /// Stated rather than defaulted, because the default contract is PascalCase and the document is
    /// camelCase: every property then binds to nothing, the record deserializes into all-nulls, and an
    /// empty document and a real one become indistinguishable. Case-insensitivity is off deliberately —
    /// matching loosely would hide exactly that mismatch instead of failing on it.
    /// </summary>
    private static readonly JsonSerializerOptions s_wireJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        NumberHandling = JsonNumberHandling.Strict,
        AllowTrailingCommas = false,
        ReadCommentHandling = JsonCommentHandling.Disallow,
    };

    private readonly GitHubActionsDeploymentOptions _options;
    private readonly TokenCredential _credential;
    private readonly HttpClient _http;
    private readonly TimeProvider _time;
    private readonly ITargetDeploymentDispatchSentinel? _sentinel;

    public GitHubActionsTargetDeploymentGateway(
        GitHubActionsDeploymentOptions options,
        TokenCredential credential,
        HttpClient http,
        TimeProvider? time = null)
        : this(options, credential, http, time, sentinel: null)
    {
    }

    private GitHubActionsTargetDeploymentGateway(
        GitHubActionsDeploymentOptions options,
        TokenCredential credential,
        HttpClient http,
        TimeProvider? time,
        ITargetDeploymentDispatchSentinel? sentinel)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(credential);
        ArgumentNullException.ThrowIfNull(http);

        _options = options;
        _credential = credential;
        _http = http;
        _time = time ?? TimeProvider.System;
        _sentinel = sentinel;
    }

    public string Description =>
        $"the trusted '{_options.WorkflowFile}' builder in {_options.Owner}/{_options.Repository} on {_options.Ref}";

    /// <summary>
    /// The same builder, bound to one run's live server checks.
    ///
    /// A new instance rather than a mutation: the host's registration is shared by every run on the
    /// process, and a gateway that could have its run swapped underneath it would revalidate against
    /// whichever run bound it last.
    /// </summary>
    public ITargetApplicationDeploymentGateway BoundTo(ITargetDeploymentDispatchSentinel sentinel)
    {
        ArgumentNullException.ThrowIfNull(sentinel);

        return new GitHubActionsTargetDeploymentGateway(_options, _credential, _http, _time, sentinel);
    }

    /// <summary>
    /// The server's live answer at one checkpoint, or the reason this operation may not pass it.
    ///
    /// An unbound gateway has no live answer and says so rather than proceeding. That is not a theoretical
    /// case: it is what a deployment attempted outside a durable run looks like, and such a run has no
    /// claim to fence and no lease to lose, so there is nothing that could stop a stale dispatch later.
    /// </summary>
    private async Task<string?> WithheldAsync(
        TargetDeploymentCheckpoint checkpoint,
        string operationId,
        CancellationToken cancellationToken)
    {
        if (_sentinel is null)
        {
            return "This deployment builder was never bound to a durable run, so nothing can re-ask the server whether " +
                "this run may still publish at the moment it would. Nothing was dispatched.";
        }

        TargetDeploymentClearance clearance = await _sentinel
            .RevalidateAsync(checkpoint, operationId, cancellationToken)
            .ConfigureAwait(false);

        return clearance.IsCleared ? null : clearance.Reason;
    }

    public async Task<TargetDeploymentResult> PublishAsync(
        TargetDeploymentRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (TargetDeploymentPolicy.RejectBundle(request.Bundle) is { Count: > 0 } badBundle)
        {
            return TargetDeploymentResult.Refused(
                TargetDeploymentState.RefusedByPolicy,
                "The gateway re-checked the bundle and refused it.",
                badBundle);
        }

        // Re-checked here rather than trusted from the adapter, and re-checked again below immediately
        // before the dispatch: this is the last process that touches the request before bytes leave it.
        if (TargetDeploymentPolicy.RejectBinding(request.Binding, _time.GetUtcNow()) is { Count: > 0 } badBinding)
        {
            return TargetDeploymentResult.Refused(
                TargetDeploymentState.RefusedByPolicy,
                "The gateway re-checked the run binding and refused it, so nothing was uploaded.",
                badBinding);
        }

        TargetDeploymentAuthority authority = request.Binding.Authority!;

        // The host names one approved target and the run carries its own. They have to be the same
        // application: a host pointed at a different app than the run's profile names is a configuration
        // drift that would otherwise publish this run's bytes somewhere its approval never mentioned.
        if (!string.Equals(_options.TargetName, authority.TargetResourceName, StringComparison.OrdinalIgnoreCase))
        {
            return TargetDeploymentResult.Refused(
                TargetDeploymentState.RefusedByPolicy,
                "This host is configured for a different deployment target than the one this run's approved target profile " +
                "names, so nothing was uploaded.",
                [
                    $"Host-approved target: {_options.TargetName}.",
                    $"Target this run was approved for: {authority.TargetResourceName}.",
                ]);
        }

        if (!TargetDeploymentPolicy.IsContentDigest(request.OperationId))
        {
            return TargetDeploymentResult.Refused(
                TargetDeploymentState.RefusedByPolicy,
                "The operation identifier is not a 64-character lowercase hex digest, so nothing was dispatched.");
        }

        byte[] archive = Package(request.Bundle);
        string archiveSha = Convert.ToHexStringLower(SHA256.HashData(archive));
        string artifactBlob = TargetDeploymentPolicy.ArtifactBlobName(request.OperationId);
        string artifactUri = $"{_options.StorageAccountUri.GetLeftPart(UriPartial.Authority)}/{_options.Container}/{artifactBlob}";

        if (TargetDeploymentPolicy.RejectArtifactUri(artifactUri) is string badUri)
        {
            return TargetDeploymentResult.Refused(
                TargetDeploymentState.RefusedByPolicy,
                $"The artifact location this host would have handed to the builder was refused by the product's own rule: {badUri}");
        }

        // Derived once, from what this process is about to dispatch, and then used both as a dispatch
        // input and as the thing the builder's report is compared against. A report echoing a binding
        // digest it was never given cannot be read as this operation's.
        TargetDeploymentBuilderExpectation expectation = new(
            request.OperationId,
            request.Binding.RunId,
            archiveSha,
            _options.WorkbenchCommitSha,
            request.Binding.SourceSnapshotHash,
            TargetDeploymentPolicy.PlanDigest(request.Binding),
            TargetDeploymentPolicy.TargetDigest(authority),
            TargetDeploymentPolicy.BindingDigest(
                request.Binding,
                request.OperationId,
                artifactUri,
                archiveSha,
                _options.WorkbenchCommitSha),
            authority.TargetResourceId,
            _time.GetUtcNow());

        try
        {
            // A completed operation is returned as it stands. This is what makes a resumed run after a
            // lease expiry read the deployment that already happened instead of publishing again.
            if (await ReadResultAsync(expectation, cancellationToken).ConfigureAwait(false) is { } existing)
            {
                return existing;
            }

            if (await WithheldAsync(TargetDeploymentCheckpoint.BeforeUpload, request.OperationId, cancellationToken)
                .ConfigureAwait(false) is string beforeUpload)
            {
                return TargetDeploymentResult.Refused(
                    TargetDeploymentState.NotAuthorized,
                    $"Nothing was uploaded: {beforeUpload}");
            }

            if (await UploadArchiveAsync(artifactBlob, archive, archiveSha, cancellationToken).ConfigureAwait(false)
                is string badUpload)
            {
                return TargetDeploymentResult.Refused(TargetDeploymentState.RefusedByPolicy, badUpload);
            }

            if (await AdvanceAsync(request, artifactUri, archiveSha, expectation, cancellationToken).ConfigureAwait(false)
                is { } halted)
            {
                return halted;
            }
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or InvalidOperationException)
        {
            return TargetDeploymentResult.Refused(
                TargetDeploymentState.NotAttempted,
                $"The trusted builder could not be reached, so nothing was published: {exception.Message}");
        }

        return await WaitAsync(request, expectation, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Stores the bundle under this operation, without ever overwriting bytes already stored under it.
    ///
    /// The identifier is derived from the bytes and the binding, so a second upload of the same operation
    /// is the same archive — but "should be" is not a check. A blind overwrite would replace an artifact a
    /// build may already be fetching, which is the one way this process could change what a running build
    /// is building. So the write is create-only and a conflict is resolved by comparing the digest the
    /// existing blob was stored under: equal is a resumption, different is a refusal.
    /// </summary>
    private async Task<string?> UploadArchiveAsync(
        string blob,
        byte[] archive,
        string archiveSha,
        CancellationToken cancellationToken)
    {
        BlobWrite stored = await PutBlobAsync(
            blob,
            archive,
            "application/zip",
            BlobPrecondition.CreateOnly,
            null,
            archiveSha,
            cancellationToken).ConfigureAwait(false);

        if (stored.Applied)
        {
            return null;
        }

        string? present = await ArchiveDigestAsync(blob, cancellationToken).ConfigureAwait(false);

        if (present is null)
        {
            return "A bundle is already stored under this operation identifier and does not state the digest it was " +
                "stored with, so this run could not confirm it is the same bytes. Nothing was overwritten and nothing " +
                "was dispatched.";
        }

        return string.Equals(present, archiveSha, StringComparison.Ordinal)
            ? null
            : "Different bytes are already stored under this operation identifier. The identifier is derived from the " +
                "bundle and the binding, so this cannot happen for a resumption of the same run, and the stored artifact " +
                "was left exactly as it is rather than replaced under a build that may already be reading it.";
    }

    /// <summary>
    /// Moves the operation from uploaded to dispatched, and decides what a claim it did not create means.
    ///
    /// Every transition is a compare-and-swap against the claim's entity tag, so two replicas that read
    /// the same claim cannot both advance it: the loser's write is refused by the store and it falls
    /// through to waiting rather than dispatching a second build of identical bytes. A blind upsert here
    /// would make the claim a record of the last writer rather than a decision about who may dispatch.
    ///
    /// The states are kept apart because they mean genuinely different things. A claim that was never
    /// attempted can be taken over after a grace period, once GitHub has been asked and says no run for
    /// this operation exists. A claim whose dispatch was attempted and whose outcome is unknown — the
    /// request left this process and the response never came back — is <em>not</em> re-dispatched. GitHub
    /// may have accepted it, and a duplicate dispatch would build and deploy the same operation twice
    /// under one run's provenance. That is answered by refusing, keeping the claim and its evidence, and
    /// saying that re-entering the run resumes this exact operation once a run becomes visible.
    ///
    /// Returning non-null stops the operation instead of waiting.
    /// </summary>
    private async Task<TargetDeploymentResult?> AdvanceAsync(
        TargetDeploymentRequest request,
        string artifactUri,
        string archiveSha,
        TargetDeploymentBuilderExpectation expectation,
        CancellationToken cancellationToken)
    {
        string claimBlob = TargetDeploymentPolicy.ClaimBlobName(request.OperationId);
        DateTimeOffset now = _time.GetUtcNow();

        if (await WithheldAsync(TargetDeploymentCheckpoint.BeforeClaim, request.OperationId, cancellationToken)
            .ConfigureAwait(false) is string beforeClaim)
        {
            return TargetDeploymentResult.Refused(
                TargetDeploymentState.NotAuthorized,
                $"No builder was claimed or dispatched for this operation: {beforeClaim}");
        }

        DispatchClaim fresh = new(
            ClaimSchemaVersion,
            DispatchPhase.Claimed,
            request.OperationId,
            request.Binding.RunId,
            archiveSha,
            expectation.BindingDigest,
            now,
            DispatchAttempts: 0,
            LastAttemptUtc: null,
            DispatchedUtc: null,
            Note: null);

        BlobWrite created = await WriteClaimAsync(claimBlob, fresh, BlobPrecondition.CreateOnly, null, cancellationToken)
            .ConfigureAwait(false);

        if (created.Applied)
        {
            return await DispatchOnceAsync(
                request, artifactUri, archiveSha, claimBlob, fresh, created.ETag, now, cancellationToken)
                .ConfigureAwait(false);
        }

        (DispatchClaim Claim, string? ETag)? held =
            await ReadClaimAsync(claimBlob, cancellationToken).ConfigureAwait(false);

        if (held is not { } current)
        {
            return TargetDeploymentResult.Refused(
                TargetDeploymentState.NotAttempted,
                $"Operation {request.OperationId} is already claimed and the claim could not be read, so this run could " +
                "not tell whether a build for it exists. Nothing was dispatched rather than risking a second build of the " +
                "same bytes.");
        }

        if (!string.Equals(current.Claim.RunId, request.Binding.RunId, StringComparison.Ordinal) ||
            !string.Equals(current.Claim.ArtifactSha256, archiveSha, StringComparison.Ordinal) ||
            !string.Equals(current.Claim.BindingDigest, expectation.BindingDigest, StringComparison.Ordinal))
        {
            return TargetDeploymentResult.Refused(
                TargetDeploymentState.RefusedByPolicy,
                $"Operation {request.OperationId} is claimed for a different run, artifact, or binding than this one. " +
                "Nothing was dispatched, and the existing claim was left untouched.");
        }

        if (current.Claim.Phase == DispatchPhase.Dispatched)
        {
            return null;
        }

        DateTimeOffset since = current.Claim.LastAttemptUtc ?? current.Claim.ClaimedUtc;

        if (current.Claim.Phase != DispatchPhase.Ambiguous && now - since < s_recoveryGrace)
        {
            // Another replica is inside the same window this one is. Waiting is correct: its dispatch
            // either lands, in which case a result appears, or its claim ages into recovery below.
            return null;
        }

        RunPresence presence = await RunPresenceAsync(request.OperationId, cancellationToken).ConfigureAwait(false);

        if (presence == RunPresence.Present)
        {
            // Proven: a build for this operation exists. Recorded so no later attempt has to ask again.
            await WriteClaimAsync(
                claimBlob,
                current.Claim with
                {
                    Phase = DispatchPhase.Dispatched,
                    DispatchedUtc = current.Claim.DispatchedUtc ?? now,
                    Note = "A run for this operation was observed in the trusted repository.",
                },
                BlobPrecondition.MatchETag,
                current.ETag,
                cancellationToken).ConfigureAwait(false);

            return null;
        }

        if (presence == RunPresence.Unknown)
        {
            return TargetDeploymentResult.Refused(
                TargetDeploymentState.NotAttempted,
                $"Operation {request.OperationId} is already claimed and this run could not read the trusted repository's " +
                "runs to find out whether a build for it exists. Nothing was dispatched: an unreadable list is not " +
                "evidence that no build is running.");
        }

        if (current.Claim.Phase is DispatchPhase.Dispatching or DispatchPhase.Ambiguous)
        {
            return TargetDeploymentResult.Refused(
                TargetDeploymentState.NotAttempted,
                $"A dispatch for operation {request.OperationId} left this product and its outcome is unknown: no run for " +
                "it is visible, and the request may still have been accepted. Nothing was dispatched again, because a " +
                "second dispatch would build and deploy this one operation twice. The claim and its evidence are retained, " +
                "and re-entering this run resumes this exact operation — its identifier is derived from the bytes and the " +
                "binding, not from the attempt.",
                [
                    $"Claim phase: {current.Claim.Phase}.",
                    $"Dispatch attempts recorded: {current.Claim.DispatchAttempts.ToString(CultureInfo.InvariantCulture)}.",
                    current.Claim.Note is { Length: > 0 } note
                        ? $"Recorded at the time: {note}"
                        : "No further detail was recorded at the time.",
                ]);
        }

        if (current.Claim.DispatchAttempts >= MaxDispatchAttempts)
        {
            return TargetDeploymentResult.Refused(
                TargetDeploymentState.NotAttempted,
                $"Operation {request.OperationId} was claimed " +
                $"{current.Claim.DispatchAttempts.ToString(CultureInfo.InvariantCulture)} times and no build for it " +
                "exists, so this gateway stopped re-dispatching rather than looping. The bytes are uploaded and the " +
                "operation identifier is derived from them, so a later attempt re-enters the same operation once the " +
                "cause is fixed.",
                ["Nothing was deployed and nothing was left running. The claim is retained for inspection."]);
        }

        return await DispatchOnceAsync(
            request, artifactUri, archiveSha, claimBlob, current.Claim, current.ETag, now, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Marks one dispatch attempt, sends it, and records what became of it.
    ///
    /// The attempt is written before the request leaves, under compare-and-swap, so the durable record of
    /// "a dispatch was attempted" cannot be lost by the same crash that loses the response. Losing the
    /// compare-and-swap means another replica advanced this claim first, and the correct answer is to wait
    /// for its build rather than to start one.
    /// </summary>
    private async Task<TargetDeploymentResult?> DispatchOnceAsync(
        TargetDeploymentRequest request,
        string artifactUri,
        string archiveSha,
        string claimBlob,
        DispatchClaim claim,
        string? etag,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        DispatchClaim attempting = claim with
        {
            Phase = DispatchPhase.Dispatching,
            DispatchAttempts = claim.DispatchAttempts + 1,
            LastAttemptUtc = now,
            Note = null,
        };

        BlobWrite marked = await WriteClaimAsync(
            claimBlob, attempting, BlobPrecondition.MatchETag, etag, cancellationToken).ConfigureAwait(false);

        if (!marked.Applied)
        {
            return null;
        }

        string? refusal;
        try
        {
            refusal = await DispatchAsync(request, artifactUri, archiveSha, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or TaskCanceledException)
        {
            await WriteClaimAsync(
                claimBlob,
                attempting with
                {
                    Phase = DispatchPhase.Ambiguous,
                    Note = $"The dispatch request failed without a usable response: {exception.Message}",
                },
                BlobPrecondition.MatchETag,
                marked.ETag,
                cancellationToken).ConfigureAwait(false);

            return TargetDeploymentResult.Refused(
                TargetDeploymentState.NotAttempted,
                $"The dispatch for operation {request.OperationId} left this product without a usable response, so whether " +
                "a build was started is unknown. Nothing was dispatched again. Re-entering this run resumes this exact " +
                "operation, which will either find the run or report the same ambiguity rather than building twice.",
                [$"Recorded at the time: {exception.Message}"]);
        }

        if (refusal is not null)
        {
            // Nothing left this process, so the attempt is unwound to the state it was taken from. The
            // count is not spent on an attempt that was never made.
            await WriteClaimAsync(
                claimBlob,
                claim with { Note = $"A dispatch was prepared and then refused before it was sent: {refusal}" },
                BlobPrecondition.MatchETag,
                marked.ETag,
                cancellationToken).ConfigureAwait(false);

            return TargetDeploymentResult.Refused(
                TargetDeploymentState.NotAuthorized,
                $"No builder was dispatched for operation {request.OperationId}: {refusal}");
        }

        await WriteClaimAsync(
            claimBlob,
            attempting with { Phase = DispatchPhase.Dispatched, DispatchedUtc = _time.GetUtcNow() },
            BlobPrecondition.MatchETag,
            marked.ETag,
            cancellationToken).ConfigureAwait(false);

        return null;
    }

    private async Task<(DispatchClaim Claim, string? ETag)?> ReadClaimAsync(
        string blob,
        CancellationToken cancellationToken)
    {
        (byte[] Content, string? ETag)? stored = await GetBlobAsync(blob, cancellationToken).ConfigureAwait(false);

        if (stored is not { } found)
        {
            return null;
        }

        try
        {
            DispatchClaim? claim = JsonSerializer.Deserialize<DispatchClaim>(found.Content, s_claimJson);

            return claim is null || !string.Equals(claim.SchemaVersion, ClaimSchemaVersion, StringComparison.Ordinal)
                ? null
                : (claim, found.ETag);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Whether the trusted repository holds a run for this operation, or whether that is unknown.</summary>
    private enum RunPresence
    {
        Present,
        Absent,
        Unknown,
    }

    /// <summary>
    /// Asks the trusted repository whether a run for this operation exists.
    ///
    /// The dispatch call returns no identifier and the inputs a run was started with are not listed, so
    /// the run's title is the only handle a dispatch leaves behind. The title is the one the workflow sets
    /// from the operation identifier, and it is matched exactly: the previous reading of this looked at
    /// the <c>name</c> field, which carries the workflow's own name and never the per-run title, so it
    /// matched nothing and every recovery concluded that no build existed.
    ///
    /// A failure to read is <see cref="RunPresence.Unknown"/>, never "absent": an unreadable list must
    /// never become the reason a second build starts.
    /// </summary>
    private async Task<RunPresence> RunPresenceAsync(string operationId, CancellationToken cancellationToken)
    {
        string expected = TargetDeploymentPolicy.ExpectedRunTitle(_options.WorkflowFile, operationId);

        try
        {
            string token = await InstallationTokenAsync(cancellationToken).ConfigureAwait(false);

            using HttpRequestMessage message = new(
                HttpMethod.Get,
                $"https://api.github.com/repos/{_options.Owner}/{_options.Repository}/actions/workflows/" +
                $"{_options.WorkflowFile}/runs?branch={Uri.EscapeDataString(_options.Ref)}" +
                "&event=workflow_dispatch&per_page=100");
            message.Headers.Authorization = new("Bearer", token);
            message.Headers.Accept.ParseAdd("application/vnd.github+json");
            message.Headers.UserAgent.ParseAdd("oracle-forms-migration-fleet");
            message.Headers.Add("X-GitHub-Api-Version", "2022-11-28");

            using HttpResponseMessage response = await _http.SendAsync(message, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            using JsonDocument document = JsonDocument.Parse(
                await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false));

            if (!document.RootElement.TryGetProperty("workflow_runs", out JsonElement runs) ||
                runs.ValueKind != JsonValueKind.Array)
            {
                return RunPresence.Unknown;
            }

            foreach (JsonElement run in runs.EnumerateArray())
            {
                if (run.TryGetProperty("display_title", out JsonElement title) &&
                    string.Equals(title.GetString(), expected, StringComparison.Ordinal))
                {
                    return RunPresence.Present;
                }
            }

            return RunPresence.Absent;
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or InvalidOperationException)
        {
            return RunPresence.Unknown;
        }
    }

    /// <summary>
    /// Packs the generated tier into a deterministic archive.
    ///
    /// Entries are ordered, timestamped from a fixed epoch, and stored without compression variation, so
    /// the same generated bytes always produce the same archive digest. That is what lets the digest be
    /// the dedupe key and the integrity check at once. The leading <c>application/</c> segment is dropped
    /// so the builder stages the tier at a path the host-owned recipe knows.
    /// </summary>
    internal static byte[] Package(TargetDeploymentBundle bundle)
    {
        ArgumentNullException.ThrowIfNull(bundle);

        WorkspaceWriter workspace = new(bundle.WorkspaceRoot);
        using MemoryStream buffer = new();

        using (ZipArchive zip = new(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (TargetDeploymentFile file in bundle.Files.OrderBy(file => file.Path, StringComparer.Ordinal))
            {
                string entryName = file.Path.StartsWith("application/", StringComparison.Ordinal)
                    ? file.Path["application/".Length..]
                    : file.Path;

                if (entryName.Length == 0)
                {
                    continue;
                }

                string absolute = workspace.Resolve($"{bundle.BundleRoot}/{file.Path}");
                byte[] content = File.ReadAllBytes(absolute);
                string actual = Convert.ToHexStringLower(SHA256.HashData(content));

                if (!string.Equals(actual, file.ContentSha256, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"'{file.Path}' changed between being digested and being packaged, so nothing was published.");
                }

                ZipArchiveEntry entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);
                entry.LastWriteTime = s_epoch;
                using Stream sink = entry.Open();
                sink.Write(content);
            }
        }

        return buffer.ToArray();
    }

    private async Task<TargetDeploymentResult> WaitAsync(
        TargetDeploymentRequest request,
        TargetDeploymentBuilderExpectation expectation,
        CancellationToken cancellationToken)
    {
        DateTimeOffset deadline = _time.GetUtcNow() + _options.Timeout;

        while (_time.GetUtcNow() < deadline)
        {
            await Task.Delay(_options.PollInterval, _time, cancellationToken).ConfigureAwait(false);

            TargetDeploymentResult? result;
            try
            {
                result = await ReadResultAsync(expectation, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is HttpRequestException or IOException)
            {
                continue;
            }

            if (result is not null)
            {
                return result;
            }
        }

        return TargetDeploymentResult.Refused(
            TargetDeploymentState.TimedOut,
            $"The trusted builder did not publish a result for operation {request.OperationId} within " +
            $"{_options.Timeout.TotalMinutes.ToString("0", CultureInfo.InvariantCulture)} minutes. Nothing here says the " +
            "deployment failed; it says this run stopped waiting, and the operation is safe to re-enter because its " +
            "identifier is derived from the bytes rather than from the attempt.",
            [$"Operation {request.OperationId} remains claimed; a later attempt reads its result instead of rebuilding."]);
    }

    /// <summary>
    /// Reads the builder's result document, and refuses anything that is not a verifiable deployment.
    ///
    /// The document is bound with an explicit camelCase contract rather than a host default, because the
    /// default contract binds none of its properties: the record then deserializes into all-nulls and an
    /// empty document becomes indistinguishable from a real one. Everything it claims is then compared
    /// against what this process actually dispatched, and a deployment is only recorded when the builder
    /// also states that it observed the revision it deployed answering.
    ///
    /// The server is asked one last time before a deployment is recorded under this run. A run whose
    /// authorization lapsed while the builder worked does not get a deployment entered under its name —
    /// and because something really is running, the refusal says what it is rather than staying silent.
    /// </summary>
    private async Task<TargetDeploymentResult?> ReadResultAsync(
        TargetDeploymentBuilderExpectation expectation,
        CancellationToken cancellationToken)
    {
        (byte[] Content, string? ETag)? stored = await GetBlobAsync(
            TargetDeploymentPolicy.ResultBlobName(expectation.OperationId), cancellationToken).ConfigureAwait(false);

        if (stored is not { } found)
        {
            return null;
        }

        TargetDeploymentBuilderReport? reported;
        try
        {
            reported = JsonSerializer.Deserialize<TargetDeploymentBuilderReport>(found.Content, s_wireJson);
        }
        catch (JsonException exception)
        {
            return TargetDeploymentResult.Refused(
                TargetDeploymentState.VerificationFailed,
                $"The builder's result document could not be read: {exception.Message}");
        }

        TargetDeploymentBuilderExpectation now = expectation with { NowUtc = _time.GetUtcNow() };

        if (TargetDeploymentPolicy.RejectReport(reported, now) is { Count: > 0 } rejections)
        {
            return new TargetDeploymentResult(
                TargetDeploymentState.VerificationFailed,
                reported?.BuildRunId,
                reported?.ImageDigest,
                reported?.RevisionName,
                reported?.ApplicationUrl,
                rejections,
                "The builder reported a deployment this product could not verify.")
            {
                DeployedResourceId = reported?.DeployedResourceId,
            };
        }

        TargetDeploymentState state = TargetDeploymentPolicy.ReportedState(reported!.State)!.Value;

        if (state != TargetDeploymentState.Deployed)
        {
            return TargetDeploymentResult.Refused(
                state,
                $"The trusted builder ran and reported {reported.State}. Nothing of this run's is serving.",
                [$"Built by {_options.Owner}/{_options.Repository} run {reported.BuildRunId ?? "(unnamed)"}."]);
        }

        if (await WithheldAsync(
                TargetDeploymentCheckpoint.BeforeResult, expectation.OperationId, cancellationToken)
            .ConfigureAwait(false) is string beforeResult)
        {
            return TargetDeploymentResult.Refused(
                TargetDeploymentState.NotAuthorized,
                "The builder reported a deployment and this run may no longer record one, so nothing was entered under " +
                $"its name: {beforeResult}",
                [
                    $"A revision really is running: {reported.RevisionName} on image {reported.ImageDigest}.",
                    $"It was deployed to {reported.DeployedResourceId} by run {reported.BuildRunId}.",
                    "This product did not undo it. It refused to claim it as this run's verified deployment.",
                ]);
        }

        return new TargetDeploymentResult(
            TargetDeploymentState.Deployed,
            reported.BuildRunId,
            reported.ImageDigest,
            reported.RevisionName,
            reported.ApplicationUrl,
            [
                $"Built by {_options.Owner}/{_options.Repository} run {reported.BuildRunId}.",
                $"Workbench commit carried into the build: {reported.WorkbenchSha}.",
                $"The builder observed {reported.ReadinessProbeUrl} answering at {reported.ReadinessObservedUtc}.",
            ],
            null)
        {
            DeployedResourceId = reported.DeployedResourceId,
        };
    }

    /// <summary>
    /// Sends the dispatch, or returns the reason it was not sent.
    ///
    /// Two checks sit between the token and the request, and their position is the point of them. Reading
    /// the signing key out of Key Vault and minting an installation token are awaited round trips, so an
    /// authorization confirmed before them is an authorization from before an unbounded wait. The server
    /// is therefore asked again <em>after</em> the token is in hand and immediately before the request is
    /// sent, and the approval's own expiry is re-read against the clock as it stands at that instant.
    ///
    /// A reason returned here means nothing left this process.
    /// </summary>
    private async Task<string?> DispatchAsync(
        TargetDeploymentRequest request,
        string artifactUri,
        string artifactSha256,
        CancellationToken cancellationToken)
    {
        if (TargetDeploymentPolicy.RejectAuthority(request.Binding.Authority, _time.GetUtcNow()) is { Count: > 0 } lapsed)
        {
            return "The approval for this destination no longer holds: " + string.Join(" ", lapsed);
        }

        string token = await InstallationTokenAsync(cancellationToken).ConfigureAwait(false);

        if (await WithheldAsync(TargetDeploymentCheckpoint.BeforeDispatch, request.OperationId, cancellationToken)
            .ConfigureAwait(false) is string withheld)
        {
            return withheld;
        }

        if (TargetDeploymentPolicy.RejectAuthority(request.Binding.Authority, _time.GetUtcNow()) is { Count: > 0 } expired)
        {
            return "The approval for this destination lapsed while the builder credential was being acquired: " +
                string.Join(" ", expired);
        }

        TargetDeploymentAuthority authority = request.Binding.Authority!;

        Dictionary<string, string> inputs = new(StringComparer.Ordinal)
        {
            ["operation_id"] = request.OperationId,
            ["artifact_uri"] = artifactUri,
            ["artifact_sha256"] = artifactSha256,
            ["run_id"] = request.Binding.RunId,
            ["workbench_sha"] = _options.WorkbenchCommitSha,
            ["source_snapshot_sha256"] = request.Binding.SourceSnapshotHash,
            ["plan_digest"] = TargetDeploymentPolicy.PlanDigest(request.Binding),
            ["target"] = authority.TargetResourceName,
            ["target_resource_id"] = authority.TargetResourceId,
            ["target_digest"] = TargetDeploymentPolicy.TargetDigest(authority),
            ["approval_expires_utc"] = TargetDeploymentPolicy.ExpiryText(authority),
            ["binding_digest"] = TargetDeploymentPolicy.BindingDigest(
                request.Binding,
                request.OperationId,
                artifactUri,
                artifactSha256,
                _options.WorkbenchCommitSha),
        };

        using HttpRequestMessage message = new(
            HttpMethod.Post,
            $"https://api.github.com/repos/{_options.Owner}/{_options.Repository}/actions/workflows/{_options.WorkflowFile}/dispatches");
        message.Headers.Authorization = new("Bearer", token);
        message.Headers.Accept.ParseAdd("application/vnd.github+json");
        message.Headers.UserAgent.ParseAdd("oracle-forms-migration-fleet");
        message.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        message.Content = new StringContent(
            JsonSerializer.Serialize(new { @ref = _options.Ref, inputs }),
            Encoding.UTF8,
            "application/json");

        using HttpResponseMessage response = await _http.SendAsync(message, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        return null;
    }

    /// <summary>
    /// Mints an installation access token for the product's own GitHub App.
    ///
    /// The App's private key is read from Key Vault with the runtime's managed identity at the moment it
    /// is used, signs a short-lived assertion, and is never retained. No operator token can substitute for
    /// it: a deployment performed on a signed-in person's behalf would attribute the product's side effects
    /// to that person and would keep working after their access was removed.
    /// </summary>
    private async Task<string> InstallationTokenAsync(CancellationToken cancellationToken)
    {
        string pem = await ReadSecretAsync(cancellationToken).ConfigureAwait(false);

        using RSA rsa = RSA.Create();
        rsa.ImportFromPem(pem);

        long now = _time.GetUtcNow().ToUnixTimeSeconds();
        string header = Base64Url(Encoding.UTF8.GetBytes("""{"alg":"RS256","typ":"JWT"}"""));
        string payload = Base64Url(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            iat = now - 60,
            exp = now + 540,
            iss = _options.AppId,
        })));
        string signature = Base64Url(rsa.SignData(
            Encoding.ASCII.GetBytes($"{header}.{payload}"),
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1));

        using HttpRequestMessage message = new(
            HttpMethod.Post,
            $"https://api.github.com/app/installations/{_options.InstallationId}/access_tokens");
        message.Headers.Authorization = new("Bearer", $"{header}.{payload}.{signature}");
        message.Headers.Accept.ParseAdd("application/vnd.github+json");
        message.Headers.UserAgent.ParseAdd("oracle-forms-migration-fleet");
        message.Headers.Add("X-GitHub-Api-Version", "2022-11-28");

        using HttpResponseMessage response = await _http.SendAsync(message, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        using JsonDocument document = JsonDocument.Parse(
            await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false));

        return document.RootElement.TryGetProperty("token", out JsonElement token) && token.GetString() is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException("The installation token response carried no token.");
    }

    private async Task<string> ReadSecretAsync(CancellationToken cancellationToken)
    {
        AccessToken token = await _credential
            .GetTokenAsync(new TokenRequestContext(s_vaultScope), cancellationToken)
            .ConfigureAwait(false);

        using HttpRequestMessage message = new(
            HttpMethod.Get,
            $"{_options.PrivateKeySecretUri.GetLeftPart(UriPartial.Path)}?api-version=7.4");
        message.Headers.Authorization = new("Bearer", token.Token);

        using HttpResponseMessage response = await _http.SendAsync(message, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        using JsonDocument document = JsonDocument.Parse(
            await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false));

        return document.RootElement.TryGetProperty("value", out JsonElement value) && value.GetString() is { Length: > 0 } pem
            ? pem
            : throw new InvalidOperationException("The configured Key Vault secret carried no value.");
    }

    /// <summary>How a blob write is conditioned. There is no unconditional write in this gateway.</summary>
    private enum BlobPrecondition
    {
        /// <summary>Only if nothing is there. A losing race is reported, never an error.</summary>
        CreateOnly,

        /// <summary>Only if the blob is still exactly the version that was read.</summary>
        MatchETag,
    }

    /// <summary>Whether a conditional write was applied, and the version it produced.</summary>
    private readonly record struct BlobWrite(bool Applied, string? ETag);

    private Task<BlobWrite> WriteClaimAsync(
        string name,
        DispatchClaim claim,
        BlobPrecondition precondition,
        string? etag,
        CancellationToken cancellationToken) =>
        PutBlobAsync(
            name,
            JsonSerializer.SerializeToUtf8Bytes(claim, s_claimJson),
            "application/json",
            precondition,
            etag,
            archiveSha256: null,
            cancellationToken);

    /// <summary>
    /// Writes a blob under a precondition the store enforces.
    ///
    /// Compare-and-swap rather than an upsert, because the claim is a decision about which replica may
    /// dispatch, not a log of the last writer. A refused precondition returns false so the caller can do
    /// the only safe thing — stop and let the replica that won proceed — instead of racing it.
    /// </summary>
    private async Task<BlobWrite> PutBlobAsync(
        string name,
        byte[] content,
        string contentType,
        BlobPrecondition precondition,
        string? etag,
        string? archiveSha256,
        CancellationToken cancellationToken)
    {
        using HttpRequestMessage message = new(HttpMethod.Put, BlobUri(name));
        await AuthorizeStorageAsync(message, cancellationToken).ConfigureAwait(false);
        message.Headers.Add("x-ms-blob-type", "BlockBlob");

        if (precondition == BlobPrecondition.CreateOnly)
        {
            message.Headers.IfNoneMatch.ParseAdd("*");
        }
        else if (etag is { Length: > 0 })
        {
            message.Headers.IfMatch.ParseAdd(etag);
        }
        else
        {
            // A compare-and-swap with nothing to compare against is an upsert wearing its name.
            throw new InvalidOperationException(
                $"'{name}' was to be replaced without the version it was read at, so nothing was written.");
        }

        if (archiveSha256 is { Length: > 0 })
        {
            message.Headers.Add(ArchiveDigestMetadata, archiveSha256);
        }

        message.Content = new ByteArrayContent(content);
        message.Content.Headers.ContentType = new(contentType);

        using HttpResponseMessage response = await _http.SendAsync(message, cancellationToken).ConfigureAwait(false);

        if (response.StatusCode is System.Net.HttpStatusCode.Conflict or System.Net.HttpStatusCode.PreconditionFailed
            or System.Net.HttpStatusCode.NotModified)
        {
            return new BlobWrite(false, null);
        }

        response.EnsureSuccessStatusCode();
        return new BlobWrite(true, response.Headers.ETag?.ToString());
    }

    private async Task<(byte[] Content, string? ETag)?> GetBlobAsync(string name, CancellationToken cancellationToken)
    {
        using HttpRequestMessage message = new(HttpMethod.Get, BlobUri(name));
        await AuthorizeStorageAsync(message, cancellationToken).ConfigureAwait(false);

        using HttpResponseMessage response = await _http.SendAsync(message, cancellationToken).ConfigureAwait(false);

        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();

        return (
            await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false),
            response.Headers.ETag?.ToString());
    }

    /// <summary>The digest a stored bundle was uploaded under, or null when it does not state one.</summary>
    private async Task<string?> ArchiveDigestAsync(string name, CancellationToken cancellationToken)
    {
        using HttpRequestMessage message = new(HttpMethod.Head, BlobUri(name));
        await AuthorizeStorageAsync(message, cancellationToken).ConfigureAwait(false);

        using HttpResponseMessage response = await _http.SendAsync(message, cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        return response.Headers.TryGetValues(ArchiveDigestMetadata, out IEnumerable<string>? values)
            ? values.FirstOrDefault()
            : null;
    }

    private string BlobUri(string name) =>
        $"{_options.StorageAccountUri.GetLeftPart(UriPartial.Authority)}/{_options.Container}/{name}";

    private async Task AuthorizeStorageAsync(HttpRequestMessage message, CancellationToken cancellationToken)
    {
        AccessToken token = await _credential
            .GetTokenAsync(new TokenRequestContext(s_storageScope), cancellationToken)
            .ConfigureAwait(false);

        message.Headers.Authorization = new("Bearer", token.Token);
        message.Headers.Add("x-ms-version", BlobApiVersion);
    }

    private static string Base64Url(byte[] value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>
    /// Where one operation's dispatch has got to, as a small state machine rather than a pair of flags.
    ///
    /// The distinction that matters is between <see cref="DispatchPhase.Claimed"/> — nothing has been sent,
    /// so another replica may take it over — and <see cref="DispatchPhase.Dispatching"/> or
    /// <see cref="DispatchPhase.Ambiguous"/>, where a request left this product and may have been accepted.
    /// Collapsing those into "not dispatched yet" is what turns one lost response into two builds.
    /// </summary>
    private enum DispatchPhase
    {
        /// <summary>The operation is claimed and no dispatch has been attempted.</summary>
        Claimed,

        /// <summary>A dispatch is in flight or died in flight. Its outcome is not known.</summary>
        Dispatching,

        /// <summary>A build for this operation is known to exist.</summary>
        Dispatched,

        /// <summary>A dispatch was sent and no usable response came back. Never re-sent automatically.</summary>
        Ambiguous,
    }

    /// <summary>
    /// What one replica recorded about one operation, advanced only by compare-and-swap.
    ///
    /// The artifact digest and binding digest are carried so a claim found under this operation identifier
    /// can be checked to be about this run's bytes rather than assumed to be.
    /// </summary>
    private sealed record DispatchClaim(
        string SchemaVersion,
        DispatchPhase Phase,
        string OperationId,
        string RunId,
        string ArtifactSha256,
        string BindingDigest,
        DateTimeOffset ClaimedUtc,
        int DispatchAttempts,
        DateTimeOffset? LastAttemptUtc,
        DateTimeOffset? DispatchedUtc,
        string? Note);
}
