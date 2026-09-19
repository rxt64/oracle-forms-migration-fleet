// Copyright (c) Microsoft. All rights reserved.

using Azure.Core;
using Npgsql;
using NpgsqlTypes;

namespace OracleFormsMigrationFleet.Hosting;

public sealed class PostgresSandboxProjectBindingStore : ISandboxProjectBindingStore
{
    private static readonly string[] s_scope = ["https://ossrdbms-aad.database.windows.net/.default"];

    private readonly Func<CancellationToken, Task<NpgsqlConnection>> _connectionFactory;
    private readonly string _schema;

    public PostgresSandboxProjectBindingStore(PlatformDatabaseOptions options, TokenCredential credential)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(credential);

        _schema = PlatformSchema.ResolveSchemaName(options.Schema);
        _connectionFactory = cancellationToken => ConnectAsync(options, credential, cancellationToken);
    }

    internal PostgresSandboxProjectBindingStore(
        PlatformDatabaseOptions options,
        Func<CancellationToken, Task<NpgsqlConnection>> connectionFactory)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(connectionFactory);

        _schema = PlatformSchema.ResolveSchemaName(options.Schema);
        _connectionFactory = connectionFactory;
    }

    public async Task<string> BindSandboxProjectAsync(
        string tenantId, string projectId, CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection = await _connectionFactory(cancellationToken).ConfigureAwait(false);
        await using (NpgsqlCommand bind = new(InsertSql(_schema), connection))
        {
            bind.Parameters.AddWithValue("tenant", tenantId);
            bind.Parameters.AddWithValue("project", projectId);
            bind.Parameters.AddWithValue("bound", NpgsqlDbType.TimestampTz, DateTimeOffset.UtcNow);
            await bind.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using NpgsqlCommand read = new(
            $"select project_id from {_schema}.sandbox_project_binding where tenant_id = @tenant", connection);
        read.Parameters.AddWithValue("tenant", tenantId);
        return (string?)await read.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The sandbox project binding could not be read after insertion.");
    }

    public async Task<string?> GetSandboxProjectAsync(string tenantId, CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection = await _connectionFactory(cancellationToken).ConfigureAwait(false);
        await using NpgsqlCommand read = new(
            $"select project_id from {_schema}.sandbox_project_binding where tenant_id = @tenant", connection);
        read.Parameters.AddWithValue("tenant", tenantId);
        return (string?)await read.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
    }

    public static string InsertSql(string schema) =>
        $"""
        insert into {schema}.sandbox_project_binding (tenant_id, project_id, bound_utc)
        values (@tenant, @project, @bound)
        on conflict (tenant_id) do nothing
        """;

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
