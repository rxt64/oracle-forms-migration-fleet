// Copyright (c) Microsoft. All rights reserved.

using System.Globalization;
using System.Text;

namespace OracleFormsMigrationFleet.Fleet.Execution;

/// <summary>
/// What happened when the product tried to publish a generated application tier to its Azure target.
///
/// Every value except <see cref="Deployed"/> means nothing is serving. They are kept apart rather than
/// collapsed into one failure because an unconfigured host, a refused binding, and a build that ran and
/// failed are three different things to tell an operator, and only the last one is about the generated code.
/// </summary>
public enum TargetDeploymentState
{
    /// <summary>The phase did not reach the gateway. Nothing was uploaded and nothing was dispatched.</summary>
    NotAttempted,

    /// <summary>No deployment gateway is configured on this host, so the product cannot deploy anything.</summary>
    GatewayUnavailable,

    /// <summary>A deterministic rule refused the request before any external call was made.</summary>
    RefusedByPolicy,

    /// <summary>The server did not re-authorize publishing this output at the moment it would have happened.</summary>
    NotAuthorized,

    /// <summary>The trusted builder ran and the generated application did not build or its tests failed.</summary>
    BuildFailed,

    /// <summary>The image was published but the deployed revision did not answer its health check.</summary>
    VerificationFailed,

    /// <summary>The trusted builder did not reach a conclusion inside the operation's budget.</summary>
    TimedOut,

    /// <summary>A revision is running the exact image digest this operation produced.</summary>
    Deployed,
}

/// <summary>One file of the generated tier, with the digest the product read from the workspace itself.</summary>
public sealed record TargetDeploymentFile(string Path, string ContentSha256, long ByteLength);

/// <summary>
/// The exact bytes a deployment is about.
///
/// <see cref="OutputSetSha256"/> is <see cref="GenerationCoverage.OutputSetDigest"/> over <see cref="Files"/>,
/// so the same digest the generation phase recorded is the one a deployment is bound to. A file edited in
/// the workspace after generation changes it, and the binding check below refuses.
/// </summary>
public sealed record TargetDeploymentBundle(
    string WorkspaceRoot,
    string BundleRoot,
    IReadOnlyList<TargetDeploymentFile> Files,
    string OutputSetSha256);

/// <summary>
/// The exact Azure destination this run was accepted against, resolved by the server from the project's
/// immutable target profile and the approval that is effective for it.
///
/// It exists because a host that merely knows the name of a container app knows nothing about whether
/// <em>this run</em> was approved to write to it. Two runs on one host can belong to different projects
/// with different profiles, so a deployment authorized by host configuration alone would let a run publish
/// into a destination its own approval never named. Every field is a non-secret coordinate the server
/// owns; there is no endpoint, token, or registry credential here, and none of it is ever read out of the
/// workspace, a request body, or the coverage record on disk.
///
/// <see cref="ApprovalExpiresUtc"/> travels with the binding rather than being consulted once, because the
/// gap between authorizing a publish and a builder finishing one is measured in tens of minutes. It is
/// immutable: extending an approval produces a new one, which produces a different operation identity.
/// </summary>
public sealed record TargetDeploymentAuthority
{
    public required string TargetProfileId { get; init; }

    public required int TargetProfileVersion { get; init; }

    /// <summary>Canonical digest of the profile version. An edited profile is a different destination.</summary>
    public required string TargetProfileHash { get; init; }

    public required string AzureTenantId { get; init; }

    public required string SubscriptionId { get; init; }

    public required string ResourceGroup { get; init; }

    /// <summary>ARM resource identifier of the approved generated-application Container App.</summary>
    public required string TargetResourceId { get; init; }

    /// <summary>The identity the deployed application runs as. A name, never a credential.</summary>
    public required string ExecutionIdentity { get; init; }

    public required string EnvironmentName { get; init; }

    /// <summary>The approval that made this destination writable for this run.</summary>
    public required string ApprovalId { get; init; }

    public required DateTimeOffset ApprovalExpiresUtc { get; init; }

    /// <summary>Last path segment of <see cref="TargetResourceId"/>: the container app's own name.</summary>
    public string TargetResourceName =>
        TargetResourceId.AsSpan(TargetResourceId.LastIndexOf('/') + 1).ToString();
}

