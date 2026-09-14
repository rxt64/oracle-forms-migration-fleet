// Copyright (c) Microsoft. All rights reserved.

using System.Globalization;
using Azure.Core;
using Npgsql;
using OracleFormsMigrationFleet.Fleet.Execution;

namespace OracleFormsMigrationFleet.Hosting;

/// <summary>
/// Loads rows into Azure Database for PostgreSQL using an Entra token, so no database password exists.
///
/// The host owns the endpoint. Nothing a caller sends can redirect where rows land, which is what stops a
/// request, or a model acting on one, from pointing a data copy at a database of its choosing.
/// </summary>
public sealed class PostgresDataMigrationGateway(
    string host,
    string database,
    string user,
    TokenCredential credential) : IDataMigrationGateway
{
    private static readonly string[] s_scope = ["https://ossrdbms-aad.database.windows.net/.default"];

    // An object that already exists is not an error here: the phase is meant to be safe to re-run.
    private static readonly HashSet<string> s_alreadyPresent =
        ["42P07", "42P06", "42710", "42701", "42P16"];

    public async Task<SchemaDeploymentOutcome> PrepareAsync(
        IReadOnlyList<string> statements,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(statements);

        await using NpgsqlConnection connection = await ConnectAsync(cancellationToken).ConfigureAwait(false);

        int applied = 0;
        int alreadyPresent = 0;
        List<string> failures = [];

        foreach (string statement in statements)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                await using NpgsqlCommand command = new(statement, connection);
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                applied++;
            }
            catch (PostgresException exception) when (s_alreadyPresent.Contains(exception.SqlState))
            {
                alreadyPresent++;
            }
            catch (PostgresException exception)
            {
                failures.Add($"{exception.SqlState} {exception.MessageText}");
            }
        }

        return new SchemaDeploymentOutcome(applied, alreadyPresent, failures);
    }

    public async Task<IReadOnlyList<TableRowCount>> CountAsync(
        IReadOnlyList<string> tables,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tables);

        await using NpgsqlConnection connection = await ConnectAsync(cancellationToken).ConfigureAwait(false);

        List<TableRowCount> counts = [];
        foreach (string table in tables)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                await using NpgsqlCommand command = new($"select count(*) from {QuoteIdentifier(table)}", connection);
                object? scalar = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
                counts.Add(new TableRowCount(table, Convert.ToInt64(scalar ?? 0L, CultureInfo.InvariantCulture)));
            }
            catch (PostgresException)
            {
                // A table that is not there is a difference, not a crash; -1 distinguishes it from empty.
                counts.Add(new TableRowCount(table, -1));
            }
        }

        return counts;
    }

    public async Task<DataMigrationOutcome> ApplyAsync(
        IReadOnlyList<DataMigrationStatement> statements,
        IReadOnlyList<string> tables,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(statements);
        ArgumentNullException.ThrowIfNull(tables);

        await using NpgsqlConnection connection = await ConnectAsync(cancellationToken).ConfigureAwait(false);

        int executed = 0;
        int alreadyPresent = 0;
        List<string> failures = [];

        foreach (DataMigrationStatement statement in statements)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                await using NpgsqlCommand command = new(statement.Sql, connection);
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                executed++;
            }
            catch (PostgresException exception) when (exception.SqlState == "23505")
            {
                // The target already holds this key. Re-running a sandbox load is expected, so this is
                // reported apart from both success and failure rather than counted as either.
                alreadyPresent++;
            }
            catch (PostgresException exception)
            {
                // Recorded and carried on, so one bad row does not hide the state of every other table.
                failures.Add($"{statement.Table}: {exception.SqlState} {exception.MessageText}");
            }
        }

        List<TableRowCount> counts = [];
        foreach (string table in tables)
        {
            try
            {
                await using NpgsqlCommand command = new($"select count(*) from {QuoteIdentifier(table)}", connection);
                object? scalar = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
                counts.Add(new TableRowCount(table, Convert.ToInt64(scalar ?? 0L, CultureInfo.InvariantCulture)));
            }
            catch (PostgresException exception)
            {
                failures.Add($"{table}: count failed, {exception.SqlState} {exception.MessageText}");
            }
        }

        return new DataMigrationOutcome(executed, failures.Count, failures, counts) { RowsAlreadyPresent = alreadyPresent };
    }

    /// <summary>Quotes a table name so it is read as an identifier rather than parsed as SQL.</summary>
    private static string QuoteIdentifier(string identifier) =>
        "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    private async Task<NpgsqlConnection> ConnectAsync(CancellationToken cancellationToken)
    {
        AccessToken token = await credential
            .GetTokenAsync(new TokenRequestContext(s_scope), cancellationToken)
            .ConfigureAwait(false);

        NpgsqlConnectionStringBuilder builder = new()
        {
            Host = host,
            Port = 5432,
            Database = database,
            Username = user,
            Password = token.Token,
            SslMode = SslMode.Require,
            Timeout = 30,
        };

        NpgsqlConnection connection = new(builder.ConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }
}
