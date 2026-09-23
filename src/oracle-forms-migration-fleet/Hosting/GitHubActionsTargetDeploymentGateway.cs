// Copyright (c) Microsoft. All rights reserved.

using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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
/// stands, and a dispatch marker written with a conditional PUT means two replicas racing on the same run
/// produce one build.
/// </summary>
public sealed class GitHubActionsTargetDeploymentGateway : ITargetApplicationDeploymentGateway
{
    private const string BlobApiVersion = "2021-12-02";

    /// <summary>
    /// How many times one operation may be dispatched before the gateway stops trying.
    ///
    /// A retry exists for exactly one failure: the process died after claiming the operation and before
    /// GitHub accepted the dispatch, which would otherwise leave the operation pending until it timed out
    /// on every later attempt. It is not a general retry loop, so it is small and it is counted durably.
    /// </summary>
    private const int MaxDispatchAttempts = 3;

    private static readonly string[] s_vaultScope = ["https://vault.azure.net/.default"];
    private static readonly string[] s_storageScope = ["https://storage.azure.com/.default"];

    /// <summary>How long an unconfirmed claim is left alone before recovery considers re-dispatching.</summary>
    private static readonly TimeSpan s_recoveryGrace = TimeSpan.FromMinutes(2);

    /// <summary>Fixed entry timestamp so the same generated tier always produces the same archive digest.</summary>
    private static readonly DateTimeOffset s_epoch = new(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static readonly JsonSerializerOptions s_json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly GitHubActionsDeploymentOptions _options;
    private readonly TokenCredential _credential;
    private readonly HttpClient _http;
    private readonly TimeProvider _time;

    public GitHubActionsTargetDeploymentGateway(
        GitHubActionsDeploymentOptions options,
        TokenCredential credential,
        HttpClient http,
        TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(credential);
        ArgumentNullException.ThrowIfNull(http);

        _options = options;
        _credential = credential;
        _http = http;
        _time = time ?? TimeProvider.System;
    }

    public string Description =>
        $"the trusted '{_options.WorkflowFile}' builder in {_options.Owner}/{_options.Repository} on {_options.Ref}";

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
        string prefix = $"operations/{request.OperationId}";
        string artifactBlob = $"{prefix}/generated-application.zip";

        // A completed operation is returned as it stands. This is what makes a resumed run after a lease
        // expiry read the deployment that already happened instead of publishing the same bytes again.
        if (await ReadResultAsync(prefix, authority, cancellationToken).ConfigureAwait(false) is { } existing)
        {
            return existing;
        }

        string artifactUri = $"{_options.StorageAccountUri.GetLeftPart(UriPartial.Authority)}/{_options.Container}/{artifactBlob}";

        if (TargetDeploymentPolicy.RejectArtifactUri(artifactUri) is string badUri)
        {
            return TargetDeploymentResult.Refused(
                TargetDeploymentState.RefusedByPolicy,
                $"The artifact location this host would have handed to the builder was refused by the product's own rule: {badUri}");
        }

        try
        {
            await PutBlobAsync(artifactBlob, archive, "application/zip", overwrite: true, cancellationToken).ConfigureAwait(false);

            if (await AdvanceAsync(request, artifactUri, archiveSha, prefix, cancellationToken).ConfigureAwait(false)
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

        return await WaitAsync(prefix, request, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Moves the operation from uploaded to dispatched, and recovers an operation that was claimed but
    /// never dispatched.
    ///
    /// The failure this exists for is narrow and real: the process claims the operation with a conditional
    /// write and then dies before GitHub accepts the dispatch. The claim outlives it, so every later
    /// attempt used to see "already claimed", wait for a result that no builder was ever asked to produce,
    /// and time out — permanently pending, with nothing anywhere saying why.
    ///
    /// Recovery is bounded and evidence-led rather than optimistic. The claim is durable state carrying an
    /// attempt count, and before re-dispatching anything the gateway asks GitHub whether a run for this
    /// operation already exists. A run that exists is waited on; only the absence of one, after a grace
    /// period and under a hard attempt ceiling, produces a second dispatch. Returning non-null stops the
    /// operation instead of waiting.
    /// </summary>
    private async Task<TargetDeploymentResult?> AdvanceAsync(
        TargetDeploymentRequest request,
        string artifactUri,
        string archiveSha,
        string prefix,
        CancellationToken cancellationToken)
    {
        string claimBlob = $"{prefix}/dispatch.json";
        DateTimeOffset now = _time.GetUtcNow();

        DispatchClaim claim = new(
            request.OperationId,
            request.Binding.RunId,
            archiveSha,
            now,
            DispatchAttempts: 0,
            LastAttemptUtc: null,
            DispatchedUtc: null);

        // The first replica to claim the operation dispatches it; a second one falls through to the
        // recovery path rather than starting a duplicate build of identical bytes.
        bool mine = await PutBlobAsync(
            claimBlob,
            JsonSerializer.SerializeToUtf8Bytes(claim, s_json),
            "application/json",
            overwrite: false,
            cancellationToken).ConfigureAwait(false);

        if (!mine)
        {
            DispatchClaim? existing = await ReadClaimAsync(claimBlob, cancellationToken).ConfigureAwait(false);

            if (existing is { DispatchedUtc: not null })
            {
                return null;
            }

            if (await RunExistsAsync(request.OperationId, cancellationToken).ConfigureAwait(false))
            {
                return null;
            }

            DateTimeOffset since = existing?.LastAttemptUtc ?? existing?.ClaimedUtc ?? now;
            if (existing is not null && now - since < s_recoveryGrace)
            {
                return null;
            }

            int attempts = existing?.DispatchAttempts ?? 0;
            if (attempts >= MaxDispatchAttempts)
            {
                return TargetDeploymentResult.Refused(
                    TargetDeploymentState.NotAttempted,
                    $"Operation {request.OperationId} was claimed {attempts.ToString(CultureInfo.InvariantCulture)} times and " +
                    "no build for it exists, so this gateway stopped re-dispatching rather than looping. The bytes are " +
                    "uploaded and the operation identifier is derived from them, so a later attempt re-enters the same " +
                    "operation once the cause is fixed.",
                    ["Nothing was deployed and nothing was left running. The claim is retained for inspection."]);
            }

            claim = (existing ?? claim) with { DispatchAttempts = attempts };
        }

        claim = claim with { DispatchAttempts = claim.DispatchAttempts + 1, LastAttemptUtc = now };
        await PutBlobAsync(
            claimBlob,
            JsonSerializer.SerializeToUtf8Bytes(claim, s_json),
            "application/json",
            overwrite: true,
            cancellationToken).ConfigureAwait(false);

        await DispatchAsync(request, artifactUri, archiveSha, cancellationToken).ConfigureAwait(false);

        // Written only after GitHub accepted the dispatch, so this is the fact recovery reads: a claim
        // without it is a claim whose dispatch is unproven, which is exactly the state that used to hang.
        await PutBlobAsync(
            claimBlob,
            JsonSerializer.SerializeToUtf8Bytes(claim with { DispatchedUtc = _time.GetUtcNow() }, s_json),
            "application/json",
            overwrite: true,
            cancellationToken).ConfigureAwait(false);

        return null;
    }

    private async Task<DispatchClaim?> ReadClaimAsync(string blob, CancellationToken cancellationToken)
    {
        byte[]? content = await GetBlobAsync(blob, cancellationToken).ConfigureAwait(false);

        if (content is null)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<DispatchClaim>(content, s_json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Whether GitHub already holds a run for this operation.
    ///
    /// The workflow names its run after the operation, which is the only handle a dispatch leaves behind:
    /// the dispatch call itself returns no identifier, and the inputs a run was started with are not
    /// listed. A failure to read this is answered as "a run exists" so an unreadable list can never be the
    /// reason a second build starts.
    /// </summary>
    private async Task<bool> RunExistsAsync(string operationId, CancellationToken cancellationToken)
    {
        try
        {
            string token = await InstallationTokenAsync(cancellationToken).ConfigureAwait(false);

            using HttpRequestMessage message = new(
                HttpMethod.Get,
                $"https://api.github.com/repos/{_options.Owner}/{_options.Repository}/actions/workflows/" +
                $"{_options.WorkflowFile}/runs?branch={Uri.EscapeDataString(_options.Ref)}&per_page=100");
            message.Headers.Authorization = new("Bearer", token);
            message.Headers.Accept.ParseAdd("application/vnd.github+json");
            message.Headers.UserAgent.ParseAdd("oracle-forms-migration-fleet");
            message.Headers.Add("X-GitHub-Api-Version", "2022-11-28");

            using HttpResponseMessage response = await _http.SendAsync(message, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            using JsonDocument document = JsonDocument.Parse(
                await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false));

            if (!document.RootElement.TryGetProperty("workflow_runs", out JsonElement runs))
            {
                return true;
            }

            foreach (JsonElement run in runs.EnumerateArray())
            {
                if (run.TryGetProperty("name", out JsonElement name) &&
                    name.GetString() is { Length: > 0 } text &&
                    text.Contains(operationId, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or InvalidOperationException)
        {
            return true;
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
        string prefix,
        TargetDeploymentRequest request,
        CancellationToken cancellationToken)
    {
        DateTimeOffset deadline = _time.GetUtcNow() + _options.Timeout;

        while (_time.GetUtcNow() < deadline)
        {
            await Task.Delay(_options.PollInterval, _time, cancellationToken).ConfigureAwait(false);

            TargetDeploymentResult? result;
            try
            {
                result = await ReadResultAsync(prefix, request.Binding.Authority, cancellationToken).ConfigureAwait(false);
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
    /// The document is treated as a report, not as an authority: the digest shape, the URL host, the
    /// operation identity, and the Azure resource it claims to have updated are re-checked here against
    /// the destination this run was approved for, so a malformed, mismatched, or misdirected result is
    /// recorded as a failed verification rather than as a deployment nobody confirmed.
    /// </summary>
    private async Task<TargetDeploymentResult?> ReadResultAsync(
        string prefix,
        TargetDeploymentAuthority? authority,
        CancellationToken cancellationToken)
    {
        byte[]? content = await GetBlobAsync($"{prefix}/result.json", cancellationToken).ConfigureAwait(false);

        if (content is null)
        {
            return null;
        }

        BuilderResult? reported;
        try
        {
            reported = JsonSerializer.Deserialize<BuilderResult>(content);
        }
        catch (JsonException exception)
        {
            return TargetDeploymentResult.Refused(
                TargetDeploymentState.VerificationFailed,
                $"The builder's result document could not be read: {exception.Message}");
        }

        if (reported is null)
        {
            return TargetDeploymentResult.Refused(
                TargetDeploymentState.VerificationFailed,
                "The builder's result document was empty.");
        }

        TargetDeploymentResult candidate = new(
            TargetDeploymentState.Deployed,
            reported.BuildRunId,
            reported.ImageDigest,
            reported.RevisionName,
            reported.ApplicationUrl,
            [
                $"Built by {_options.Owner}/{_options.Repository} run {reported.BuildRunId}.",
                $"Workbench commit carried into the build: {reported.WorkbenchSha}.",
            ],
            null)
        {
            DeployedResourceId = reported.DeployedResourceId,
        };

        return TargetDeploymentPolicy.RejectResult(candidate, authority) is { Count: > 0 } rejections
            ? candidate with
            {
                State = TargetDeploymentState.VerificationFailed,
                FailureReason = "The builder reported a deployment this product could not verify.",
                Findings = rejections,
            }
            : candidate;
    }

    private async Task DispatchAsync(
        TargetDeploymentRequest request,
        string artifactUri,
        string artifactSha256,
        CancellationToken cancellationToken)
    {
        // The last check before anything leaves this process. Everything between the adapter's own
        // re-check and here is time spent uploading, and an approval that lapsed during the upload is an
        // approval no builder should be started under.
        if (TargetDeploymentPolicy.RejectAuthority(request.Binding.Authority, _time.GetUtcNow()) is { Count: > 0 } lapsed)
        {
            throw new InvalidOperationException(
                "The approval for this destination no longer holds, so no builder was dispatched: " +
                string.Join(" ", lapsed));
        }

        TargetDeploymentAuthority authority = request.Binding.Authority!;
        string token = await InstallationTokenAsync(cancellationToken).ConfigureAwait(false);

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

    /// <summary>Writes a blob. With <paramref name="overwrite"/> false a losing race returns false, not an error.</summary>
    private async Task<bool> PutBlobAsync(
        string name,
        byte[] content,
        string contentType,
        bool overwrite,
        CancellationToken cancellationToken)
    {
        using HttpRequestMessage message = new(HttpMethod.Put, BlobUri(name));
        await AuthorizeStorageAsync(message, cancellationToken).ConfigureAwait(false);
        message.Headers.Add("x-ms-blob-type", "BlockBlob");

        if (!overwrite)
        {
            message.Headers.IfNoneMatch.ParseAdd("*");
        }

        message.Content = new ByteArrayContent(content);
        message.Content.Headers.ContentType = new(contentType);

        using HttpResponseMessage response = await _http.SendAsync(message, cancellationToken).ConfigureAwait(false);

        if (!overwrite && response.StatusCode == System.Net.HttpStatusCode.Conflict)
        {
            return false;
        }

        if (!overwrite && response.StatusCode == System.Net.HttpStatusCode.PreconditionFailed)
        {
            return false;
        }

        response.EnsureSuccessStatusCode();
        return true;
    }

    private async Task<byte[]?> GetBlobAsync(string name, CancellationToken cancellationToken)
    {
        using HttpRequestMessage message = new(HttpMethod.Get, BlobUri(name));
        await AuthorizeStorageAsync(message, cancellationToken).ConfigureAwait(false);

        using HttpResponseMessage response = await _http.SendAsync(message, cancellationToken).ConfigureAwait(false);

        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
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
    /// What one replica recorded about one operation.
    ///
    /// <see cref="DispatchedUtc"/> is written only after GitHub accepted the dispatch. A claim without it
    /// is the crash window this record exists to make recoverable: the bytes are uploaded, the operation
    /// is claimed, and nothing was ever asked to build it.
    /// </summary>
    private sealed record DispatchClaim(
        string OperationId,
        string RunId,
        string ArtifactSha256,
        DateTimeOffset ClaimedUtc,
        int DispatchAttempts,
        DateTimeOffset? LastAttemptUtc,
        DateTimeOffset? DispatchedUtc);

    /// <summary>What the trusted builder writes. Read as a report and re-checked, never trusted as a verdict.</summary>
    private sealed record BuilderResult(
        string? SchemaVersion,
        string? State,
        string? OperationId,
        string? RunId,
        string? BuildRunId,
        string? ArtifactSha256,
        string? WorkbenchSha,
        string? PlanDigest,
        string? ImageDigest,
        string? RevisionName,
        string? ApplicationUrl,
        string? DeployedResourceId);
}