/// <summary>Exactly one side is set: a resolved destination, or every reason there is not one.</summary>
public sealed record TargetDeploymentAuthorityDecision(
    TargetDeploymentAuthority? Authority,
    IReadOnlyList<string> Denials)
{
    public static TargetDeploymentAuthorityDecision Granted(TargetDeploymentAuthority authority) => new(authority, []);

    public static TargetDeploymentAuthorityDecision Denied(params string[] denials) => new(null, denials);
}

/// <summary>
/// The server's authority over where this run may publish, consulted at the moment of publishing.
///
/// Like the generation and verification providers beside it, the host constructs this holding the run's
/// own tenant, project, and profile identity, so a phase supplies nothing and cannot ask about another
/// project's destination. A phase with no provider publishes nowhere: an unresolvable destination and an
/// approved one are not the same answer.
/// </summary>
public interface ITargetDeploymentAuthorityProvider
{
    Task<TargetDeploymentAuthorityDecision> ResolveAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Which run, which source, which recorded decisions, and which approved destination a deployment is
/// claimed to be of.
///
/// None of it is caller-supplied: the adapter reads the run's own request, the authorization the server
/// issued at the moment of publishing, and the destination the server resolved from the project's
/// immutable profile. It is carried to the trusted builder as dispatch inputs so the artifact, the run,
/// the decisions, and the destination are one traceable statement rather than four unrelated ones.
/// </summary>
public sealed record TargetDeploymentBinding(
    string RunId,
    string TenantId,
    string ProjectId,
    string LedgerId,
    string EngagementId,
    string ApplicationName,
    string SourceSnapshotHash,
    string IntermediateContentSha256,
    string MappingManifestSha256,
    string ScopeDigest)
{
    /// <summary>
    /// The resolved destination. Null is a refusal, never a weaker check: a binding without one names no
    /// approved resource, and a deployment would then be authorized by host configuration alone.
    /// </summary>
    public TargetDeploymentAuthority? Authority { get; init; }
}

/// <summary>Everything the host gateway is given. It holds no credential, endpoint, or registry name.</summary>
public sealed record TargetDeploymentRequest(
    TargetDeploymentBinding Binding,
    TargetDeploymentBundle Bundle,
    BackEndStack BackEnd,
    DatabaseTarget Database,
    string OperationId);

/// <summary>
/// What the trusted builder reported back.
///
/// <see cref="ImageDigest"/> is the immutable digest of the image a revision is running, never a tag. A
/// tag can be moved after it was verified; a digest cannot, which is the only reason a deployment claim
/// here is worth anything.
/// </summary>
public sealed record TargetDeploymentResult(
    TargetDeploymentState State,
    string? BuildRunId,
    string? ImageDigest,
    string? RevisionName,
    string? ApplicationUrl,
    IReadOnlyList<string> Findings,
    string? FailureReason)
{
    /// <summary>
    /// The ARM resource the builder says it updated. Compared against the run's own approved destination
    /// before anything is recorded as deployed, so a builder pointed at a different app by configuration
    /// drift reports a verification failure instead of a success under this run's provenance.
    /// </summary>
    public string? DeployedResourceId { get; init; }

    public bool Succeeded => State == TargetDeploymentState.Deployed;

    public static TargetDeploymentResult Refused(
        TargetDeploymentState state,
        string reason,
        IReadOnlyList<string>? findings = null) =>
        new(state, null, null, null, null, findings ?? [], reason);
}

/// <summary>
/// The host's ability to have a trusted builder build, publish, and deploy a generated application tier.
///
/// The product never builds a deployable image itself and never holds a registry or subscription
/// credential in the run's process: it hands an approved builder a bundle it has already digested, and
/// reads back the digest that builder published. A host with nothing configured registers no
/// implementation, and the phase refuses rather than pretending a deployment was skipped for a good reason.
/// </summary>
public interface ITargetApplicationDeploymentGateway
{
    /// <summary>Names the builder in operator-facing output. Never a URL, token, or subscription.</summary>
    string Description { get; }

