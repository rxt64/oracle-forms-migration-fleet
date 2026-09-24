// Copyright (c) Microsoft. All rights reserved.

using System.Globalization;
using Azure.Core;
using Npgsql;
using OracleFormsMigrationFleet.Fleet;
using OracleFormsMigrationFleet.Fleet.Execution;

namespace OracleFormsMigrationFleet.Hosting;

/// <summary>
/// Reads the project's approved PostgreSQL target, read-only, to answer per-entry verification cases.
///
/// Nothing generated is executed here and nothing generated is given a connection. The statements below
/// are the complete set this class can issue: four parameterized catalog probes written in this build.
/// Every identifier a case names travels as a query parameter and is never interpolated, so a hostile
/// source tree that reached a table name has reached a string comparison in <c>pg_catalog</c> and nothing
/// else.
///
/// The session is put into read-only mode before any probe runs, and the probes execute inside a
/// transaction that inherits it. A statement that tried to change the target would be refused by the
/// server, not merely absent from this file — which is the difference between a sandbox and an intention.
///
/// It never builds a target of its own. The schema it reads is the one the sandbox data migration landed
/// in, named by the approved target profile; a schema this process created by running generated DDL under
/// its own privileges would show only that the DDL parses.
/// </summary>
public sealed class PostgresEntryRuntimeVerificationGateway : IEntryRuntimeVerificationGateway
{
    private static readonly string[] s_scope = ["https://ossrdbms-aad.database.windows.net/.default"];

    /// <summary>The complete session preamble. Read-only first, so nothing after it can write.</summary>
    private const string ReadOnlySession =
        "set session characteristics as transaction read only; " +
        "set statement_timeout = '30s'; " +
        "set idle_in_transaction_session_timeout = '60s'";

    private readonly Func<CancellationToken, Task<NpgsqlConnection>> _connectionFactory;
    private readonly TimeSpan _timeout;

    public PostgresEntryRuntimeVerificationGateway(
        string host,
        string database,
        string schema,
        string user,
        TokenCredential credential)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        ArgumentException.ThrowIfNullOrWhiteSpace(database);
        ArgumentException.ThrowIfNullOrWhiteSpace(schema);
        ArgumentException.ThrowIfNullOrWhiteSpace(user);
        ArgumentNullException.ThrowIfNull(credential);

        _timeout = TimeSpan.FromMinutes(5);
        Target = new EntryVerificationTargetBinding(host, database, schema, user);

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

    internal PostgresEntryRuntimeVerificationGateway(
        Func<CancellationToken, Task<NpgsqlConnection>> connectionFactory,
        EntryVerificationTargetBinding target,
        TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(connectionFactory);
        ArgumentNullException.ThrowIfNull(target);
        _connectionFactory = connectionFactory;
        _timeout = timeout ?? TimeSpan.FromMinutes(5);
        Target = target;
    }

    public EntryVerificationTargetBinding Target { get; }

    public async Task<EntryRuntimeVerificationRun> InspectAsync(
        EntryVerificationTargetBinding approved,
        IReadOnlyList<EntryVerificationExpectation> expectations,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(approved);
        ArgumentNullException.ThrowIfNull(expectations);

        const string command = "PostgreSQL read-only catalog inspection of the approved target";

        if (!approved.SameTargetAs(Target))
        {
            return new EntryRuntimeVerificationRun(
                command, ToolAvailable: true, TimedOut: false, SetupFailed: true,
                $"The approved target profile names {approved.Describe()} and this verifier is wired to {Target.Describe()}. " +
                "Nothing was read.",
                []);
        }

        if (!EntryVerificationCoverage.IsVerifiableIdentifier(approved.SchemaName))
        {
            return new EntryRuntimeVerificationRun(
                command, ToolAvailable: true, TimedOut: false, SetupFailed: true,
                "The approved target profile names a schema this verifier will not ask a catalog about, so nothing was read.",
                []);
        }

        List<EntryVerificationObservation> observations = [];

        using CancellationTokenSource timeout = new(_timeout);
        using CancellationTokenSource linked =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);

