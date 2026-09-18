// Copyright (c) Microsoft. All rights reserved.

using System.Text.RegularExpressions;
using OracleFormsMigrationFleet.Hosting;

namespace OracleFormsMigrationFleet.Tests;

/// <summary>
/// The PostgreSQL contract: the migration text, the ledger, and the statements the adapter issues.
///
/// **These tests do not connect to a database.** They assert the properties that can be established
/// offline — ordering, idempotence, isolation to the configured schema, absence of destructive DDL, and
/// agreement between each statement's parameters and the columns it names. Whether the managed identity
/// can actually reach the server, holds CREATE on the schema, and passes through the firewall is live
/// qualification that has not been performed and is recorded as outstanding in the 004 verification notes.
/// </summary>
public class PlatformPostgresContractTests
{
    private const string Schema = PlatformSchema.DefaultSchema;

    private static IReadOnlyList<string> AllStatements(string schema) =>
    [
        PlatformSchema.LedgerStatement(schema),
        .. PlatformSchema.Migrations(schema).SelectMany(migration => migration.Statements),
    ];

    [Fact]
    public void Migrations_are_ordered_from_one_with_no_gaps_and_no_repeats()
    {
        IReadOnlyList<PlatformSchema.Migration> migrations = PlatformSchema.Migrations(Schema);

        Assert.NotEmpty(migrations);
        Assert.Equal(Enumerable.Range(1, migrations.Count), migrations.Select(migration => migration.Version));
        Assert.Equal(migrations.Count, migrations.Select(migration => migration.Name).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(PlatformSchema.CurrentVersion, migrations[^1].Version);
    }

    /// <summary>
    /// Every statement is safe to replay. The ledger stops a second attempt in the normal case, but a
    /// migration that is only correct because the ledger was readable is one that fails badly when it is
    /// not.
    /// </summary>
    [Fact]
    public void Every_migration_statement_is_idempotent()
    {
        foreach (string statement in AllStatements(Schema))
        {
            Assert.True(
                statement.Contains("if not exists", StringComparison.OrdinalIgnoreCase)
                || statement.Contains("on conflict", StringComparison.OrdinalIgnoreCase),
                $"Statement is not replay-safe:\n{statement}");
        }
    }

    [Theory]
    [InlineData("drop ")]
    [InlineData("truncate ")]
    [InlineData("delete ")]
    [InlineData("alter table")]
    public void No_migration_statement_can_destroy_a_recorded_decision(string forbidden)
    {
        foreach (string statement in AllStatements(Schema))
        {
            Assert.DoesNotContain(forbidden, statement, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void Every_object_is_created_inside_the_configured_schema()
    {
        const string schema = "ofm_platform_test";

        foreach (string statement in AllStatements(schema))
        {
            foreach (Match match in Regex.Matches(
                statement,
                @"(?:create table if not exists|references|insert into|update|from)\s+([a-z_][a-z0-9_]*)\.",
                RegexOptions.IgnoreCase))
            {
                Assert.Equal(schema, match.Groups[1].Value);
            }
        }

        Assert.DoesNotContain(PlatformSchema.DefaultSchema + ".", string.Join("\n", AllStatements(schema)), StringComparison.Ordinal);
    }

    [Fact]
    public void The_ledger_is_created_before_anything_reads_it_and_records_each_applied_version()
    {
        string ledger = PlatformSchema.LedgerStatement(Schema);

        Assert.Contains($"create schema if not exists {Schema}", ledger, StringComparison.Ordinal);
        Assert.Contains($"create table if not exists {Schema}.schema_version", ledger, StringComparison.Ordinal);
        Assert.Contains("version integer primary key", ledger, StringComparison.Ordinal);

        Assert.Contains($"from {Schema}.schema_version", PlatformSchema.AppliedVersionsQuery(Schema), StringComparison.Ordinal);
        Assert.Contains("order by version", PlatformSchema.AppliedVersionsQuery(Schema), StringComparison.Ordinal);
        Assert.Contains("on conflict (version) do nothing", PlatformSchema.RecordVersionStatement(Schema), StringComparison.Ordinal);
    }

    [Fact]
    public void Version_one_creates_every_table_the_adapter_reads()
    {
        string migration = string.Join("\n", PlatformSchema.Migrations(Schema)[0].Statements);

        foreach (string table in new[] { "organization", "project", "membership", "target_profile", "approval", "approval_event" })
        {
            Assert.Contains($"create table if not exists {Schema}.{table}", migration, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("ofm_platform")]
    [InlineData("ofm_platform_dev")]
    [InlineData("a")]
    public void A_valid_schema_name_is_accepted(string schema) =>
        Assert.True(PlatformSchema.IsValidSchemaName(schema));

    [Theory]
    [InlineData("OFM_Platform")]
    [InlineData("public; drop table approval")]
    [InlineData("ofm-platform")]
    [InlineData("1platform")]
    [InlineData("\"ofm\"")]
    public void A_schema_name_that_could_carry_sql_is_refused(string schema)
    {
        Assert.False(PlatformSchema.IsValidSchemaName(schema));
        Assert.Throws<InvalidOperationException>(() => PlatformSchema.ResolveSchemaName(schema));
    }

    [Fact]
    public void An_empty_schema_name_is_not_an_identifier_and_resolves_to_the_default()
    {
        Assert.False(PlatformSchema.IsValidSchemaName(string.Empty));
        Assert.Equal(PlatformSchema.DefaultSchema, PlatformSchema.ResolveSchemaName(string.Empty));
    }

    [Fact]
    public void An_unset_schema_name_resolves_to_the_isolated_default()
    {
        Assert.Equal(PlatformSchema.DefaultSchema, PlatformSchema.ResolveSchemaName(null));
        Assert.Equal(PlatformSchema.DefaultSchema, PlatformSchema.ResolveSchemaName("  "));
        Assert.NotEqual("public", PlatformSchema.DefaultSchema);
    }

    [Fact]
    public void A_binary_refuses_a_platform_schema_created_by_a_newer_version()
    {
        Assert.Throws<InvalidOperationException>(() =>
            PlatformSchema.ValidateAppliedVersions([PlatformSchema.CurrentVersion + 1]));
        PlatformSchema.ValidateAppliedVersions([PlatformSchema.CurrentVersion]);
    }

    [Fact]
    public void A_concurrent_first_member_insert_conflicts_instead_of_overwriting_roles()
    {
        string sql = PostgresPlatformStateStore.MembershipUpsertSql(Schema);

        Assert.Contains("on conflict (project_id, object_id) do nothing", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("do update", sql, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Every column an insert names must have a value bound to it and no more. A mismatch here is the
    /// defect class that presents as a silent null in a persisted authorization binding.
    /// </summary>
    [Theory]
    [InlineData("approval")]
    [InlineData("target_profile")]
    [InlineData("approval_event")]
    public void Each_insert_binds_exactly_one_parameter_per_column(string table)
    {
        string sql = table switch
        {
            "approval" => PostgresPlatformStateStore.ApprovalInsertSql(Schema),
            "target_profile" => PostgresPlatformStateStore.TargetProfileInsertSql(Schema),
            _ => PostgresPlatformStateStore.ApprovalEventInsertSql(Schema),
        };

        Match columns = Regex.Match(sql, @"insert into [a-z_.]+\s*\(([^)]*)\)", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        Match values = Regex.Match(sql, @"values\s*\(([^)]*)\)", RegexOptions.IgnoreCase | RegexOptions.Singleline);

        Assert.True(columns.Success && values.Success, sql);

        int columnCount = columns.Groups[1].Value.Split(',').Length;
        string[] parameters = [.. Regex.Matches(values.Groups[1].Value, "@[A-Za-z]+").Select(match => match.Value)];

        Assert.Equal(columnCount, parameters.Length);
        Assert.Equal(parameters.Length, parameters.Distinct(StringComparer.Ordinal).Count());
    }

    /// <summary>
    /// Optimistic concurrency is in the SQL, not only in the adapter. An update that matched on identity
    /// alone would let a second decider overwrite the first one's answer.
    /// </summary>
    [Fact]
    public void The_approval_update_is_guarded_by_the_version_the_caller_read()
    {
        string sql = PostgresPlatformStateStore.ApprovalUpdateSql(Schema);

        Assert.Contains("version = version + 1", sql, StringComparison.Ordinal);
        Assert.Contains("and version = @expected", sql, StringComparison.Ordinal);
        Assert.Contains("tenant_id = @tenant", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_read_is_scoped_to_one_tenant()
    {
        foreach (string sql in new[]
        {
            PostgresPlatformStateStore.ApprovalUpdateSql(Schema),
            PostgresPlatformStateStore.MembershipVersionedUpdateSql(Schema),
        })
        {
            Assert.Contains("@tenant", sql, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Platform_database_configuration_reports_exactly_what_is_missing()
    {
        Assert.False(PlatformDatabaseOptions.TryRead(_ => null, out _, out string error));
        Assert.Contains(PlatformDatabaseOptions.HostVariable, error, StringComparison.Ordinal);

        Assert.True(PlatformDatabaseOptions.TryRead(
            name => name switch
            {
                PlatformDatabaseOptions.HostVariable => "pg.postgres.database.azure.com",
                PlatformDatabaseOptions.DatabaseVariable => "ofm",
                PlatformDatabaseOptions.UserVariable => "id-ofmfleet-web-dev",
                _ => null,
            },
            out PlatformDatabaseOptions? options,
            out _));

        Assert.Equal(PlatformSchema.DefaultSchema, options!.Schema);

        Assert.False(PlatformDatabaseOptions.TryRead(
            name => name switch
            {
                PlatformDatabaseOptions.HostVariable => "pg.postgres.database.azure.com",
                PlatformDatabaseOptions.DatabaseVariable => "ofm",
                PlatformDatabaseOptions.UserVariable => "id-ofmfleet-web-dev",
                PlatformDatabaseOptions.SchemaVariable => "public; drop table approval",
                _ => null,
            },
            out _,
            out string schemaError));

        Assert.Contains(PlatformDatabaseOptions.SchemaVariable, schemaError, StringComparison.Ordinal);
    }

    /// <summary>
    /// Nothing in the configuration record is a credential, and there is no property that could hold one.
    /// The only secret in the connection is the managed-identity token, acquired per connection.
    /// </summary>
    [Fact]
    public void The_platform_database_configuration_has_nowhere_to_put_a_password()
    {
        string[] properties = [.. typeof(PlatformDatabaseOptions)
            .GetProperties()
            .Select(property => property.Name)];

        Assert.DoesNotContain(properties, name =>
            name.Contains("password", StringComparison.OrdinalIgnoreCase)
            || name.Contains("secret", StringComparison.OrdinalIgnoreCase)
            || name.Contains("connectionstring", StringComparison.OrdinalIgnoreCase));
    }
}
