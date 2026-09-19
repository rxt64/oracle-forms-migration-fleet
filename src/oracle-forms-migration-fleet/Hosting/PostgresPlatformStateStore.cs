// Copyright (c) Microsoft. All rights reserved.

using Azure.Core;
using Npgsql;
using NpgsqlTypes;

namespace OracleFormsMigrationFleet.Hosting;

/// <summary>
/// Non-secret coordinates of the platform database. There is no password here and no place to put one:
/// the connection authenticates with a managed identity token acquired at connection time.
/// </summary>
public sealed record PlatformDatabaseOptions
{
    public const string HostVariable = "PLATFORM_PGHOST";
    public const string DatabaseVariable = "PLATFORM_PGDATABASE";
    public const string UserVariable = "PLATFORM_PGUSER";
    public const string SchemaVariable = "PLATFORM_PGSCHEMA";

    public required string Host { get; init; }

    public required string Database { get; init; }

    public required string User { get; init; }

    public required string Schema { get; init; }

    /// <summary>Reads the configuration, or reports exactly which variable is missing.</summary>
    public static bool TryRead(
        WorkbenchConfigurationLookup configuration,
        out PlatformDatabaseOptions? options,
        out string error)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        options = null;
        string host = configuration(HostVariable)?.Trim() ?? string.Empty;
        string database = configuration(DatabaseVariable)?.Trim() ?? string.Empty;
        string user = configuration(UserVariable)?.Trim() ?? string.Empty;
        string schema = configuration(SchemaVariable)?.Trim() ?? PlatformSchema.DefaultSchema;

        if (host.Length == 0 || database.Length == 0 || user.Length == 0)
        {
            error =
                $"{HostVariable}, {DatabaseVariable}, and {UserVariable} must all be set for the platform state store.";
            return false;
        }

        if (!PlatformSchema.IsValidSchemaName(schema))
        {
            error = $"{SchemaVariable} must be a lowercase identifier such as '{PlatformSchema.DefaultSchema}'.";
            return false;
        }

        options = new PlatformDatabaseOptions { Host = host, Database = database, User = user, Schema = schema };
        error = string.Empty;
        return true;
    }
}

/// <summary>
/// Platform state in Azure Database for PostgreSQL, reached with a managed-identity token.
///
/// The schema is isolated from the migration target schemas so a customer migration can never write
/// over the records that authorize it. No credential is stored in a record or written to a log: the
/// only secret in the process is a short-lived token held by the connection.
///
/// This adapter is never exercised by the offline suite. What the suite does check is the SQL and the
/// mapping, in <c>PlatformPostgresContract</c> — live managed-identity qualification is separate
/// evidence and is still outstanding.
/// </summary>
public sealed class PostgresPlatformStateStore : IPlatformStateStore, IAsyncDisposable
{
    private static readonly string[] s_scope = ["https://ossrdbms-aad.database.windows.net/.default"];

    private readonly PlatformDatabaseOptions _options;
    private readonly Func<CancellationToken, Task<NpgsqlConnection>> _connectionFactory;
    private readonly string _schema;

    public PostgresPlatformStateStore(PlatformDatabaseOptions options, TokenCredential credential)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(credential);

