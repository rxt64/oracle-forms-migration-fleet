// Copyright (c) Microsoft. All rights reserved.

using OracleFormsMigrationFleet.Fleet;
using OracleFormsMigrationFleet.Fleet.Execution;

namespace OracleFormsMigrationFleet.Hosting;

public sealed class WorkspaceSourceSnapshotStore(SourceWorkspaceService workspaces) : ISourceSnapshotStore
{
    public Task<SourceSnapshotFacts?> ResolveAsync(
        string owner,
        string workspaceId,
        string sourceRoot,
        SourceEnvironmentProfileBinding profile,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SourceWorkspaceFacts? facts = workspaces.Describe(owner, workspaceId, sourceRoot);
        if (facts is null)
        {
            return Task.FromResult<SourceSnapshotFacts?>(null);
        }
        SourceSnapshotFacts snapshot = new(
            workspaceId,
            profile.SourceEnvironmentId,
            profile.Version,
            profile.CanonicalHash,
            facts.SnapshotHash,
            facts.Summary.AcquiredUtc,
            [.. facts.Summary.Artifacts.Select(artifact => new ArtifactReference(
                facts.Summary.SourceRoot,
                ArtifactKind.SourceInput,
                $"{artifact.Count} {artifact.Kind} source artifact(s); example {artifact.Example}."))]);
        return Task.FromResult<SourceSnapshotFacts?>(snapshot);
    }
}