    Task<TargetDeploymentResult> PublishAsync(TargetDeploymentRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// Every deterministic rule a deployment is held to. Pure: no file, process, clock, network, or random source.
///
/// It exists as its own type because these are the checks that have to hold identically in two places —
/// before the product dispatches anything, and again when it reads back what the builder claims to have
/// done. A rule written once in the adapter and re-implemented in the gateway is a rule that will drift.
/// </summary>
public static class TargetDeploymentPolicy
{
    /// <summary>Ceiling on the file count of one deployable bundle. A larger tree is refused, never truncated.</summary>
    public const int MaxBundleFiles = 5_000;

    /// <summary>Ceiling on one bundle's total size. Chosen to hold generated source, never a build output tree.</summary>
    public const long MaxBundleBytes = 64L * 1024 * 1024;

    /// <summary>Ceiling on one file inside a bundle.</summary>
    public const long MaxBundleFileBytes = 8L * 1024 * 1024;

    /// <summary>
    /// Hosts an artifact may be read from. Anything else is refused before a request is made, so a
    /// dispatch input cannot become a fetch of an attacker-chosen URL inside the trusted builder.
    /// </summary>
    public static IReadOnlyList<string> AllowedArtifactHostSuffixes { get; } =
        [".blob.core.windows.net"];

    /// <summary>Hosts a deployed application may legitimately answer on.</summary>
    public static IReadOnlyList<string> AllowedApplicationHostSuffixes { get; } =
        [".azurecontainerapps.io"];

    /// <summary>True for a 64-character lowercase hex content digest.</summary>
    public static bool IsContentDigest(string? value) =>
        value is { Length: 64 } && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    /// <summary>True for an OCI image digest: the literal 'sha256:' and 64 lowercase hex characters.</summary>
    public static bool IsImageDigest(string? value) =>
        value is { Length: 71 } &&
        value.StartsWith("sha256:", StringComparison.Ordinal) &&
        IsContentDigest(value[7..]);

    /// <summary>
    /// The deterministic identity of one publish attempt.
    ///
    /// The same run publishing the same bytes to the same approved destination under the same decisions
    /// and the same approval produces the same identifier, which is what lets a retry after a lease expiry
    /// find the operation already in flight instead of starting a second build of the same artifact. Any
    /// change to the bytes, the decisions, the destination, or the approval produces a different one — a
    /// re-approved run is a new operation rather than a resumption of an expired one.
    /// </summary>
    public static string OperationId(TargetDeploymentBinding binding, string outputSetSha256)
    {
        ArgumentNullException.ThrowIfNull(binding);

        StringBuilder canonical = new();
        canonical
            .Append(binding.RunId).Append('\u0000')
            .Append(binding.TenantId).Append('\u0000')
            .Append(binding.ProjectId).Append('\u0000')
            .Append(binding.LedgerId).Append('\u0000')
            .Append(binding.SourceSnapshotHash).Append('\u0000')
            .Append(binding.IntermediateContentSha256).Append('\u0000')
            .Append(binding.MappingManifestSha256).Append('\u0000')
            .Append(binding.ScopeDigest).Append('\u0000')
            .Append(TargetDigest(binding.Authority)).Append('\u0000')
            .Append(outputSetSha256 ?? string.Empty).Append('\n');

        return GenerationCoverage.Digest(canonical.ToString());
    }

    /// <summary>
    /// A single digest over the approved destination and the approval that made it writable.
    ///
    /// The builder is handed this alongside the resource identifier it is told to update, so a dispatch
    /// input rewritten in transit no longer matches the digest the product derived, and the workflow can
    /// refuse without having to be trusted to keep a set of separate fields together.
    /// </summary>
    public static string TargetDigest(TargetDeploymentAuthority? authority) =>
        authority is null
            ? GenerationCoverage.Digest("\u0000no-approved-destination\n")
            : GenerationCoverage.Digest(string.Join(
                '\u0000',
                authority.TargetProfileId,
                authority.TargetProfileVersion.ToString(CultureInfo.InvariantCulture),
                authority.TargetProfileHash,
                authority.AzureTenantId,
                authority.SubscriptionId,
                authority.ResourceGroup,
                authority.TargetResourceId,
                authority.ExecutionIdentity,
                authority.EnvironmentName,
                authority.ApprovalId,
                authority.ApprovalExpiresUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)) + "\n");

    /// <summary>
    /// A single digest over what the run planned and decided, carried as one dispatch input.
    ///
    /// The builder has no way to read a ledger, so it is given a value it can echo back into its own
    /// provenance rather than a set of fields it would have to be trusted to keep together.
    /// </summary>
    public static string PlanDigest(TargetDeploymentBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);

        return GenerationCoverage.Digest(
            $"{binding.IntermediateContentSha256}\u0000{binding.MappingManifestSha256}\u0000{binding.ScopeDigest}\n");
    }

