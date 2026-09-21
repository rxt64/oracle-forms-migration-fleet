// Copyright (c) Microsoft. All rights reserved.

using System.Xml.Linq;
using Azure.Core;
using Npgsql;
using OracleFormsMigrationFleet.Fleet.Execution;

namespace OracleFormsMigrationFleet.Hosting;

public sealed class PostgresTargetApplicationVerificationGateway : ITargetApplicationVerificationGateway
{
    private static readonly string[] s_scope = ["https://ossrdbms-aad.database.windows.net/.default"];
    private static readonly TimeSpan s_cleanupTimeout = TimeSpan.FromSeconds(30);
    private readonly Func<CancellationToken, Task<NpgsqlConnection>> _connectionFactory;
    private readonly TimeSpan _timeout;

    public PostgresTargetApplicationVerificationGateway(
        string host,
        string database,
        string user,
        TokenCredential credential)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        ArgumentException.ThrowIfNullOrWhiteSpace(database);
        ArgumentException.ThrowIfNullOrWhiteSpace(user);
        ArgumentNullException.ThrowIfNull(credential);
        _timeout = TimeSpan.FromMinutes(5);

        _connectionFactory = async cancellationToken =>
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
        };
    }

    internal PostgresTargetApplicationVerificationGateway(
        Func<CancellationToken, Task<NpgsqlConnection>> connectionFactory,
        TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(connectionFactory);
        _connectionFactory = connectionFactory;
        _timeout = timeout ?? TimeSpan.FromMinutes(5);
    }

    public async Task<ApplicationTestRun> VerifyAsync(
        string schemaPath,
        string reportDirectory,
        CancellationToken cancellationToken)
    {
        string command = "PostgreSQL disposable-schema execution";
        string schema = $"ofm_verify_{Guid.NewGuid():N}"[..27];
        string? failure = null;
        bool setupFailed = false;
        bool timedOut = false;
        bool schemaCreated = false;
        int tableCount = 0;

        using CancellationTokenSource timeout = new(_timeout);
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeout.Token);

        try
        {
            string ddl = await File.ReadAllTextAsync(schemaPath, linked.Token).ConfigureAwait(false);
            await using NpgsqlConnection connection = await _connectionFactory(linked.Token).ConfigureAwait(false);
            await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(linked.Token).ConfigureAwait(false);
            try
            {
                await ExecuteAsync(connection, $"create schema {schema}", linked.Token).ConfigureAwait(false);
                schemaCreated = true;
                await ExecuteAsync(connection, $"set local search_path to {schema};\n{ddl}", linked.Token).ConfigureAwait(false);
                await using NpgsqlCommand count = new(
                    "select count(*) from information_schema.tables where table_schema = @schema and table_type = 'BASE TABLE'",
                    connection);
                count.Parameters.AddWithValue("schema", schema);
                tableCount = Convert.ToInt32(
                    await count.ExecuteScalarAsync(linked.Token).ConfigureAwait(false),
                    System.Globalization.CultureInfo.InvariantCulture);
                if (tableCount == 0)
                {
                    failure = "Generated DDL executed but created no target tables.";
                }
            }
            catch (PostgresException exception)
            {
                failure = $"Generated DDL was rejected by PostgreSQL ({exception.SqlState}): {exception.MessageText}";
            }
            finally
            {
                try
                {
                    using CancellationTokenSource rollbackTimeout = new(s_cleanupTimeout);
                    await transaction.RollbackAsync(rollbackTimeout.Token).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is NpgsqlException or InvalidOperationException or OperationCanceledException)
                {
                    failure = $"Disposable target transaction rollback failed: {exception.Message}";
                }
            }
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            failure = $"Disposable target verification timed out after {_timeout.TotalSeconds:0} seconds.";
            timedOut = true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NpgsqlException)
        {
            failure = $"Disposable target setup failed: {exception.Message}";
            setupFailed = true;
        }
        finally
        {
            if (schemaCreated)
            {
                try
                {
                    using CancellationTokenSource cleanupTimeout = new(s_cleanupTimeout);
                    await using NpgsqlConnection cleanup = await _connectionFactory(cleanupTimeout.Token).ConfigureAwait(false);
                    await ExecuteAsync(cleanup, $"drop schema if exists {schema} cascade", cleanupTimeout.Token)
                        .ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is NpgsqlException or InvalidOperationException or OperationCanceledException)
                {
                    failure = $"Disposable target cleanup failed: {exception.Message}";
                    setupFailed = false;
                    timedOut = false;
                }
            }
        }

        Directory.CreateDirectory(reportDirectory);
        XElement suite = new(
            "testsuite",
            new XAttribute("name", "generated-target-database"),
            new XAttribute("tests", "1"),
            new XAttribute("failures", failure is null ? "0" : "1"),
            new XAttribute("errors", "0"),
            new XAttribute("skipped", "0"),
            new XElement(
                "testcase",
                new XAttribute("name", "generated_schema_executes_in_disposable_postgresql"),
                failure is null ? null : new XElement("failure", new XAttribute("message", failure))));
        new XDocument(suite).Save(Path.Combine(reportDirectory, "target-database.xml"));

        return new ApplicationTestRun(
            command,
            ToolAvailable: true,
            TimedOut: timedOut,
            ExitCode: failure is null ? 0 : 1,
            Output: failure ?? $"Generated schema created {tableCount} table(s) in a disposable PostgreSQL schema and was removed.",
            SetupFailed: setupFailed);
    }

    private static async Task ExecuteAsync(
        NpgsqlConnection connection,
        string sql,
        CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = new(sql, connection);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}