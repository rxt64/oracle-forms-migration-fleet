// Copyright (c) Microsoft. All rights reserved.

using OracleFormsMigrationFleet.Fleet.Execution;

namespace OracleFormsMigrationFleet.Hosting;

/// <summary>
/// Resolves where one run may publish its generated application tier, from the project's immutable target
/// profile and the approval that is effective for it at the moment it is asked.
///
/// Everything identifying the destination comes from the server. The profile supplies the Azure tenant,
/// subscription, resource group, execution identity, environment and canonical hash; the approval supplies
/// the identifier and the expiry. The host supplies exactly one thing — the name of the Container App this
/// deployment builds into — and that alone is not authority: a run whose profile lives in a different
/// subscription or resource group resolves a destination this host's builder refuses, and the builder
/// re-derives the same identifier from its own configuration and compares.
///
/// Nothing here is read from the workspace, the request body, or the coverage record the generation phase
/// wrote. A destination an untrusted document can name is a destination a caller can redirect.
/// </summary>
public sealed class PlatformTargetDeploymentAuthorityProvider(
    IPlatformStateStore store,
    MigrationRunRecord run,
    string applicationResourceName,
    TimeProvider? time = null) : ITargetDeploymentAuthorityProvider
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public async Task<TargetDeploymentAuthorityDecision> ResolveAsync(CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(run);

        if (string.IsNullOrWhiteSpace(applicationResourceName))
        {
            return TargetDeploymentAuthorityDecision.Denied(
                "This host names no generated-application Container App, so it cannot resolve a destination for this run. " +
                "Nothing was deployed.");
        }

        if (string.IsNullOrWhiteSpace(run.TargetProfileId))
        {
            return TargetDeploymentAuthorityDecision.Denied(
                "This run names no target profile, so there is no immutable record of the destination it was accepted " +
                "against. Nothing was deployed.");
        }

        PlatformTargetProfile? profile = await store
            .GetTargetProfileAsync(run.TenantId, run.ProjectId, run.TargetProfileId, run.TargetProfileVersion, cancellationToken)
            .ConfigureAwait(false);

        if (profile is null)
        {
            return TargetDeploymentAuthorityDecision.Denied(
                $"Target profile version {run.TargetProfileVersion} is no longer readable, so the destination this run was " +
                "accepted against cannot be confirmed. Nothing was deployed.");
        }

        // Compared rather than adopted. A profile that reads back with a different canonical hash is a
        // different destination wearing the same identifier, and a deployment against it would carry this
        // run's provenance to somewhere the run was never accepted for.
        if (!string.Equals(profile.CanonicalHash, run.TargetProfileHash, StringComparison.Ordinal))
        {
            return TargetDeploymentAuthorityDecision.Denied(
                "The target profile this run was accepted against no longer hashes to what the run recorded, so the " +
                "destination changed after the run was queued. Nothing was deployed.");
        }

        IReadOnlyList<PlatformApproval> approvals = await store
            .ApprovalsForProjectAsync(run.TenantId, run.ProjectId, cancellationToken)
            .ConfigureAwait(false);

        DateTimeOffset now = _time.GetUtcNow();

        // The approval has to be the one this exact run was accepted under: same source snapshot, same
        // plan input, same profile version and hash, and a scope that authorizes a side effect at all.
        // An approval matching only the project would let a deployment ride on an approval an operator
        // granted for different bytes.
        PlatformApproval? effective = approvals
            .Where(approval =>
                approval.IsEffective(now) &&
                approval.Scope == WorkbenchMutationScope.SandboxDatabaseWrite &&
                string.Equals(approval.TargetProfileId, run.TargetProfileId, StringComparison.Ordinal) &&
                approval.TargetProfileVersion == run.TargetProfileVersion &&
                string.Equals(approval.TargetProfileHash, run.TargetProfileHash, StringComparison.Ordinal) &&
                string.Equals(approval.SourceSnapshotHash, run.SourceSnapshotHash, StringComparison.Ordinal) &&
                string.Equals(approval.PlanInputHash, run.PlanInputHash, StringComparison.Ordinal))
            .OrderByDescending(approval => approval.DecidedUtc ?? approval.RequestedUtc)
            .ThenBy(approval => approval.ApprovalId, StringComparer.Ordinal)
            .FirstOrDefault();

        if (effective is null)
        {
            return TargetDeploymentAuthorityDecision.Denied(
                "No effective approval covers publishing this run's generated application to its target profile at this " +
                "moment. An approval that was revoked, that expired, or that was granted over different source or plan " +
                "bytes does not authorize a deployment, so nothing was deployed.");
        }

        return TargetDeploymentAuthorityDecision.Granted(new TargetDeploymentAuthority
        {
            TargetProfileId = profile.TargetProfileId,
            TargetProfileVersion = profile.Version,
            TargetProfileHash = profile.CanonicalHash,
            AzureTenantId = profile.AzureTenantId,
            SubscriptionId = profile.SubscriptionId,
            ResourceGroup = profile.ResourceGroup,
            TargetResourceId = ApplicationResourceId(profile, applicationResourceName),
            ExecutionIdentity = profile.ExecutionIdentity,
            EnvironmentName = profile.EnvironmentName,
            ApprovalId = effective.ApprovalId,
            ApprovalExpiresUtc = effective.ExpiresUtc,
        });
    }

    /// <summary>
    /// The generated application's own ARM identifier, built from the profile's Azure coordinates.
    ///
    /// The profile's <see cref="PlatformTargetProfile.ResourceId"/> names the database this migration
    /// lands in, which is a different resource from the application that serves it. It is deliberately not
    /// reused here: deploying an application image to a database's identifier would be a category error,
    /// and widening the profile to carry both would let one approved coordinate stand in for the other.
    /// </summary>
    private static string ApplicationResourceId(PlatformTargetProfile profile, string applicationResourceName) =>
        $"/subscriptions/{profile.SubscriptionId}/resourceGroups/{profile.ResourceGroup}" +
        $"/providers/Microsoft.App/containerApps/{applicationResourceName}";
}