    /// <summary>
    /// A digest over every dispatch input the builder is given, in a fixed order.
    ///
    /// The builder recomputes this from the inputs it received and refuses when it differs. That turns the
    /// set of inputs into one statement: an operator or an intermediary who rewrites the destination, the
    /// expiry, or the artifact location has to produce a matching digest, and cannot, because the digest
    /// is derived from the values the product bound and dispatched.
    /// </summary>
    public static string BindingDigest(
        TargetDeploymentBinding binding,
        string operationId,
        string artifactUri,
        string artifactSha256,
        string workbenchCommitSha)
    {
        ArgumentNullException.ThrowIfNull(binding);

        return GenerationCoverage.Digest(string.Join(
            '\u0000',
            "fleet.target-deployment-binding/1",
            operationId ?? string.Empty,
            artifactUri ?? string.Empty,
            artifactSha256 ?? string.Empty,
            binding.RunId,
            workbenchCommitSha ?? string.Empty,
            binding.SourceSnapshotHash,
            PlanDigest(binding),
            TargetDigest(binding.Authority),
            binding.Authority?.TargetResourceId ?? string.Empty,
            ExpiryText(binding.Authority)) + "\n");
    }

    /// <summary>The approval expiry as the product writes it everywhere: UTC, round-trip format.</summary>
    public static string ExpiryText(TargetDeploymentAuthority? authority) =>
        authority is null
            ? string.Empty
            : authority.ApprovalExpiresUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    /// <summary>
    /// Every reason this destination may not be published to at <paramref name="nowUtc"/>.
    ///
    /// The expiry is checked against a clock the caller supplies rather than a captured one, because the
    /// point of carrying it is that it is re-read: once before the bytes are uploaded, again immediately
    /// before the builder is dispatched, and again before a reported deployment is recorded.
    /// </summary>
    public static IReadOnlyList<string> RejectAuthority(TargetDeploymentAuthority? authority, DateTimeOffset nowUtc)
    {
        if (authority is null)
        {
            return
            [
                "This run resolved no approved deployment destination, so there is nowhere it may publish. Host " +
                "configuration naming a container app is not an approval: it says where a deployment could go, not that " +
                "this run's project was approved to send one there.",
            ];
        }

        List<string> rejections = [];

        if (string.IsNullOrWhiteSpace(authority.TargetProfileId))
        {
            rejections.Add(
                "The approved destination names no target profile identifier, so the immutable record it was resolved " +
                "from cannot be named and the destination cannot be traced back to an approval.");
        }

        // A directory identifier, not a credential. Without it the coordinates below name a subscription
        // and a resource without saying which tenant's they are, and two directories can hold identically
        // named resource groups.
        if (!Guid.TryParseExact(authority.AzureTenantId, "D", out _))
        {
            rejections.Add("The approved destination's Azure tenant identifier is not a GUID.");
        }

        if (!Guid.TryParseExact(authority.SubscriptionId, "D", out _))
        {
            rejections.Add("The approved destination's subscription identifier is not a GUID.");
        }

        if (string.IsNullOrWhiteSpace(authority.ResourceGroup))
        {
            rejections.Add("The approved destination names no resource group.");
        }

        if (string.IsNullOrWhiteSpace(authority.EnvironmentName))
        {
            rejections.Add(
                "The approved destination names no environment, so a publish could not be told apart from one into a " +
                "different environment of the same project.");
        }

        if (!IsContentDigest(authority.TargetProfileHash))
        {
            rejections.Add("The approved destination carries no canonical profile digest, so it cannot be pinned to a profile version.");
        }

        if (authority.TargetProfileVersion < 1)
        {
            rejections.Add("The approved destination names no target profile version.");
        }

        if (string.IsNullOrWhiteSpace(authority.ApprovalId))
        {
            rejections.Add("No approval identifier was resolved for this destination.");
        }

        if (string.IsNullOrWhiteSpace(authority.ExecutionIdentity))
        {
            rejections.Add("The approved destination names no execution identity.");
        }

        if (RejectTargetResourceId(authority) is string badResource)
        {
            rejections.Add(badResource);
        }

        if (authority.ApprovalExpiresUtc <= nowUtc)
        {
            rejections.Add(
                $"The approval for this destination expired at {ExpiryText(authority)}. Nothing was published. An approval " +
                "is not extended by a build taking longer than expected: a fresh approval produces a new operation rather " +
                "than resuming this one.");
        }

        return rejections;
    }

