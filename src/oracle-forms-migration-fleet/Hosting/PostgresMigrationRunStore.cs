// Copyright (c) Microsoft. All rights reserved.

using System.Text.Json;
using System.Text.Json.Serialization;
using Azure.Core;
using Npgsql;
using NpgsqlTypes;
using OracleFormsMigrationFleet.Fleet;
using OracleFormsMigrationFleet.Fleet.Execution;

namespace OracleFormsMigrationFleet.Hosting;

public sealed class PostgresMigrationRunStore : IMigrationRunStore
{
    private static readonly string[] s_scope = ["https://ossrdbms-aad.database.windows.net/.default"];
    private static readonly JsonSerializerOptions s_json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly Func<CancellationToken, Task<NpgsqlConnection>> _connectionFactory;
    private readonly string _schema;

    public PostgresMigrationRunStore(PlatformDatabaseOptions options, TokenCredential credential)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(credential);
        _schema = PlatformSchema.ResolveSchemaName(options.Schema);
        _connectionFactory = cancellationToken => ConnectAsync(options, credential, cancellationToken);
    }

    internal PostgresMigrationRunStore(
        PlatformDatabaseOptions options,
        Func<CancellationToken, Task<NpgsqlConnection>> connectionFactory)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(connectionFactory);
        _schema = PlatformSchema.ResolveSchemaName(options.Schema);
        _connectionFactory = connectionFactory;
    }

    public async Task<MigrationRunRecord> EnqueueAsync(MigrationRunRecord run, CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection = await _connectionFactory(cancellationToken).ConfigureAwait(false);
        await using NpgsqlCommand command = new(InsertSql(_schema), connection);
        BindRun(command, run);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return run;
    }

    public async Task<MigrationRunRecord?> GetAsync(string tenantId, string runId, CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection = await _connectionFactory(cancellationToken).ConfigureAwait(false);
        await using NpgsqlCommand command = new(
            $"{SelectSql(_schema)} where tenant_id = @tenant and run_id = @run", connection);
        command.Parameters.AddWithValue("tenant", tenantId);
        command.Parameters.AddWithValue("run", runId);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadRun(reader) : null;
    }

    public async Task<IReadOnlyList<MigrationRunRecord>> ForProjectAsync(
        string tenantId, string projectId, int limit, CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection = await _connectionFactory(cancellationToken).ConfigureAwait(false);
        await using NpgsqlCommand command = new(
            $"{SelectSql(_schema)} where tenant_id = @tenant and project_id = @project order by enqueued_utc desc limit @limit",
            connection);
        command.Parameters.AddWithValue("tenant", tenantId);
        command.Parameters.AddWithValue("project", projectId);
        command.Parameters.AddWithValue("limit", Math.Clamp(limit, 1, 100));
        List<MigrationRunRecord> runs = [];
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            runs.Add(ReadRun(reader));
        }
        return runs;
    }

    public async Task<MigrationRunClaim?> ClaimAsync(
        string nodeId, string workerId, DateTimeOffset now, TimeSpan lease, CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection = await _connectionFactory(cancellationToken).ConfigureAwait(false);
        await using NpgsqlCommand command = new(ClaimSql(_schema), connection);
        command.Parameters.AddWithValue("node", nodeId);
        command.Parameters.AddWithValue("worker", workerId);
        command.Parameters.AddWithValue("lease", NpgsqlDbType.Interval, lease);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }
        MigrationRunRecord run = ReadRun(reader);
        return new MigrationRunClaim(run, run.FenceToken);
    }

    public async Task<int> ReconcileExpiredAsync(
        string currentNodeId, DateTimeOffset now, string reason, CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection = await _connectionFactory(cancellationToken).ConfigureAwait(false);
        await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using NpgsqlCommand select = new(
            $"""
            select run_id, last_sequence
            from {_schema}.migration_run
                        where (state = 'Running' or (state = 'Leased' and workspace_node_id <> @node))
                            and lease_expires_utc <= now()
            for update skip locked
            """,
            connection,
            transaction);
        select.Parameters.AddWithValue("node", currentNodeId);
        List<(string RunId, long Sequence)> expired = [];
        await using (NpgsqlDataReader reader = await select.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                expired.Add((reader.GetString(0), reader.GetInt64(1) + 1));
            }
        }

        foreach ((string runId, long sequence) in expired)
        {
            await using NpgsqlCommand update = new(
                $"update {_schema}.migration_run set state = 'Interrupted', completed_utc = now(), " +
                "lease_owner = null, lease_expires_utc = null, fence_token = fence_token + 1, " +
                "last_sequence = @sequence, failure_reason = @reason, version = version + 1 where run_id = @run",
                connection,
                transaction);
            update.Parameters.AddWithValue("sequence", sequence);
            update.Parameters.AddWithValue("reason", reason);
            update.Parameters.AddWithValue("run", runId);
            await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            await using NpgsqlCommand terminal = new(
                $"insert into {_schema}.migration_run_event " +
                "(run_id, sequence, recorded_utc, level, text, signal_json, outcome_json) " +
                "values (@run, @sequence, now(), 'error', @reason, @signal, null)",
                connection,
                transaction);
            terminal.Parameters.AddWithValue("run", runId);
            terminal.Parameters.AddWithValue("sequence", sequence);
            terminal.Parameters.AddWithValue("reason", reason);
            AddJson(terminal, "signal", new ProgressSignal(
                ProgressOperations.MigrationRun,
                ProgressActions.RunFailed,
                ProgressState.Failed,
                "Recovering a durable migration run.",
                reason,
                "Review retained events and destination state before retrying."));
            await terminal.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return expired.Count;
    }

    public Task<bool> MarkRunningAsync(
        string runId, long fenceToken, DateTimeOffset now, CancellationToken cancellationToken) =>
        FencedUpdateAsync(
            $"update {_schema}.migration_run set state = 'Running', started_utc = coalesce(started_utc, @now), version = version + 1 " +
            "where run_id = @run and fence_token = @fence and state = 'Leased' " +
            "and lease_expires_utc > now() and cancel_requested_utc is null",
            runId,
            fenceToken,
            command => command.Parameters.AddWithValue("now", NpgsqlDbType.TimestampTz, now),
            cancellationToken);

    public Task<bool> RenewAsync(
        string runId, long fenceToken, TimeSpan lease, CancellationToken cancellationToken) =>
        FencedUpdateAsync(
            $"update {_schema}.migration_run set lease_expires_utc = now() + @lease, version = version + 1 " +
            "where run_id = @run and fence_token = @fence and state in ('Leased','Running') and lease_expires_utc > now()",
            runId,
            fenceToken,
            command => command.Parameters.AddWithValue("lease", NpgsqlDbType.Interval, lease),
            cancellationToken);

    public async Task<MigrationRunEvent?> AppendEventAsync(
        string runId,
        long fenceToken,
        DateTimeOffset recordedUtc,
        string level,
        string text,
        ProgressSignal? signal,
        WorkbenchExecutionView? outcome,
        CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection = await _connectionFactory(cancellationToken).ConfigureAwait(false);
        await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using NpgsqlCommand advance = new(
            $"update {_schema}.migration_run set last_sequence = last_sequence + 1, version = version + 1 " +
            "where run_id = @run and fence_token = @fence and state in ('Leased','Running') " +
            "and lease_expires_utc > now() returning last_sequence",
            connection,
            transaction);
        advance.Parameters.AddWithValue("run", runId);
        advance.Parameters.AddWithValue("fence", fenceToken);
        object? next = await advance.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (next is null)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return null;
        }

        long sequence = Convert.ToInt64(next, System.Globalization.CultureInfo.InvariantCulture);
        await using NpgsqlCommand insert = new(
            $"insert into {_schema}.migration_run_event " +
            "(run_id, sequence, recorded_utc, level, text, signal_json, outcome_json) " +
            "values (@run, @sequence, @recorded, @level, @text, @signal, @outcome)",
            connection,
            transaction);
        insert.Parameters.AddWithValue("run", runId);
        insert.Parameters.AddWithValue("sequence", sequence);
        insert.Parameters.AddWithValue("recorded", NpgsqlDbType.TimestampTz, recordedUtc);
        insert.Parameters.AddWithValue("level", level);
        insert.Parameters.AddWithValue("text", text);
        AddJson(insert, "signal", signal);
        AddJson(insert, "outcome", outcome);
        await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new MigrationRunEvent(runId, sequence, recordedUtc, level, text, signal, outcome);
    }

    public async Task<IReadOnlyList<MigrationRunEvent>> EventsAsync(
        string tenantId, string runId, long afterSequence, CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection = await _connectionFactory(cancellationToken).ConfigureAwait(false);
        await using NpgsqlCommand command = new(
            $"""
            select e.run_id, e.sequence, e.recorded_utc, e.level, e.text, e.signal_json, e.outcome_json
            from {_schema}.migration_run_event e
            join {_schema}.migration_run r on r.run_id = e.run_id
            where r.tenant_id = @tenant and e.run_id = @run and e.sequence > @after
            order by e.sequence
            """,
            connection);
        command.Parameters.AddWithValue("tenant", tenantId);
        command.Parameters.AddWithValue("run", runId);
        command.Parameters.AddWithValue("after", afterSequence);
        List<MigrationRunEvent> events = [];
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            events.Add(new MigrationRunEvent(
                reader.GetString(0),
                reader.GetInt64(1),
                reader.GetFieldValue<DateTimeOffset>(2),
                reader.GetString(3),
                reader.GetString(4),
                ReadJson<ProgressSignal>(reader, 5),
                ReadJson<WorkbenchExecutionView>(reader, 6)));
        }
        return events;
    }

    public async Task<bool> CompleteAsync(
        string runId,
        long fenceToken,
        MigrationRunState state,
        DateTimeOffset completedUtc,
        WorkbenchExecutionView? outcome,
        string? failureReason,
        IReadOnlyList<MigrationRunArtifact> artifacts,
        string terminalLevel,
        ProgressSignal terminalSignal,
        CancellationToken cancellationToken)
    {
        if (state is not (MigrationRunState.Succeeded or MigrationRunState.Failed or
            MigrationRunState.Cancelled or MigrationRunState.Interrupted))
        {
            throw new ArgumentOutOfRangeException(nameof(state), state, "A completed run requires a terminal state.");
        }

        await using NpgsqlConnection connection = await _connectionFactory(cancellationToken).ConfigureAwait(false);
        await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using NpgsqlCommand complete = new(
            $"update {_schema}.migration_run set state = @state, completed_utc = @completed, lease_owner = null, " +
            "lease_expires_utc = null, outcome_json = @outcome, failure_reason = @failure, " +
            "last_sequence = last_sequence + 1, version = version + 1 " +
            "where run_id = @run and fence_token = @fence and state in ('Leased','Running') " +
            "and lease_expires_utc > now() and (@state = 'Cancelled' or cancel_requested_utc is null) returning last_sequence",
            connection,
            transaction);
        complete.Parameters.AddWithValue("state", state.ToString());
        complete.Parameters.AddWithValue("completed", NpgsqlDbType.TimestampTz, completedUtc);
        AddJson(complete, "outcome", outcome);
        complete.Parameters.AddWithValue("failure", NpgsqlDbType.Text, (object?)failureReason ?? DBNull.Value);
        complete.Parameters.AddWithValue("run", runId);
        complete.Parameters.AddWithValue("fence", fenceToken);
        object? terminalSequence = await complete.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (terminalSequence is null)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return false;
        }

        await using (NpgsqlCommand terminal = new(
            $"insert into {_schema}.migration_run_event " +
            "(run_id, sequence, recorded_utc, level, text, signal_json, outcome_json) " +
            "values (@run, @sequence, @recorded, @level, @text, @signal, @outcome)",
            connection,
            transaction))
        {
            terminal.Parameters.AddWithValue("run", runId);
            terminal.Parameters.AddWithValue(
                "sequence", Convert.ToInt64(terminalSequence, System.Globalization.CultureInfo.InvariantCulture));
            terminal.Parameters.AddWithValue("recorded", NpgsqlDbType.TimestampTz, completedUtc);
            terminal.Parameters.AddWithValue("level", terminalLevel);
            terminal.Parameters.AddWithValue("text", failureReason ?? string.Empty);
            AddJson(terminal, "signal", terminalSignal);
            AddJson(terminal, "outcome", outcome);
            await terminal.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        foreach (MigrationRunArtifact artifact in artifacts)
        {
            await using NpgsqlCommand insert = new(
                $"insert into {_schema}.migration_run_artifact " +
                "(run_id, path, kind, description, byte_length, content_sha256) " +
                "values (@run, @path, @kind, @description, @bytes, @hash) on conflict (run_id, path) do nothing",
                connection,
                transaction);
            insert.Parameters.AddWithValue("run", runId);
            insert.Parameters.AddWithValue("path", artifact.Path);
            insert.Parameters.AddWithValue("kind", artifact.Kind);
            insert.Parameters.AddWithValue("description", artifact.Description);
            insert.Parameters.AddWithValue("bytes", artifact.ByteLength);
            insert.Parameters.AddWithValue("hash", artifact.ContentSha256);
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    public async Task<IReadOnlyList<MigrationRunArtifact>> ArtifactsAsync(
        string tenantId, string runId, CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection = await _connectionFactory(cancellationToken).ConfigureAwait(false);
        await using NpgsqlCommand command = new(
            $"""
            select a.run_id, a.path, a.kind, a.description, a.byte_length, a.content_sha256
            from {_schema}.migration_run_artifact a
            join {_schema}.migration_run r on r.run_id = a.run_id
            where r.tenant_id = @tenant and a.run_id = @run
            order by a.path
            """,
            connection);
        command.Parameters.AddWithValue("tenant", tenantId);
        command.Parameters.AddWithValue("run", runId);
        List<MigrationRunArtifact> artifacts = [];
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            artifacts.Add(new MigrationRunArtifact(
                reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetInt64(4), reader.GetString(5)));
        }
        return artifacts;
    }

    public async Task<bool> RequestCancellationAsync(
        string tenantId, string runId, string actorObjectId, DateTimeOffset requestedUtc, CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection = await _connectionFactory(cancellationToken).ConfigureAwait(false);
        await using NpgsqlCommand command = new(
            $"update {_schema}.migration_run set cancel_requested_utc = @requested, " +
            "cancel_requested_by_object_id = @actor, version = version + 1 " +
            "where tenant_id = @tenant and run_id = @run and state not in ('Succeeded','Failed','Cancelled','Interrupted')",
            connection);
        command.Parameters.AddWithValue("requested", NpgsqlDbType.TimestampTz, requestedUtc);
        command.Parameters.AddWithValue("actor", actorObjectId);
        command.Parameters.AddWithValue("tenant", tenantId);
        command.Parameters.AddWithValue("run", runId);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0;
    }

    public static string ClaimSql(string schema) =>
        $"""
        with candidate as (
            select run_id
            from {schema}.migration_run
            where workspace_node_id = @node
              and (state = 'Queued' or (state = 'Leased' and lease_expires_utc <= now()))
            order by enqueued_utc
            for update skip locked
            limit 1
        )
        update {schema}.migration_run r
        set state = 'Leased', lease_owner = @worker, lease_expires_utc = now() + @lease,
            fence_token = r.fence_token + 1, version = r.version + 1
        from candidate c
        where r.run_id = c.run_id
        returning {ReturningColumns("r")}
        """;

    public static string InsertSql(string schema) =>
        $"""
        insert into {schema}.migration_run
            (run_id, tenant_id, project_id, actor_object_id, workspace_id, workspace_node_id,
             workspace_owner_id, source_snapshot_hash, plan_input_hash, target_profile_id,
             target_profile_version, target_profile_hash, request_json, state, enqueued_utc,
             started_utc, completed_utc, lease_owner, lease_expires_utc, fence_token,
             cancel_requested_utc, cancel_requested_by_object_id, last_sequence, outcome_json,
             failure_reason, version)
        values
            (@run, @tenant, @project, @actor, @workspace, @node, @workspaceOwner, @sourceHash,
             @planHash, @profile, @profileVersion, @profileHash, @request, @state, @enqueued,
             @started, @completed, @leaseOwner, @leaseExpires, @fence, @cancelRequested,
             @cancelActor, @lastSequence, @outcome, @failure, @version)
        """;

    public static string SelectSql(string schema) =>
        $"select {ReturningColumns(string.Empty)} from {schema}.migration_run";

    private static string ReturningColumns(string alias)
    {
        string prefix = alias.Length == 0 ? string.Empty : alias + ".";
        return string.Join(", ", new[]
        {
            "run_id", "tenant_id", "project_id", "actor_object_id", "workspace_id", "workspace_node_id",
            "workspace_owner_id", "source_snapshot_hash", "plan_input_hash", "target_profile_id",
            "target_profile_version", "target_profile_hash", "request_json", "state", "enqueued_utc",
            "started_utc", "completed_utc", "lease_owner", "lease_expires_utc", "fence_token",
            "cancel_requested_utc", "cancel_requested_by_object_id", "last_sequence", "outcome_json",
            "failure_reason", "version",
        }.Select(column => prefix + column));
    }

    private async Task<bool> FencedUpdateAsync(
        string sql,
        string runId,
        long fenceToken,
        Action<NpgsqlCommand> bind,
        CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection = await _connectionFactory(cancellationToken).ConfigureAwait(false);
        await using NpgsqlCommand command = new(sql, connection);
        command.Parameters.AddWithValue("run", runId);
        command.Parameters.AddWithValue("fence", fenceToken);
        bind(command);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0;
    }

    private static void BindRun(NpgsqlCommand command, MigrationRunRecord run)
    {
        command.Parameters.AddWithValue("run", run.RunId);
        command.Parameters.AddWithValue("tenant", run.TenantId);
        command.Parameters.AddWithValue("project", run.ProjectId);
        command.Parameters.AddWithValue("actor", run.ActorObjectId);
        command.Parameters.AddWithValue("workspace", run.WorkspaceId);
        command.Parameters.AddWithValue("node", run.WorkspaceNodeId);
        command.Parameters.AddWithValue("workspaceOwner", run.WorkspaceOwnerId);
        command.Parameters.AddWithValue("sourceHash", run.SourceSnapshotHash);
        command.Parameters.AddWithValue("planHash", run.PlanInputHash);
        command.Parameters.AddWithValue("profile", run.TargetProfileId);
        command.Parameters.AddWithValue("profileVersion", run.TargetProfileVersion);
        command.Parameters.AddWithValue("profileHash", run.TargetProfileHash);
        AddJson(command, "request", run.Request);
        command.Parameters.AddWithValue("state", run.State.ToString());
        command.Parameters.AddWithValue("enqueued", NpgsqlDbType.TimestampTz, run.EnqueuedUtc);
        command.Parameters.AddWithValue("started", NpgsqlDbType.TimestampTz, (object?)run.StartedUtc ?? DBNull.Value);
        command.Parameters.AddWithValue("completed", NpgsqlDbType.TimestampTz, (object?)run.CompletedUtc ?? DBNull.Value);
        command.Parameters.AddWithValue("leaseOwner", NpgsqlDbType.Text, (object?)run.LeaseOwner ?? DBNull.Value);
        command.Parameters.AddWithValue("leaseExpires", NpgsqlDbType.TimestampTz, (object?)run.LeaseExpiresUtc ?? DBNull.Value);
        command.Parameters.AddWithValue("fence", run.FenceToken);
        command.Parameters.AddWithValue("cancelRequested", NpgsqlDbType.TimestampTz, (object?)run.CancelRequestedUtc ?? DBNull.Value);
        command.Parameters.AddWithValue("cancelActor", NpgsqlDbType.Text, (object?)run.CancelRequestedByObjectId ?? DBNull.Value);
        command.Parameters.AddWithValue("lastSequence", run.LastSequence);
        AddJson(command, "outcome", run.Outcome);
        command.Parameters.AddWithValue("failure", NpgsqlDbType.Text, (object?)run.FailureReason ?? DBNull.Value);
        command.Parameters.AddWithValue("version", run.Version);
    }

    private static MigrationRunRecord ReadRun(NpgsqlDataReader reader) => new()
    {
        RunId = reader.GetString(0),
        TenantId = reader.GetString(1),
        ProjectId = reader.GetString(2),
        ActorObjectId = reader.GetString(3),
        WorkspaceId = reader.GetString(4),
        WorkspaceNodeId = reader.GetString(5),
        WorkspaceOwnerId = reader.GetString(6),
        SourceSnapshotHash = reader.GetString(7),
        PlanInputHash = reader.GetString(8),
        TargetProfileId = reader.GetString(9),
        TargetProfileVersion = reader.GetInt32(10),
        TargetProfileHash = reader.GetString(11),
        Request = JsonSerializer.Deserialize<MigrationRunRequest>(reader.GetString(12), s_json)
            ?? throw new JsonException("A stored run request could not be read."),
        State = Enum.Parse<MigrationRunState>(reader.GetString(13)),
        EnqueuedUtc = reader.GetFieldValue<DateTimeOffset>(14),
        StartedUtc = reader.IsDBNull(15) ? null : reader.GetFieldValue<DateTimeOffset>(15),
        CompletedUtc = reader.IsDBNull(16) ? null : reader.GetFieldValue<DateTimeOffset>(16),
        LeaseOwner = reader.IsDBNull(17) ? null : reader.GetString(17),
        LeaseExpiresUtc = reader.IsDBNull(18) ? null : reader.GetFieldValue<DateTimeOffset>(18),
        FenceToken = reader.GetInt64(19),
        CancelRequestedUtc = reader.IsDBNull(20) ? null : reader.GetFieldValue<DateTimeOffset>(20),
        CancelRequestedByObjectId = reader.IsDBNull(21) ? null : reader.GetString(21),
        LastSequence = reader.GetInt64(22),
        Outcome = ReadJson<WorkbenchExecutionView>(reader, 23),
        FailureReason = reader.IsDBNull(24) ? null : reader.GetString(24),
        Version = reader.GetInt32(25),
    };

    private static void AddJson<T>(NpgsqlCommand command, string name, T? value) where T : class =>
        command.Parameters.AddWithValue(
            name,
            NpgsqlDbType.Jsonb,
            value is null ? DBNull.Value : JsonSerializer.Serialize(value, s_json));

    private static T? ReadJson<T>(NpgsqlDataReader reader, int ordinal) where T : class =>
        reader.IsDBNull(ordinal) ? null : JsonSerializer.Deserialize<T>(reader.GetString(ordinal), s_json);

    private static async Task<NpgsqlConnection> ConnectAsync(
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
}
