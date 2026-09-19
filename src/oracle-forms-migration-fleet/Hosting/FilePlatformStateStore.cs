// Copyright (c) Microsoft. All rights reserved.

using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OracleFormsMigrationFleet.Hosting;

/// <summary>
/// Durable platform state in one JSON file, for explicit Development mode.
///
/// This exists so the approval state machine, restart behaviour, and optimistic concurrency are
/// testable without cloud credentials. It is not a smaller production store: every operation reads the
/// whole document, applies the change, and replaces the file atomically, which is correct for a single
/// developer and wrong for a fleet of replicas. Production uses PostgreSQL.
///
/// Writes are serialized by a lock held per absolute file path, so two store instances pointed at the
/// same file — a restart, a second injected instance, a test recreating the adapter — do not interleave.
/// The replacement itself is a <see cref="File.Move(string, string, bool)"/> of a fully written
/// temporary file, so a crash mid-write leaves the previous document intact rather than a truncated one.
/// </summary>
public sealed class FilePlatformStateStore : IPlatformStateStore, ISandboxProjectBindingStore
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> s_locks = new(StringComparer.OrdinalIgnoreCase);

    private static readonly JsonSerializerOptions s_json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _path;
    private readonly SemaphoreSlim _gate;

    public FilePlatformStateStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        _path = System.IO.Path.GetFullPath(path);
        _gate = s_locks.GetOrAdd(_path, _ => new SemaphoreSlim(1, 1));
    }

    public string Description => "Durable development file store";

    /// <summary>Where the state lives. Exposed so a test can assert isolation, never returned to a browser.</summary>
    public string StatePath => _path;

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path)!);
            Document document = Read();
            PlatformSchema.ValidateAppliedVersions([document.SchemaVersion]);
            if (document.SchemaVersion < 2)
            {
                foreach (IGrouping<string, PlatformApproval> tenant in document.Approvals
                    .Where(approval => approval.Scope == WorkbenchMutationScope.SandboxDatabaseWrite)
                    .GroupBy(approval => approval.TenantId, StringComparer.OrdinalIgnoreCase))
                {
                    PlatformApproval binding = tenant
                        .OrderBy(approval => approval.RequestedUtc)
                        .ThenBy(approval => approval.ApprovalId, StringComparer.Ordinal)
                        .First();
                    document.SandboxProjectBindings.TryAdd(binding.TenantId, binding.ProjectId);
                }
            }
            document.SchemaVersion = PlatformSchema.CurrentVersion;
            await WriteAsync(document, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task<PlatformOrganization> EnsureOrganizationAsync(string tenantId, string displayName, CancellationToken cancellationToken) =>
        MutateAsync(document =>
        {
            PlatformOrganization? existing = document.Organizations
                .FirstOrDefault(organization => string.Equals(organization.TenantId, tenantId, StringComparison.OrdinalIgnoreCase));

            if (existing is not null)
            {
                return existing;
            }

            PlatformOrganization created = new()
            {
                OrganizationId = Identifier("org"),
                TenantId = tenantId,
                DisplayName = displayName,
                CreatedUtc = DateTimeOffset.UtcNow,
            };

            document.Organizations.Add(created);
            return created;
        }, cancellationToken);

    public Task<PlatformProject> CreateProjectAsync(PlatformProject project, PlatformMembership founder, CancellationToken cancellationToken) =>
        MutateAsync(document =>
        {
            document.Projects.Add(project);
            document.Memberships.Add(founder);
            return project;
        }, cancellationToken);

    public Task<PlatformProject?> GetProjectAsync(string tenantId, string projectId, CancellationToken cancellationToken) =>
        ReadAsync(document => document.Projects.FirstOrDefault(project =>
            string.Equals(project.TenantId, tenantId, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(project.ProjectId, projectId, StringComparison.Ordinal)), cancellationToken);

    public Task<IReadOnlyList<PlatformProject>> ProjectsForActorAsync(string tenantId, string objectId, CancellationToken cancellationToken) =>
        ReadAsync(document =>
        {
            HashSet<string> projects =
            [.. document.Memberships
                .Where(membership =>
                    membership.IsActive &&
                    string.Equals(membership.TenantId, tenantId, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(membership.ObjectId, objectId, StringComparison.OrdinalIgnoreCase))
                .Select(membership => membership.ProjectId)];

            return (IReadOnlyList<PlatformProject>)
            [.. document.Projects
                .Where(project =>
                    string.Equals(project.TenantId, tenantId, StringComparison.OrdinalIgnoreCase) &&
                    projects.Contains(project.ProjectId))
                .OrderBy(project => project.CreatedUtc)];
        }, cancellationToken);

    public Task<PlatformMembership?> GetMembershipAsync(string tenantId, string projectId, string objectId, CancellationToken cancellationToken) =>
        ReadAsync(document => Find(document, tenantId, projectId, objectId), cancellationToken);

    public Task<IReadOnlyList<PlatformMembership>> MembershipsAsync(string tenantId, string projectId, CancellationToken cancellationToken) =>
        ReadAsync(document => (IReadOnlyList<PlatformMembership>)
        [.. document.Memberships.Where(membership =>
            string.Equals(membership.TenantId, tenantId, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(membership.ProjectId, projectId, StringComparison.Ordinal))], cancellationToken);

    public Task<PlatformMembership?> UpsertMembershipAsync(PlatformMembership membership, int? expectedVersion, CancellationToken cancellationToken) =>
        MutateAsync<PlatformMembership?>(document =>
        {
            PlatformMembership? existing = Find(document, membership.TenantId, membership.ProjectId, membership.ObjectId);
            if (existing is null)
            {
                document.Memberships.Add(membership);
                return membership;
            }

            if (expectedVersion is null)
            {
                return null;
            }

            if (expectedVersion is int expected && existing.Version != expected)
            {
                return null;
            }

            PlatformMembership updated = membership with { Version = existing.Version + 1, CreatedUtc = existing.CreatedUtc };
            document.Memberships.Remove(existing);
            document.Memberships.Add(updated);
            return updated;
        }, cancellationToken);

    public Task<PlatformTargetProfile?> CreateTargetProfileAsync(PlatformTargetProfile profile, CancellationToken cancellationToken) =>
        MutateAsync<PlatformTargetProfile?>(document =>
        {
            bool duplicate = document.TargetProfiles.Any(stored =>
                string.Equals(stored.TenantId, profile.TenantId, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(stored.ProjectId, profile.ProjectId, StringComparison.Ordinal) &&
                string.Equals(stored.TargetProfileId, profile.TargetProfileId, StringComparison.Ordinal) &&
                stored.Version == profile.Version);

            // A version that already exists is never rewritten: immutability is the property a grant
            // binds to, so silently replacing one would invalidate every decision made against it.
            if (duplicate)
            {
                return null;
            }

            document.TargetProfiles.Add(profile);
            return profile;
        }, cancellationToken);

    public Task<PlatformTargetProfile?> GetTargetProfileAsync(
        string tenantId, string projectId, string targetProfileId, int? version, CancellationToken cancellationToken) =>
        ReadAsync(document => document.TargetProfiles
            .Where(profile =>
                string.Equals(profile.TenantId, tenantId, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(profile.ProjectId, projectId, StringComparison.Ordinal) &&
                string.Equals(profile.TargetProfileId, targetProfileId, StringComparison.Ordinal) &&
                (version is null || profile.Version == version))
            .OrderByDescending(profile => profile.Version)
            .FirstOrDefault(), cancellationToken);

    public Task<IReadOnlyList<PlatformTargetProfile>> TargetProfilesAsync(string tenantId, string projectId, CancellationToken cancellationToken) =>
        ReadAsync(document => (IReadOnlyList<PlatformTargetProfile>)
        [.. document.TargetProfiles
            .Where(profile =>
                string.Equals(profile.TenantId, tenantId, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(profile.ProjectId, projectId, StringComparison.Ordinal))
            .GroupBy(profile => profile.TargetProfileId, StringComparer.Ordinal)
            .Select(group => group.OrderByDescending(profile => profile.Version).First())
            .OrderBy(profile => profile.CreatedUtc)], cancellationToken);

    public Task<string> BindSandboxProjectAsync(
        string tenantId, string projectId, CancellationToken cancellationToken) =>
        MutateAsync(document =>
        {
            if (document.SandboxProjectBindings.TryGetValue(tenantId, out string? bound))
            {
                return bound;
            }

            document.SandboxProjectBindings[tenantId] = projectId;
            return projectId;
        }, cancellationToken);

    public Task<string?> GetSandboxProjectAsync(string tenantId, CancellationToken cancellationToken) =>
        ReadAsync(document => document.SandboxProjectBindings.GetValueOrDefault(tenantId), cancellationToken);

    public Task<PlatformApproval> CreateApprovalAsync(PlatformApproval approval, CancellationToken cancellationToken) =>
        MutateAsync(document =>
        {
            document.Approvals.Add(approval);
            return approval;
        }, cancellationToken);

    public Task<PlatformApproval?> GetApprovalAsync(string tenantId, string approvalId, CancellationToken cancellationToken) =>
        ReadAsync(document => document.Approvals.FirstOrDefault(approval =>
            string.Equals(approval.TenantId, tenantId, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(approval.ApprovalId, approvalId, StringComparison.Ordinal)), cancellationToken);

    public Task<IReadOnlyList<PlatformApproval>> ApprovalsForProjectAsync(string tenantId, string projectId, CancellationToken cancellationToken) =>
        ReadAsync(document => (IReadOnlyList<PlatformApproval>)
        [.. document.Approvals
            .Where(approval =>
                string.Equals(approval.TenantId, tenantId, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(approval.ProjectId, projectId, StringComparison.Ordinal))
            .OrderByDescending(approval => approval.RequestedUtc)], cancellationToken);

    public Task<IReadOnlyList<PlatformApproval>> ApprovalsForRequesterAsync(string tenantId, string objectId, CancellationToken cancellationToken) =>
        ReadAsync(document => (IReadOnlyList<PlatformApproval>)
        [.. document.Approvals
            .Where(approval =>
                string.Equals(approval.TenantId, tenantId, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(approval.RequestedByObjectId, objectId, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(approval => approval.RequestedUtc)], cancellationToken);

    public Task<PlatformApproval?> UpdateApprovalAsync(PlatformApproval approval, int expectedVersion, CancellationToken cancellationToken) =>
        MutateAsync<PlatformApproval?>(document =>
        {
            PlatformApproval? existing = document.Approvals.FirstOrDefault(stored =>
                string.Equals(stored.TenantId, approval.TenantId, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(stored.ApprovalId, approval.ApprovalId, StringComparison.Ordinal));

            if (existing is null || existing.Version != expectedVersion)
            {
                return null;
            }

            PlatformApproval updated = approval with { Version = existing.Version + 1 };
            document.Approvals.Remove(existing);
            document.Approvals.Add(updated);
            return updated;
        }, cancellationToken);

    private static PlatformMembership? Find(Document document, string tenantId, string projectId, string objectId) =>
        document.Memberships.FirstOrDefault(membership =>
            string.Equals(membership.TenantId, tenantId, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(membership.ProjectId, projectId, StringComparison.Ordinal) &&
            string.Equals(membership.ObjectId, objectId, StringComparison.OrdinalIgnoreCase));

    private static string Identifier(string prefix) => $"{prefix}-{Guid.NewGuid():N}";

    private async Task<T> ReadAsync<T>(Func<Document, T> projection, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return projection(Read());
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<T> MutateAsync<T>(Func<Document, T> mutation, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Document document = Read();
            T result = mutation(document);

            // A rejected transition wrote nothing, so the file is left exactly as it was.
            if (result is null)
            {
                return result;
            }

            await WriteAsync(document, cancellationToken).ConfigureAwait(false);
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    private Document Read()
    {
        if (!File.Exists(_path))
        {
            return new Document();
        }

        try
        {
            return JsonSerializer.Deserialize<Document>(File.ReadAllText(_path), s_json) ?? new Document();
        }
        catch (JsonException)
        {
            // Refusing is the safe answer: an unreadable store must not present as an empty one, because
            // an empty store authorizes nothing but also silently discards every recorded decision.
            throw new InvalidOperationException(
                $"The development platform state file at '{_path}' could not be read. It was not replaced.");
        }
    }

    private async Task WriteAsync(Document document, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path)!);
        string temporary = $"{_path}.{Guid.NewGuid():N}.tmp";

        try
        {
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(document, s_json), cancellationToken)
                .ConfigureAwait(false);

            File.Move(temporary, _path, overwrite: true);
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    private sealed class Document
    {
        public int SchemaVersion { get; set; }

        public List<PlatformOrganization> Organizations { get; set; } = [];

        public List<PlatformProject> Projects { get; set; } = [];

        public List<PlatformMembership> Memberships { get; set; } = [];

        public List<PlatformTargetProfile> TargetProfiles { get; set; } = [];

        public Dictionary<string, string> SandboxProjectBindings { get; set; } =
            new(StringComparer.OrdinalIgnoreCase);

        public List<PlatformApproval> Approvals { get; set; } = [];
    }
}
