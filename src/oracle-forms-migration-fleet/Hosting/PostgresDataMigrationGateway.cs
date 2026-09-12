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

    public async Task<DataMigrationOutcome> ApplyAsync(
        IReadOnlyList<DataMigrationStatement> statements,
        IReadOnlyList<string> tables,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(statements);
        ArgumentNullException.ThrowIfNull(tables);

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

        await using NpgsqlConnection connection = new(builder.ConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        int executed = 0;
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

        return new DataMigrationOutcome(executed, failures.Count, failures, counts);
    }

    /// <summary>Quotes a table name so it is read as an identifier rather than parsed as SQL.</summary>
    private static string QuoteIdentifier(string identifier) =>
        "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
}