    /// <summary>
    /// Whether the destination is a single, fully qualified Container App inside the approved subscription
    /// and resource group.
    ///
    /// A resource group, a subscription, or a managed environment would all be legitimate-looking ARM
    /// identifiers and would all widen the deployment far past one application, so the shape is asserted
    /// exactly rather than merely prefixed-matched.
    /// </summary>
    private static string? RejectTargetResourceId(TargetDeploymentAuthority authority)
    {
        string id = authority.TargetResourceId ?? string.Empty;

        if (!id.StartsWith('/'))
        {
            return "The approved destination is not an absolute ARM resource identifier.";
        }

        string[] segments = id.Split('/', StringSplitOptions.None);

        // '', subscriptions, <sub>, resourceGroups, <rg>, providers, Microsoft.App, containerApps, <name>
        if (segments.Length != 9 ||
            !string.Equals(segments[1], "subscriptions", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(segments[3], "resourceGroups", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(segments[5], "providers", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(segments[6], "Microsoft.App", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(segments[7], "containerApps", StringComparison.OrdinalIgnoreCase) ||
            segments[8].Length == 0)
        {
            return "The approved destination is not a single Azure Container App resource identifier. A subscription, " +
                "resource group, or environment identifier would authorize far more than one application and is refused.";
        }

        if (!string.Equals(segments[2], authority.SubscriptionId, StringComparison.OrdinalIgnoreCase))
        {
            return "The approved destination's resource identifier names a different subscription than the profile does.";
        }

        if (!string.Equals(segments[4], authority.ResourceGroup, StringComparison.OrdinalIgnoreCase))
        {
            return "The approved destination's resource identifier names a different resource group than the profile does.";
        }

        return null;
    }

    /// <summary>Every reason this bundle may not be published. Empty means the bytes are publishable.</summary>
    public static IReadOnlyList<string> RejectBundle(TargetDeploymentBundle? bundle)
    {
        List<string> rejections = [];

        if (bundle is null)
        {
            return ["No bundle was supplied, so there is nothing to publish."];
        }

        if (bundle.Files.Count == 0)
        {
            rejections.Add("The generated application tier holds no files, so there is nothing to publish.");
        }

        if (bundle.Files.Count > MaxBundleFiles)
        {
            rejections.Add(
                $"The generated application tier holds {bundle.Files.Count.ToString(CultureInfo.InvariantCulture)} files, " +
                $"above the {MaxBundleFiles.ToString(CultureInfo.InvariantCulture)} this product will package. Nothing was " +
                "published rather than publishing part of it.");
        }

        long total = 0;
        foreach (TargetDeploymentFile file in bundle.Files)
        {
            if (WorkspacePath.Validate(file.Path, "bundle path") is string invalid)
            {
                rejections.Add($"'{file.Path}' is not a workspace-relative path and was refused: {invalid}");
                continue;
            }

            if (!IsContentDigest(file.ContentSha256))
            {
                rejections.Add($"'{file.Path}' carries no readable content digest, so the bundle cannot be bound to its bytes.");
            }

            if (file.ByteLength < 0 || file.ByteLength > MaxBundleFileBytes)
            {
                rejections.Add(
                    $"'{file.Path}' is {file.ByteLength.ToString(CultureInfo.InvariantCulture)} bytes, outside the per-file " +
                    $"ceiling of {MaxBundleFileBytes.ToString(CultureInfo.InvariantCulture)}.");
            }

            total += Math.Max(file.ByteLength, 0);
        }

        if (total > MaxBundleBytes)
        {
            rejections.Add(
                $"The generated application tier is {total.ToString(CultureInfo.InvariantCulture)} bytes, above the " +
                $"{MaxBundleBytes.ToString(CultureInfo.InvariantCulture)} this product will package.");
        }

        if (!IsContentDigest(bundle.OutputSetSha256))
        {
            rejections.Add("The bundle carries no output-set digest, so nothing could be bound to the generation that produced it.");
        }
        else if (bundle.Files.Count > 0 &&
                 !string.Equals(
                     bundle.OutputSetSha256,
                     GenerationCoverage.OutputSetDigest(bundle.Files.Select(file => (file.Path, file.ContentSha256))),
                     StringComparison.Ordinal))
        {
            rejections.Add(
                "The bundle's output-set digest does not match the files it lists. The digest is recomputed here rather " +
                "than trusted, so a file added, removed, or edited after generation refuses the deployment.");
        }

        return rejections;
    }

    /// <summary>
    /// Every reason this binding may not be published under, including whether its approved destination is
    /// still approved at <paramref name="nowUtc"/>.
    ///
    /// The clock is a required argument rather than a default, because the one check most likely to be
    /// skipped by accident is the one that matters most after a long wait.
    /// </summary>
    public static IReadOnlyList<string> RejectBinding(TargetDeploymentBinding? binding, DateTimeOffset nowUtc)
    {
        if (binding is null)
        {
            return ["No run binding was supplied, so a deployment could not be attributed to any run."];
        }

        List<string> rejections = [];

        void Require(string value, string name, bool digest)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                rejections.Add($"{name} is empty, so the deployment could not be bound to it.");
            }
            else if (digest && !IsContentDigest(value))
            {
                rejections.Add($"{name} is not a 64-character lowercase hex digest.");
            }
        }

        Require(binding.RunId, "The run identifier", digest: false);
        Require(binding.TenantId, "The tenant identifier", digest: false);
        Require(binding.ProjectId, "The project identifier", digest: false);

        // The deployment claims to publish a tier whose decisions were recorded. Without the ledger the
        // scope digest below names decisions nobody can look up afterwards.
        Require(binding.LedgerId, "The ledger identifier", digest: false);
        Require(binding.EngagementId, "The engagement identifier", digest: false);
        Require(binding.ApplicationName, "The application name", digest: false);
        Require(binding.SourceSnapshotHash, "The source snapshot digest", digest: true);
        Require(binding.IntermediateContentSha256, "The normalized source digest", digest: true);
        Require(binding.MappingManifestSha256, "The target-mapping manifest digest", digest: true);
        Require(binding.ScopeDigest, "The recorded-decision digest", digest: true);

        if (FleetGuardrails.ContainsPotentialSecret(binding.EngagementId) ||
            FleetGuardrails.ContainsPotentialSecret(binding.ApplicationName) ||
            FleetGuardrails.ContainsPotentialSecret(binding.LedgerId))
        {
            rejections.Add("The run binding appears to contain credential material and was refused.");
        }

        rejections.AddRange(RejectAuthority(binding.Authority, nowUtc));

        return rejections;
    }

    /// <summary>
    /// Whether an artifact location may be handed to the trusted builder.
    ///
    /// The builder fetches whatever it is told to fetch, inside a job that has the repository checked out,
    /// so an unrestricted location is a server-side request forgery with a build agent behind it. Only
    /// HTTPS, only an allowlisted host, and no query string: a location carrying a shared-access signature
    /// would put a credential into a workflow input and into every log that echoes one.
    /// </summary>
    public static string? RejectArtifactUri(string? candidate, IReadOnlyList<string>? allowedHostSuffixes = null)
    {
        IReadOnlyList<string> allowed = allowedHostSuffixes ?? AllowedArtifactHostSuffixes;

        if (string.IsNullOrWhiteSpace(candidate))
        {
            return "No artifact location was supplied.";
        }

        if (!Uri.TryCreate(candidate, UriKind.Absolute, out Uri? uri))
        {
            return "The artifact location is not an absolute URI.";
        }

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal))
        {
            return "The artifact location must use https.";
        }

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            return "The artifact location carries user information and was refused.";
        }