        _options = options;
        _schema = PlatformSchema.ResolveSchemaName(options.Schema);
        _connectionFactory = cancellationToken =>
            ConnectWithManagedIdentityAsync(options, credential, cancellationToken);
    }

    internal PostgresPlatformStateStore(
        PlatformDatabaseOptions options,
        Func<CancellationToken, Task<NpgsqlConnection>> connectionFactory)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(connectionFactory);

        _options = options;
        _schema = PlatformSchema.ResolveSchemaName(options.Schema);
        _connectionFactory = connectionFactory;
    }

    public string Description => $"Azure Database for PostgreSQL schema '{_schema}'";

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    /// <summary>
    /// Applies every unapplied migration in order, under an advisory lock so concurrent replicas do not
    /// race, and records each one in the ledger. A failure throws: a host that could not establish its
    /// own authorization schema has no business serving requests.
    /// </summary>
    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection = await _connectionFactory(cancellationToken).ConfigureAwait(false);

        await using (NpgsqlCommand advisory = new("select pg_advisory_lock(@key)", connection))
        {
            advisory.Parameters.AddWithValue("key", PlatformSchema.AdvisoryLockKey);
            await advisory.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        try
        {
            await using (NpgsqlCommand ledger = new(PlatformSchema.LedgerStatement(_schema), connection))
            {
                await ledger.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            HashSet<int> applied = [];
            await using (NpgsqlCommand read = new(PlatformSchema.AppliedVersionsQuery(_schema), connection))
            await using (NpgsqlDataReader reader = await read.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            {
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    applied.Add(reader.GetInt32(0));
                }
            }

            PlatformSchema.ValidateAppliedVersions(applied);

            foreach (PlatformSchema.Migration migration in PlatformSchema.Migrations(_schema))
            {
                if (applied.Contains(migration.Version))
                {
                    continue;
                }

                await using NpgsqlTransaction transaction =
                    await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

                foreach (string statement in migration.Statements)
                {
                    await using NpgsqlCommand command = new(statement, connection, transaction);
                    await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }

                await using (NpgsqlCommand record = new(PlatformSchema.RecordVersionStatement(_schema), connection, transaction))
                {
                    record.Parameters.AddWithValue("version", migration.Version);
                    record.Parameters.AddWithValue("name", migration.Name);
                    await record.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }

                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            await using NpgsqlCommand release = new("select pg_advisory_unlock(@key)", connection);
            release.Parameters.AddWithValue("key", PlatformSchema.AdvisoryLockKey);
            await release.ExecuteNonQueryAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    public async Task<PlatformOrganization> EnsureOrganizationAsync(string tenantId, string displayName, CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection = await ConnectAsync(cancellationToken).ConfigureAwait(false);

        await using (NpgsqlCommand insert = new(
            $"""
            insert into {_schema}.organization (organization_id, tenant_id, display_name, created_utc, version)
            values (@id, @tenant, @name, @created, 1)
            on conflict (tenant_id) do nothing
            """, connection))
        {
            insert.Parameters.AddWithValue("id", $"org-{Guid.NewGuid():N}");
            insert.Parameters.AddWithValue("tenant", tenantId);
            insert.Parameters.AddWithValue("name", displayName);
            insert.Parameters.AddWithValue("created", NpgsqlDbType.TimestampTz, DateTimeOffset.UtcNow);
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using NpgsqlCommand read = new(
            $"select organization_id, tenant_id, display_name, created_utc, version from {_schema}.organization where tenant_id = @tenant",
            connection);
        read.Parameters.AddWithValue("tenant", tenantId);

        await using NpgsqlDataReader reader = await read.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("The organization row could not be read back after insertion.");
        }

        return new PlatformOrganization
        {
            OrganizationId = reader.GetString(0),
            TenantId = reader.GetString(1),
            DisplayName = reader.GetString(2),
            CreatedUtc = reader.GetFieldValue<DateTimeOffset>(3),
            Version = reader.GetInt32(4),
        };
    }

    public async Task<PlatformProject> CreateProjectAsync(PlatformProject project, PlatformMembership founder, CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection = await ConnectAsync(cancellationToken).ConfigureAwait(false);
        await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        await using (NpgsqlCommand command = new(
            $"""
            insert into {_schema}.project (project_id, organization_id, tenant_id, name, created_utc, version)
            values (@id, @organization, @tenant, @name, @created, @version)
            """, connection, transaction))
        {
            command.Parameters.AddWithValue("id", project.ProjectId);
            command.Parameters.AddWithValue("organization", project.OrganizationId);
            command.Parameters.AddWithValue("tenant", project.TenantId);
            command.Parameters.AddWithValue("name", project.Name);
            command.Parameters.AddWithValue("created", NpgsqlDbType.TimestampTz, project.CreatedUtc);
            command.Parameters.AddWithValue("version", project.Version);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (NpgsqlCommand command = new(MembershipUpsertSql(_schema), connection, transaction))
        {
            BindMembership(command, founder);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return project;
    }

    public async Task<PlatformProject?> GetProjectAsync(string tenantId, string projectId, CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection = await ConnectAsync(cancellationToken).ConfigureAwait(false);
        await using NpgsqlCommand command = new(
            $"""
            select project_id, organization_id, tenant_id, name, created_utc, version
            from {_schema}.project where tenant_id = @tenant and project_id = @project
            """, connection);
        command.Parameters.AddWithValue("tenant", tenantId);
        command.Parameters.AddWithValue("project", projectId);

        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadProject(reader) : null;
    }

    public async Task<IReadOnlyList<PlatformProject>> ProjectsForActorAsync(string tenantId, string objectId, CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection = await ConnectAsync(cancellationToken).ConfigureAwait(false);
        await using NpgsqlCommand command = new(
            $"""
            select p.project_id, p.organization_id, p.tenant_id, p.name, p.created_utc, p.version
            from {_schema}.project p
            join {_schema}.membership m on m.project_id = p.project_id
            where p.tenant_id = @tenant and m.tenant_id = @tenant and m.object_id = @object and m.removed_utc is null
            order by p.created_utc
            """, connection);
        command.Parameters.AddWithValue("tenant", tenantId);
        command.Parameters.AddWithValue("object", objectId);

        List<PlatformProject> projects = [];
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            projects.Add(ReadProject(reader));
        }

        return projects;
    }

    public async Task<PlatformMembership?> GetMembershipAsync(string tenantId, string projectId, string objectId, CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection = await ConnectAsync(cancellationToken).ConfigureAwait(false);
        await using NpgsqlCommand command = new(
            $"""
            select project_id, tenant_id, object_id, roles, created_utc, removed_utc, version
            from {_schema}.membership where tenant_id = @tenant and project_id = @project and object_id = @object
            """, connection);
        command.Parameters.AddWithValue("tenant", tenantId);
        command.Parameters.AddWithValue("project", projectId);
        command.Parameters.AddWithValue("object", objectId);

        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadMembership(reader) : null;
    }

    public async Task<IReadOnlyList<PlatformMembership>> MembershipsAsync(string tenantId, string projectId, CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection = await ConnectAsync(cancellationToken).ConfigureAwait(false);
        await using NpgsqlCommand command = new(
            $"""
            select project_id, tenant_id, object_id, roles, created_utc, removed_utc, version
            from {_schema}.membership where tenant_id = @tenant and project_id = @project
            """, connection);
        command.Parameters.AddWithValue("tenant", tenantId);
        command.Parameters.AddWithValue("project", projectId);

        List<PlatformMembership> memberships = [];
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            memberships.Add(ReadMembership(reader));
        }

        return memberships;
    }

    public async Task<PlatformMembership?> UpsertMembershipAsync(PlatformMembership membership, int? expectedVersion, CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection = await ConnectAsync(cancellationToken).ConfigureAwait(false);
        await using NpgsqlCommand command = new(
            expectedVersion is null ? MembershipUpsertSql(_schema) : MembershipVersionedUpdateSql(_schema),
            connection);
        BindMembership(command, membership);
        if (expectedVersion is int expected)
        {
            command.Parameters.AddWithValue("expected", expected);
        }

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 0
            ? null
            : await GetMembershipAsync(membership.TenantId, membership.ProjectId, membership.ObjectId, cancellationToken)
                .ConfigureAwait(false);
    }

    public async Task<PlatformTargetProfile?> CreateTargetProfileAsync(PlatformTargetProfile profile, CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection = await ConnectAsync(cancellationToken).ConfigureAwait(false);
        await using NpgsqlCommand command = new(TargetProfileInsertSql(_schema), connection);
        BindTargetProfile(command, profile);

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 0 ? null : profile;
    }

    public async Task<PlatformTargetProfile?> GetTargetProfileAsync(
        string tenantId, string projectId, string targetProfileId, int? version, CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection = await ConnectAsync(cancellationToken).ConfigureAwait(false);
        await using NpgsqlCommand command = new(
            $"""
            {TargetProfileSelectSql(_schema)}
            where tenant_id = @tenant and project_id = @project and target_profile_id = @profile
              and (@version is null or version = @version)
            order by version desc limit 1
            """, connection);
        command.Parameters.AddWithValue("tenant", tenantId);
        command.Parameters.AddWithValue("project", projectId);
        command.Parameters.AddWithValue("profile", targetProfileId);
        command.Parameters.AddWithValue(
            "version", NpgsqlDbType.Integer, version.HasValue ? version.Value : DBNull.Value);

        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadTargetProfile(reader) : null;
    }

    public async Task<IReadOnlyList<PlatformTargetProfile>> TargetProfilesAsync(string tenantId, string projectId, CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection = await ConnectAsync(cancellationToken).ConfigureAwait(false);
        await using NpgsqlCommand command = new(
            $"""
            select distinct on (target_profile_id) project_id, target_profile_id, version, tenant_id, azure_tenant_id,
                   subscription_id, resource_group, resource_id, region, endpoint_host, database_name, schema_name,
                   execution_identity, environment_name, stack_database, stack_front_end, stack_back_end,
                   canonical_hash, created_utc
            from {_schema}.target_profile
            where tenant_id = @tenant and project_id = @project
            order by target_profile_id, version desc
            """, connection);
        command.Parameters.AddWithValue("tenant", tenantId);
        command.Parameters.AddWithValue("project", projectId);

        List<PlatformTargetProfile> profiles = [];
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            profiles.Add(ReadTargetProfile(reader));
        }

        return profiles;
    }

    public async Task<PlatformApproval> CreateApprovalAsync(PlatformApproval approval, CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection = await ConnectAsync(cancellationToken).ConfigureAwait(false);
        await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        await using (NpgsqlCommand command = new(ApprovalInsertSql(_schema), connection, transaction))
        {
            BindApproval(command, approval);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await RecordEventAsync(connection, transaction, approval, approval.RequestedByObjectId, "Requested", approval.RequestNotes, cancellationToken)
            .ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return approval;
    }

    public async Task<PlatformApproval?> GetApprovalAsync(string tenantId, string approvalId, CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection = await ConnectAsync(cancellationToken).ConfigureAwait(false);
        await using NpgsqlCommand command = new(
            $"{ApprovalSelectSql(_schema)} where tenant_id = @tenant and approval_id = @approval", connection);
        command.Parameters.AddWithValue("tenant", tenantId);
        command.Parameters.AddWithValue("approval", approvalId);

        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadApproval(reader) : null;
    }

    public Task<IReadOnlyList<PlatformApproval>> ApprovalsForProjectAsync(string tenantId, string projectId, CancellationToken cancellationToken) =>
        ApprovalsAsync(
            $"{ApprovalSelectSql(_schema)} where tenant_id = @tenant and project_id = @key order by requested_utc desc",
            tenantId, projectId, cancellationToken);

    public Task<IReadOnlyList<PlatformApproval>> ApprovalsForRequesterAsync(string tenantId, string objectId, CancellationToken cancellationToken) =>
        ApprovalsAsync(
            $"{ApprovalSelectSql(_schema)} where tenant_id = @tenant and requested_by_object_id = @key order by requested_utc desc",
            tenantId, objectId, cancellationToken);

    public async Task<PlatformApproval?> UpdateApprovalAsync(PlatformApproval approval, int expectedVersion, CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection = await ConnectAsync(cancellationToken).ConfigureAwait(false);
        await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        int affected;
        await using (NpgsqlCommand command = new(ApprovalUpdateSql(_schema), connection, transaction))
        {
            BindApproval(command, approval);
            command.Parameters.AddWithValue("expected", expectedVersion);
            affected = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        if (affected == 0)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return null;
        }

        string actor = approval.RevokedByObjectId ?? approval.DecidedByObjectId ?? approval.RequestedByObjectId;
        string? notes = approval.RevocationNotes ?? approval.DecisionNotes;
        await RecordEventAsync(connection, transaction, approval, actor, approval.State.ToString(), notes, cancellationToken)
            .ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return approval with { Version = expectedVersion + 1 };
    }

    private async Task<IReadOnlyList<PlatformApproval>> ApprovalsAsync(
        string sql, string tenantId, string key, CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection = await ConnectAsync(cancellationToken).ConfigureAwait(false);
        await using NpgsqlCommand command = new(sql, connection);
        command.Parameters.AddWithValue("tenant", tenantId);
        command.Parameters.AddWithValue("key", key);

        List<PlatformApproval> approvals = [];
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            approvals.Add(ReadApproval(reader));
        }

        return approvals;
    }

    private async Task RecordEventAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        PlatformApproval approval,
        string actorObjectId,
        string action,
        string? notes,
        CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = new(ApprovalEventInsertSql(_schema), connection, transaction);
        command.Parameters.AddWithValue("approval", approval.ApprovalId);
        command.Parameters.AddWithValue("tenant", approval.TenantId);
        command.Parameters.AddWithValue("actor", actorObjectId);
        command.Parameters.AddWithValue("action", action);
        command.Parameters.AddWithValue("recorded", NpgsqlDbType.TimestampTz, DateTimeOffset.UtcNow);
        command.Parameters.AddWithValue("notes", (object?)notes ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public static string ApprovalEventInsertSql(string schema) =>
        $"""
        insert into {schema}.approval_event (approval_id, tenant_id, actor_object_id, action, recorded_utc, notes)
        values (@approval, @tenant, @actor, @action, @recorded, @notes)
        """;

    private Task<NpgsqlConnection> ConnectAsync(CancellationToken cancellationToken) =>
        _connectionFactory(cancellationToken);

    private static async Task<NpgsqlConnection> ConnectWithManagedIdentityAsync(
        PlatformDatabaseOptions options,
        TokenCredential credential,
        CancellationToken cancellationToken)
    {
        AccessToken token = await credential
            .GetTokenAsync(new TokenRequestContext(s_scope), cancellationToken)
            .ConfigureAwait(false);

        NpgsqlConnectionStringBuilder builder = new()
        {
            Host = options.Host,
            Database = options.Database,
            Username = options.User,
            Password = token.Token,
            SslMode = SslMode.Require,
            Timeout = 30,
            IncludeErrorDetail = false,
        };

        NpgsqlConnection connection = new(builder.ConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }

    // ---- SQL text, exposed so the contract tests can assert on it without a database ----

    public static string MembershipUpsertSql(string schema) =>
        $"""
        insert into {schema}.membership (project_id, tenant_id, object_id, roles, created_utc, removed_utc, version)
        values (@project, @tenant, @object, @roles, @created, @removed, @version)
        on conflict (project_id, object_id) do nothing
        """;

    public static string MembershipVersionedUpdateSql(string schema) =>
        $"""
        update {schema}.membership
        set roles = @roles, removed_utc = @removed, version = version + 1
        where project_id = @project and tenant_id = @tenant and object_id = @object and version = @expected
        """;

    public static string TargetProfileInsertSql(string schema) =>
        $"""
        insert into {schema}.target_profile (project_id, target_profile_id, version, tenant_id, azure_tenant_id,
            subscription_id, resource_group, resource_id, region, endpoint_host, database_name, schema_name,
            execution_identity, environment_name, stack_database, stack_front_end, stack_back_end, canonical_hash, created_utc)
        values (@project, @profile, @version, @tenant, @azureTenant, @subscription, @resourceGroup, @resourceId,
            @region, @endpointHost, @databaseName, @schemaName, @executionIdentity, @environmentName,
            @stackDatabase, @stackFrontEnd, @stackBackEnd, @canonicalHash, @created)
        on conflict (project_id, target_profile_id, version) do nothing
        """;

    public static string TargetProfileSelectSql(string schema) =>
        $"""
        select project_id, target_profile_id, version, tenant_id, azure_tenant_id, subscription_id, resource_group,
               resource_id, region, endpoint_host, database_name, schema_name, execution_identity, environment_name,
               stack_database, stack_front_end, stack_back_end, canonical_hash, created_utc
        from {schema}.target_profile
        """;

    public static string ApprovalSelectSql(string schema) =>
        $"""
        select approval_id, project_id, tenant_id, requested_by_object_id, requested_utc, state, scope, required_role,
               engagement_id, source_snapshot_hash, plan_input_hash, target_profile_id, target_profile_version,
               target_profile_hash, expires_utc, request_notes, decided_by_object_id, decided_utc, decision_notes,
               revoked_by_object_id, revoked_utc, revocation_notes, version
        from {schema}.approval
        """;

    public static string ApprovalInsertSql(string schema) =>
        $"""
        insert into {schema}.approval (approval_id, project_id, tenant_id, requested_by_object_id, requested_utc,
            state, scope, required_role, engagement_id, source_snapshot_hash, plan_input_hash, target_profile_id,
            target_profile_version, target_profile_hash, expires_utc, request_notes, decided_by_object_id, decided_utc,
            decision_notes, revoked_by_object_id, revoked_utc, revocation_notes, version)
        values (@approval, @project, @tenant, @requestedBy, @requested, @state, @scope, @role, @engagement,
            @sourceHash, @planHash, @profile, @profileVersion, @profileHash, @expires, @requestNotes, @decidedBy,
            @decided, @decisionNotes, @revokedBy, @revoked, @revocationNotes, @version)
        """;

    public static string ApprovalUpdateSql(string schema) =>
        $"""
        update {schema}.approval
        set state = @state, decided_by_object_id = @decidedBy, decided_utc = @decided, decision_notes = @decisionNotes,
            revoked_by_object_id = @revokedBy, revoked_utc = @revoked, revocation_notes = @revocationNotes,
            version = version + 1
        where approval_id = @approval and tenant_id = @tenant and version = @expected
        """;

    private static void BindMembership(NpgsqlCommand command, PlatformMembership membership)
    {
        command.Parameters.AddWithValue("project", membership.ProjectId);
        command.Parameters.AddWithValue("tenant", membership.TenantId);
        command.Parameters.AddWithValue("object", membership.ObjectId);
        command.Parameters.AddWithValue("roles", NpgsqlDbType.Array | NpgsqlDbType.Text, membership.Roles.ToArray());
        command.Parameters.AddWithValue("created", NpgsqlDbType.TimestampTz, membership.CreatedUtc);
        command.Parameters.AddWithValue("removed", membership.RemovedUtc.HasValue ? membership.RemovedUtc.Value : DBNull.Value);
        command.Parameters.AddWithValue("version", membership.Version);
    }

    private static void BindTargetProfile(NpgsqlCommand command, PlatformTargetProfile profile)
    {
        command.Parameters.AddWithValue("project", profile.ProjectId);
        command.Parameters.AddWithValue("profile", profile.TargetProfileId);
        command.Parameters.AddWithValue("version", profile.Version);
        command.Parameters.AddWithValue("tenant", profile.TenantId);
        command.Parameters.AddWithValue("azureTenant", profile.AzureTenantId);
        command.Parameters.AddWithValue("subscription", profile.SubscriptionId);
        command.Parameters.AddWithValue("resourceGroup", profile.ResourceGroup);
        command.Parameters.AddWithValue("resourceId", profile.ResourceId);
        command.Parameters.AddWithValue("region", profile.Region);
        command.Parameters.AddWithValue("endpointHost", profile.EndpointHost);
        command.Parameters.AddWithValue("databaseName", profile.DatabaseName);
        command.Parameters.AddWithValue("schemaName", profile.SchemaName);
        command.Parameters.AddWithValue("executionIdentity", profile.ExecutionIdentity);
        command.Parameters.AddWithValue("environmentName", profile.EnvironmentName);
        command.Parameters.AddWithValue("stackDatabase", profile.StackDatabase);
        command.Parameters.AddWithValue("stackFrontEnd", profile.StackFrontEnd);
        command.Parameters.AddWithValue("stackBackEnd", profile.StackBackEnd);
        command.Parameters.AddWithValue("canonicalHash", profile.CanonicalHash);
        command.Parameters.AddWithValue("created", NpgsqlDbType.TimestampTz, profile.CreatedUtc);
    }

    private static void BindApproval(NpgsqlCommand command, PlatformApproval approval)
    {
        command.Parameters.AddWithValue("approval", approval.ApprovalId);
        command.Parameters.AddWithValue("project", approval.ProjectId);
        command.Parameters.AddWithValue("tenant", approval.TenantId);
        command.Parameters.AddWithValue("requestedBy", approval.RequestedByObjectId);
        command.Parameters.AddWithValue("requested", NpgsqlDbType.TimestampTz, approval.RequestedUtc);
        command.Parameters.AddWithValue("state", approval.State.ToString());
        command.Parameters.AddWithValue("scope", approval.Scope.ToString());
        command.Parameters.AddWithValue("role", approval.RequiredRole);
        command.Parameters.AddWithValue("engagement", approval.EngagementId);
        command.Parameters.AddWithValue("sourceHash", approval.SourceSnapshotHash);
        command.Parameters.AddWithValue("planHash", approval.PlanInputHash);
        command.Parameters.AddWithValue("profile", approval.TargetProfileId);
        command.Parameters.AddWithValue("profileVersion", approval.TargetProfileVersion);
        command.Parameters.AddWithValue("profileHash", approval.TargetProfileHash);
        command.Parameters.AddWithValue("expires", NpgsqlDbType.TimestampTz, approval.ExpiresUtc);
        command.Parameters.AddWithValue("requestNotes", (object?)approval.RequestNotes ?? DBNull.Value);
        command.Parameters.AddWithValue("decidedBy", (object?)approval.DecidedByObjectId ?? DBNull.Value);
        command.Parameters.AddWithValue("decided", approval.DecidedUtc.HasValue ? approval.DecidedUtc.Value : DBNull.Value);
        command.Parameters.AddWithValue("decisionNotes", (object?)approval.DecisionNotes ?? DBNull.Value);
        command.Parameters.AddWithValue("revokedBy", (object?)approval.RevokedByObjectId ?? DBNull.Value);
        command.Parameters.AddWithValue("revoked", approval.RevokedUtc.HasValue ? approval.RevokedUtc.Value : DBNull.Value);
        command.Parameters.AddWithValue("revocationNotes", (object?)approval.RevocationNotes ?? DBNull.Value);
        command.Parameters.AddWithValue("version", approval.Version);
    }

    private static PlatformProject ReadProject(NpgsqlDataReader reader) => new()
    {
        ProjectId = reader.GetString(0),
        OrganizationId = reader.GetString(1),
        TenantId = reader.GetString(2),
        Name = reader.GetString(3),
        CreatedUtc = reader.GetFieldValue<DateTimeOffset>(4),
        Version = reader.GetInt32(5),
    };

    private static PlatformMembership ReadMembership(NpgsqlDataReader reader) => new()
    {
        ProjectId = reader.GetString(0),
        TenantId = reader.GetString(1),
        ObjectId = reader.GetString(2),
        Roles = reader.GetFieldValue<string[]>(3),
        CreatedUtc = reader.GetFieldValue<DateTimeOffset>(4),
        RemovedUtc = reader.IsDBNull(5) ? null : reader.GetFieldValue<DateTimeOffset>(5),
        Version = reader.GetInt32(6),
    };

    private static PlatformTargetProfile ReadTargetProfile(NpgsqlDataReader reader) => new()
    {
        ProjectId = reader.GetString(0),
        TargetProfileId = reader.GetString(1),
        Version = reader.GetInt32(2),
        TenantId = reader.GetString(3),
        AzureTenantId = reader.GetString(4),
        SubscriptionId = reader.GetString(5),
        ResourceGroup = reader.GetString(6),
        ResourceId = reader.GetString(7),
        Region = reader.GetString(8),
        EndpointHost = reader.GetString(9),
        DatabaseName = reader.GetString(10),
        SchemaName = reader.GetString(11),
        ExecutionIdentity = reader.GetString(12),
        EnvironmentName = reader.GetString(13),
        StackDatabase = reader.GetString(14),
        StackFrontEnd = reader.GetString(15),
        StackBackEnd = reader.GetString(16),
        CanonicalHash = reader.GetString(17),
        CreatedUtc = reader.GetFieldValue<DateTimeOffset>(18),
    };

    private static PlatformApproval ReadApproval(NpgsqlDataReader reader) => new()
    {
        ApprovalId = reader.GetString(0),
        ProjectId = reader.GetString(1),
        TenantId = reader.GetString(2),
        RequestedByObjectId = reader.GetString(3),
        RequestedUtc = reader.GetFieldValue<DateTimeOffset>(4),
        State = Enum.Parse<PlatformApprovalState>(reader.GetString(5)),
        Scope = Enum.Parse<WorkbenchMutationScope>(reader.GetString(6)),
        RequiredRole = reader.GetString(7),
        EngagementId = reader.GetString(8),
        SourceSnapshotHash = reader.GetString(9),
        PlanInputHash = reader.GetString(10),
        TargetProfileId = reader.GetString(11),
        TargetProfileVersion = reader.GetInt32(12),
        TargetProfileHash = reader.GetString(13),
        ExpiresUtc = reader.GetFieldValue<DateTimeOffset>(14),
        RequestNotes = reader.IsDBNull(15) ? null : reader.GetString(15),
        DecidedByObjectId = reader.IsDBNull(16) ? null : reader.GetString(16),
        DecidedUtc = reader.IsDBNull(17) ? null : reader.GetFieldValue<DateTimeOffset>(17),
        DecisionNotes = reader.IsDBNull(18) ? null : reader.GetString(18),
        RevokedByObjectId = reader.IsDBNull(19) ? null : reader.GetString(19),
        RevokedUtc = reader.IsDBNull(20) ? null : reader.GetFieldValue<DateTimeOffset>(20),
        RevocationNotes = reader.IsDBNull(21) ? null : reader.GetString(21),
        Version = reader.GetInt32(22),
    };
}
