// Copyright (c) Microsoft. All rights reserved.

using System.Text.RegularExpressions;

namespace OracleFormsMigrationFleet.Hosting;

/// <summary>
/// The ordered schema migrations for the isolated platform schema.
///
/// Migrations are additive, idempotent, and applied in order under an advisory lock, and each one is
/// recorded in a ledger so a replay is a no-op rather than a second attempt. Nothing here drops or
/// rewrites an object: a migration that can destroy state is a migration that can destroy an audit
/// trail, and the approvals in this schema are the audit trail.
///
/// The schema name is configurable but always an identifier this code validated. It is interpolated
/// into DDL because PostgreSQL has no parameter form for an identifier, which makes validating it the
/// only thing standing between configuration and injected SQL.
/// </summary>
public static partial class PlatformSchema
{
    public const string DefaultSchema = "ofm_platform";

    public const int CurrentVersion = 2;

    /// <summary>
    /// Lock key for <c>pg_advisory_lock</c>. Two replicas starting together must not both run V001;
    /// one applies it and the other waits and then finds the ledger already satisfied.
    /// </summary>
    public const long AdvisoryLockKey = 7_265_991_004_001L;

    [GeneratedRegex("^[a-z_][a-z0-9_]{0,62}$")]
    private static partial Regex IdentifierPattern();

    /// <summary>Accepts only a lowercase unquoted identifier, so a schema name can never carry SQL.</summary>
    public static bool IsValidSchemaName(string? schema) =>
        !string.IsNullOrEmpty(schema) && IdentifierPattern().IsMatch(schema);

    public static string ResolveSchemaName(string? configured)
    {
        string schema = string.IsNullOrWhiteSpace(configured) ? DefaultSchema : configured.Trim();
        return IsValidSchemaName(schema)
            ? schema
            : throw new InvalidOperationException(
                $"'{schema}' is not a valid platform schema name. Use lowercase letters, digits, and underscores.");
    }

    /// <summary>One ordered migration. Statements run in sequence inside the migration transaction.</summary>
    public sealed record Migration(int Version, string Name, IReadOnlyList<string> Statements);

    /// <summary>The ledger DDL, applied before any migration so a replay can be detected.</summary>
    public static string LedgerStatement(string schema) =>
        $"""
        create schema if not exists {schema};
        create table if not exists {schema}.schema_version (
            version integer primary key,
            name text not null,
            applied_utc timestamptz not null default now()
        );
        """;

    public static string AppliedVersionsQuery(string schema) =>
        $"select version from {schema}.schema_version order by version";

    public static string RecordVersionStatement(string schema) =>
        $"insert into {schema}.schema_version (version, name) values (@version, @name) on conflict (version) do nothing";

    public static void ValidateAppliedVersions(IEnumerable<int> versions)
    {
        int newest = versions.DefaultIfEmpty(0).Max();
        if (newest > CurrentVersion)
        {
            throw new InvalidOperationException(
                $"The platform state schema is version {newest}, but this build supports only version {CurrentVersion}. Refusing to run an older binary against newer authorization records.");
        }
    }

    /// <summary>Every migration, in the order they must be applied.</summary>
    public static IReadOnlyList<Migration> Migrations(string schema)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(schema);
        if (!IsValidSchemaName(schema))
        {
            throw new ArgumentException("The platform schema name is not a valid identifier.", nameof(schema));
        }

        return
        [
            new Migration(1, "initial-platform-state",
            [
                $"""
                create table if not exists {schema}.organization (
                    organization_id text primary key,
                    tenant_id text not null unique,
                    display_name text not null,
                    created_utc timestamptz not null,
                    version integer not null default 1
                )
                """,
                $"""
                create table if not exists {schema}.project (
                    project_id text primary key,
                    organization_id text not null references {schema}.organization (organization_id),
                    tenant_id text not null,
                    name text not null,
                    created_utc timestamptz not null,
                    version integer not null default 1
                )
                """,
                $"""
                create table if not exists {schema}.membership (
                    project_id text not null references {schema}.project (project_id),
                    tenant_id text not null,
                    object_id text not null,
                    roles text[] not null,
                    created_utc timestamptz not null,
                    removed_utc timestamptz,
                    version integer not null default 1,
                    primary key (project_id, object_id)
                )
                """,
                $"""
                create table if not exists {schema}.target_profile (
                    project_id text not null references {schema}.project (project_id),
                    target_profile_id text not null,
                    version integer not null,
                    tenant_id text not null,
                    azure_tenant_id text not null,
                    subscription_id text not null,
                    resource_group text not null,
                    resource_id text not null,
                    region text not null,
                    endpoint_host text not null,
                    database_name text not null,
                    schema_name text not null,
                    execution_identity text not null,
                    environment_name text not null,
                    stack_database text not null,
                    stack_front_end text not null,
                    stack_back_end text not null,
                    canonical_hash text not null,
                    created_utc timestamptz not null,
                    primary key (project_id, target_profile_id, version)
                )
                """,
                $"""
                create table if not exists {schema}.approval (
                    approval_id text primary key,
                    project_id text not null references {schema}.project (project_id),
                    tenant_id text not null,
                    requested_by_object_id text not null,
                    requested_utc timestamptz not null,
                    state text not null,
                    scope text not null,
                    required_role text not null,
                    engagement_id text not null,
                    source_snapshot_hash text not null,
                    plan_input_hash text not null,
                    target_profile_id text not null,
                    target_profile_version integer not null,
                    target_profile_hash text not null,
                    expires_utc timestamptz not null,
                    request_notes text,
                    decided_by_object_id text,
                    decided_utc timestamptz,
                    decision_notes text,
                    revoked_by_object_id text,
                    revoked_utc timestamptz,
                    revocation_notes text,
                    version integer not null default 1
                )
                """,
                $"""
                create table if not exists {schema}.approval_event (
                    event_id bigserial primary key,
                    approval_id text not null references {schema}.approval (approval_id),
                    tenant_id text not null,
                    actor_object_id text not null,
                    action text not null,
                    recorded_utc timestamptz not null,
                    notes text
                )
                """,
                $"create index if not exists ix_membership_actor on {schema}.membership (tenant_id, object_id)",
                $"create index if not exists ix_approval_project on {schema}.approval (tenant_id, project_id)",
                $"create index if not exists ix_approval_requester on {schema}.approval (tenant_id, requested_by_object_id)",
            ]),
            new Migration(2, "single-sandbox-project-boundary",
            [
                $"""
                create table if not exists {schema}.sandbox_project_binding (
                    tenant_id text primary key,
                    project_id text not null references {schema}.project (project_id),
                    bound_utc timestamptz not null
                )
                """,
                $"""
                insert into {schema}.sandbox_project_binding (tenant_id, project_id, bound_utc)
                select distinct on (tenant_id) tenant_id, project_id, requested_utc
                from {schema}.approval
                where scope = 'SandboxDatabaseWrite'
                order by tenant_id, requested_utc, approval_id
                on conflict (tenant_id) do nothing
                """,
            ]),
        ];
    }
}