        if (!uri.IsDefaultPort)
        {
            return "The artifact location must use the default https port.";
        }

        if (uri.Query.Length > 0)
        {
            return "The artifact location carries a query string. A shared-access signature must never travel as a build input.";
        }

        if (uri.Fragment.Length > 0)
        {
            return "The artifact location carries a fragment and was refused.";
        }

        string host = uri.IdnHost;
        if (!allowed.Any(suffix => host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)))
        {
            return $"The artifact host '{host}' is not one this product is allowed to hand to a builder.";
        }

        // Compared against the text as supplied, because Uri collapses '..' during parsing: a location
        // written as '/container/../../secret' arrives here already normalized to '/secret', and checking
        // only the parsed path would accept a traversal the builder is then handed verbatim.
        string raw = candidate.AsSpan(Uri.UriSchemeHttps.Length + 3).ToString();
        int separator = raw.IndexOf('/', StringComparison.Ordinal);
        string rawPath = separator < 0 ? "/" : raw[separator..];

        if (!string.Equals(rawPath, uri.AbsolutePath, StringComparison.Ordinal))
        {
            return "The artifact path is not a single well-formed blob path: it is not already canonical.";
        }

        if (rawPath.Contains("..", StringComparison.Ordinal) ||
            rawPath.Contains("//", StringComparison.Ordinal) ||
            rawPath.Contains('%', StringComparison.Ordinal) ||
            rawPath.Count(character => character == '/') < 2 ||
            rawPath.EndsWith('/') ||
            rawPath.Length <= 1)
        {
            return "The artifact path is not a single well-formed blob path.";
        }