        try
        {
            await using NpgsqlConnection connection = await _connectionFactory(linked.Token).ConfigureAwait(false);
            await using (NpgsqlCommand preamble = new(ReadOnlySession, connection))
            {
                await preamble.ExecuteNonQueryAsync(linked.Token).ConfigureAwait(false);
            }

            await using NpgsqlTransaction transaction =
                await connection.BeginTransactionAsync(linked.Token).ConfigureAwait(false);

            foreach (EntryVerificationExpectation expectation in expectations)
            {
                linked.Token.ThrowIfCancellationRequested();
                observations.Add(await ObserveAsync(connection, approved, expectation, linked.Token).ConfigureAwait(false));
            }

            await transaction.RollbackAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            return new EntryRuntimeVerificationRun(
                command, ToolAvailable: true, TimedOut: true, SetupFailed: false,
                $"Reading the approved target timed out after {_timeout.TotalSeconds.ToString("0", CultureInfo.InvariantCulture)} seconds.",
                []);
        }
        catch (Exception exception) when (exception is NpgsqlException or InvalidOperationException)
        {
            return new EntryRuntimeVerificationRun(
                command, ToolAvailable: true, TimedOut: false, SetupFailed: true,
                $"The approved target could not be read: {exception.Message}", []);
        }

        return new EntryRuntimeVerificationRun(
            command, ToolAvailable: true, TimedOut: false, SetupFailed: false, Failure: null, observations);
    }

    private static async Task<EntryVerificationObservation> ObserveAsync(
        NpgsqlConnection connection,
        EntryVerificationTargetBinding approved,
        EntryVerificationExpectation expectation,
        CancellationToken cancellationToken)
    {
        if (!EntryVerificationCoverage.IsVerifiableIdentifier(expectation.Table) ||
            (expectation.Column is { Length: > 0 } named && !EntryVerificationCoverage.IsVerifiableIdentifier(named)))
        {
            return new EntryVerificationObservation(
                expectation.EntryId, expectation.TestId, DispositionVerificationStatus.NotExecuted, null,
                "The target object is named in a way this verifier will not ask a catalog about, so nothing was read.");
        }

        try
        {
            return expectation.Kind switch
            {
                EntryVerificationCaseKind.TargetTableExists =>
                    await TableExistsAsync(connection, approved, expectation, cancellationToken).ConfigureAwait(false),
                EntryVerificationCaseKind.TargetTableReadable =>
                    await TableReadableAsync(connection, approved, expectation, cancellationToken).ConfigureAwait(false),
                EntryVerificationCaseKind.TargetColumnExists =>
                    await ColumnExistsAsync(connection, approved, expectation, cancellationToken).ConfigureAwait(false),
                EntryVerificationCaseKind.TargetColumnShape =>
                    await ColumnShapeAsync(connection, approved, expectation, cancellationToken).ConfigureAwait(false),
                _ => new EntryVerificationObservation(
                    expectation.EntryId, expectation.TestId, DispositionVerificationStatus.NotExecuted, null,
                    "That case kind is not one this verifier reads."),
            };
        }
        catch (PostgresException exception)
        {
            return new EntryVerificationObservation(
                expectation.EntryId, expectation.TestId, DispositionVerificationStatus.Failed, null,
                $"The target refused the read ({exception.SqlState}): {exception.MessageText}");
        }
    }

    private static async Task<EntryVerificationObservation> TableExistsAsync(
        NpgsqlConnection connection,
        EntryVerificationTargetBinding approved,
        EntryVerificationExpectation expectation,
        CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = new(
            "select count(*) from pg_catalog.pg_class c " +
            "join pg_catalog.pg_namespace n on n.oid = c.relnamespace " +
            "where n.nspname = @schema and c.relname = @table and c.relkind in ('r', 'p')",
            connection);
        command.Parameters.AddWithValue("schema", approved.SchemaName);
        command.Parameters.AddWithValue("table", expectation.Table);

        bool present = Convert.ToInt32(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            CultureInfo.InvariantCulture) == 1;

        return new EntryVerificationObservation(
            expectation.EntryId,
            expectation.TestId,
            present ? DispositionVerificationStatus.Passed : DispositionVerificationStatus.Failed,
            present ? expectation.Table : null,
            present
                ? $"Table '{expectation.Table}' exists in the approved target."
                : $"The approved target holds no table '{expectation.Table}'.");
    }

    private static async Task<EntryVerificationObservation> TableReadableAsync(
        NpgsqlConnection connection,
        EntryVerificationTargetBinding approved,
        EntryVerificationExpectation expectation,
        CancellationToken cancellationToken)
    {
        // A relation the execution identity cannot select from is not one the migrated application could
        // read, whatever the catalog says about its existence. to_regclass resolves a name supplied as a
        // parameter, so this asks about the table without ever naming it in SQL text.
        await using NpgsqlCommand command = new(
            "select has_table_privilege(@identity, to_regclass(@qualified), 'SELECT') " +
            "from pg_catalog.pg_class c " +
            "join pg_catalog.pg_namespace n on n.oid = c.relnamespace " +
            "where n.nspname = @schema and c.relname = @table",
            connection);
        command.Parameters.AddWithValue("identity", approved.ExecutionIdentity);
        command.Parameters.AddWithValue("qualified", $"{approved.SchemaName}.{expectation.Table}");
        command.Parameters.AddWithValue("schema", approved.SchemaName);
        command.Parameters.AddWithValue("table", expectation.Table);

        object? value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        bool readable = value is bool granted && granted;

        return new EntryVerificationObservation(
            expectation.EntryId,
            expectation.TestId,
            readable ? DispositionVerificationStatus.Passed : DispositionVerificationStatus.Failed,
            readable ? "readable" : null,
            readable
                ? $"The run's execution identity may select from '{expectation.Table}' in the approved target."
                : $"The run's execution identity cannot select from '{expectation.Table}' in the approved target, or the " +
                  "relation is not there to select from.");
    }

    private static async Task<EntryVerificationObservation> ColumnExistsAsync(
        NpgsqlConnection connection,
        EntryVerificationTargetBinding approved,
        EntryVerificationExpectation expectation,
        CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = new(
            "select count(*) from pg_catalog.pg_attribute a " +
            "join pg_catalog.pg_class c on c.oid = a.attrelid " +
            "join pg_catalog.pg_namespace n on n.oid = c.relnamespace " +
            "where n.nspname = @schema and c.relname = @table and a.attname = @column " +
            "and a.attnum > 0 and not a.attisdropped",
            connection);
        command.Parameters.AddWithValue("schema", approved.SchemaName);
        command.Parameters.AddWithValue("table", expectation.Table);
        command.Parameters.AddWithValue("column", expectation.Column ?? string.Empty);

        bool present = Convert.ToInt32(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            CultureInfo.InvariantCulture) == 1;

        return new EntryVerificationObservation(
            expectation.EntryId,
            expectation.TestId,
            present ? DispositionVerificationStatus.Passed : DispositionVerificationStatus.Failed,
            present ? $"{expectation.Table}.{expectation.Column}" : null,
            present
                ? $"Column '{expectation.Table}.{expectation.Column}' exists in the approved target."
                : $"The approved target holds no column '{expectation.Table}.{expectation.Column}'.");
    }

    private static async Task<EntryVerificationObservation> ColumnShapeAsync(
        NpgsqlConnection connection,
        EntryVerificationTargetBinding approved,
        EntryVerificationExpectation expectation,
        CancellationToken cancellationToken)
    {
        // format_type spells the type the way the server holds it, including length and scale, so a
        // narrowed column or a lost scale is a difference this reads rather than one it rounds away.
        await using NpgsqlCommand command = new(
            "select format_type(a.atttypid, a.atttypmod) from pg_catalog.pg_attribute a " +
            "join pg_catalog.pg_class c on c.oid = a.attrelid " +
            "join pg_catalog.pg_namespace n on n.oid = c.relnamespace " +
            "where n.nspname = @schema and c.relname = @table and a.attname = @column " +
            "and a.attnum > 0 and not a.attisdropped",
            connection);
        command.Parameters.AddWithValue("schema", approved.SchemaName);
        command.Parameters.AddWithValue("table", expectation.Table);
        command.Parameters.AddWithValue("column", expectation.Column ?? string.Empty);

        object? value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

        if (value is not string declared)
        {
            return new EntryVerificationObservation(
                expectation.EntryId, expectation.TestId, DispositionVerificationStatus.Failed, null,
                $"The approved target holds no column '{expectation.Table}.{expectation.Column}', so its shape could not be read.");
        }

        string actual = EntryVerificationCoverage.CanonicalType(declared);
        bool matches = string.Equals(actual, expectation.Expected, StringComparison.Ordinal);

        return new EntryVerificationObservation(
            expectation.EntryId,
            expectation.TestId,
            matches ? DispositionVerificationStatus.Passed : DispositionVerificationStatus.Failed,
            actual,
            matches
                ? $"Column '{expectation.Table}.{expectation.Column}' is '{actual}' in the approved target, which is the shape the source column resolved to."
                : $"Column '{expectation.Table}.{expectation.Column}' is '{actual}' in the approved target; the source column resolved to '{expectation.Expected}'.");
    }
}