        return null;
    }

    /// <summary>
    /// Every reason a builder's report may not be recorded as a deployment.
    ///
    /// This runs after the builder answers, and it is the reason a successful workflow run is not by itself
    /// a deployment: a run that concluded without an immutable digest, that reports a URL on a host this
    /// product does not deploy to, or that updated a resource other than the one this run was approved
    /// for, has not shown that anything of this run's is serving where it was allowed to serve.
    /// </summary>
    public static IReadOnlyList<string> RejectResult(
        TargetDeploymentResult? result,
        TargetDeploymentAuthority? authority = null,
        IReadOnlyList<string>? allowedApplicationHostSuffixes = null)
    {
        if (result is null)
        {
            return ["The deployment gateway returned nothing."];
        }

        if (result.State != TargetDeploymentState.Deployed)
        {
            return [];
        }

        List<string> rejections = [];
        IReadOnlyList<string> allowed = allowedApplicationHostSuffixes ?? AllowedApplicationHostSuffixes;

        if (!IsImageDigest(result.ImageDigest))
        {
            rejections.Add(
                "The builder reported a deployment without an immutable image digest. A tag can be moved after it was " +
                "verified, so a deployment is only claimed against a digest.");
        }

        if (string.IsNullOrWhiteSpace(result.RevisionName))
        {
            rejections.Add("The builder reported a deployment without naming the revision that is running the image.");
        }

        if (string.IsNullOrWhiteSpace(result.BuildRunId))
        {
            rejections.Add("The builder reported a deployment without naming the build that produced it.");
        }

        if (!Uri.TryCreate(result.ApplicationUrl, UriKind.Absolute, out Uri? url) ||
            !string.Equals(url.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal) ||
            !allowed.Any(suffix => url.IdnHost.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)))
        {
            rejections.Add("The builder reported a deployment without an https URL on a host this product deploys to.");
        }

        if (authority is not null)
        {
            if (string.IsNullOrWhiteSpace(result.DeployedResourceId))
            {
                rejections.Add(
                    "The builder reported a deployment without naming the Azure resource it updated, so nothing shows it " +
                    "updated the application this run was approved for.");
            }
            else if (!string.Equals(result.DeployedResourceId, authority.TargetResourceId, StringComparison.OrdinalIgnoreCase))
            {
                rejections.Add(
                    "The builder updated a different Azure resource than the one this run's approved target profile names. " +
                    "It is recorded as unverified: a deployment that landed somewhere else is not this run's deployment.");
            }
        }

        return rejections;
    }
}
