#!/bin/sh
set -eu

: "${ADMIN_CLIENT_ID:?ADMIN_CLIENT_ID is required}"
: "${TARGET_CLIENT_ID:?TARGET_CLIENT_ID is required}"
: "${PGHOST:?PGHOST is required}"
: "${ADMIN_USER:?ADMIN_USER is required}"
: "${TARGET_USER:?TARGET_USER is required}"
: "${TARGET_DATABASE:?TARGET_DATABASE is required}"
: "${PLATFORM_DATABASE:=ofm_platform}"
: "${MIGRATION_USER:=}"
: "${MIGRATION_CLIENT_ID:=}"

if [ -n "$MIGRATION_USER" ] && [ -z "$MIGRATION_CLIENT_ID" ]; then
    echo "MIGRATION_CLIENT_ID is required when MIGRATION_USER is set." >&2
    exit 2
fi

case "$TARGET_DATABASE" in
    postgres|ofm_platform|azure_sys|azure_maintenance)
        echo "The target database name is reserved." >&2
        exit 2
        ;;
esac

if [ "$PLATFORM_DATABASE" != "ofm_platform" ]; then
    echo "PLATFORM_DATABASE must name the protected ofm_platform database." >&2
    exit 2
fi

get_token() {
    if [ -n "${IDENTITY_ENDPOINT:-}" ] && [ -n "${IDENTITY_HEADER:-}" ]; then
        token_response="$(wget -qO- \
            --header "X-IDENTITY-HEADER: $IDENTITY_HEADER" \
            "${IDENTITY_ENDPOINT}?resource=https%3A%2F%2Fossrdbms-aad.database.windows.net&api-version=2019-08-01&client_id=$1")"
    else
        token_response="$(wget -qO- \
            --header "Metadata: true" \
            "http://169.254.169.254/metadata/identity/oauth2/token?resource=https%3A%2F%2Fossrdbms-aad.database.windows.net&api-version=2018-02-01&client_id=$1")"
    fi
    printf '%s' "$token_response" | sed -n 's/.*"access_token":"\([^"]*\)".*/\1/p'
}

export PGSSLMODE=require
admin_token="$(get_token "$ADMIN_CLIENT_ID")"
test -n "$admin_token"
export PGPASSWORD="$admin_token"

role_exists="$(
    psql -h "$PGHOST" -U "$ADMIN_USER" -d postgres -tA -v ON_ERROR_STOP=1 \
    -v target_user="$TARGET_USER" <<'SQL'
select 1 from pg_roles where rolname = :'target_user';
SQL
)"
if [ "$role_exists" != "1" ]; then
    psql -h "$PGHOST" -U "$ADMIN_USER" -d postgres -v ON_ERROR_STOP=1 \
    -v target_user="$TARGET_USER" <<'SQL'
select * from pgaadauth_create_principal(:'target_user', false, false);
SQL
fi

if [ -n "$MIGRATION_USER" ]; then
    migration_role_exists="$(
        psql -h "$PGHOST" -U "$ADMIN_USER" -d postgres -tA -v ON_ERROR_STOP=1 \
            -v migration_user="$MIGRATION_USER" <<'SQL'
select 1 from pg_roles where rolname = :'migration_user';
SQL
    )"
    if [ "$migration_role_exists" != "1" ]; then
        psql -h "$PGHOST" -U "$ADMIN_USER" -d postgres -v ON_ERROR_STOP=1 \
            -v migration_user="$MIGRATION_USER" <<'SQL'
select * from pgaadauth_create_principal(:'migration_user', false, false);
SQL
    fi
fi

psql -h "$PGHOST" -U "$ADMIN_USER" -d postgres -v ON_ERROR_STOP=1 \
    -v platform_database="$PLATFORM_DATABASE" -v admin_user="$ADMIN_USER" <<'SQL'
revoke connect on database :"platform_database" from public;
grant connect on database :"platform_database" to :"admin_user";
SQL

if [ -n "$MIGRATION_USER" ]; then
    psql -h "$PGHOST" -U "$ADMIN_USER" -d postgres -v ON_ERROR_STOP=1 \
    -v platform_database="$PLATFORM_DATABASE" -v migration_user="$MIGRATION_USER" <<'SQL'
grant connect on database :"platform_database" to :"migration_user";
SQL
fi

database_owner="$(
    psql -h "$PGHOST" -U "$ADMIN_USER" -d postgres -tA -v ON_ERROR_STOP=1 \
    -v target_database="$TARGET_DATABASE" <<'SQL'
select pg_catalog.pg_get_userbyid(datdba) from pg_database where datname = :'target_database';
SQL
)"
if [ -z "$database_owner" ]; then
    psql -h "$PGHOST" -U "$ADMIN_USER" -d postgres -v ON_ERROR_STOP=1 \
        -v target_database="$TARGET_DATABASE" \
    -v admin_user="$ADMIN_USER" <<'SQL'
create database :"target_database" owner :"admin_user";
SQL
elif [ "$database_owner" != "$ADMIN_USER" ]; then
    echo "The target database exists with an unexpected owner." >&2
    exit 3
fi

psql -h "$PGHOST" -U "$ADMIN_USER" -d "$TARGET_DATABASE" -v ON_ERROR_STOP=1 \
    -v target_database="$TARGET_DATABASE" -v target_user="$TARGET_USER" <<'SQL'
revoke connect on database :"target_database" from public;
grant connect, temporary on database :"target_database" to :"target_user";
revoke create on schema public from public;
grant usage, create on schema public to :"target_user";
SQL

if [ -n "$MIGRATION_USER" ]; then
    psql -h "$PGHOST" -U "$ADMIN_USER" -d "$TARGET_DATABASE" -v ON_ERROR_STOP=1 \
        -v target_database="$TARGET_DATABASE" -v migration_user="$MIGRATION_USER" <<'SQL'
grant connect, temporary on database :"target_database" to :"migration_user";
grant usage, create on schema public to :"migration_user";
SQL
fi

unset admin_token PGPASSWORD
target_token="$(get_token "$TARGET_CLIENT_ID")"
test -n "$target_token"
export PGPASSWORD="$target_token"

psql -h "$PGHOST" -U "$TARGET_USER" -d "$TARGET_DATABASE" -tA -F '|' -v ON_ERROR_STOP=1 \
    -c "select current_database(), current_user, has_database_privilege(current_user,current_database(),'CONNECT'), has_database_privilege(current_user,current_database(),'CREATE'), has_schema_privilege(current_user,'public','USAGE'), has_schema_privilege(current_user,'public','CREATE');"

if psql -h "$PGHOST" -U "$TARGET_USER" -d "$PLATFORM_DATABASE" -tA -v ON_ERROR_STOP=1 \
    -c "select current_database(), current_user;" >/dev/null 2>&1; then
    echo "Isolation probe failed: the target identity connected to '$PLATFORM_DATABASE'." >&2
    exit 4
fi
echo "ISOLATION_OK|$TARGET_USER|$PLATFORM_DATABASE|CONNECT_DENIED"

unset target_token PGPASSWORD

if [ -n "$MIGRATION_USER" ]; then
    migration_token="$(get_token "$MIGRATION_CLIENT_ID")"
    test -n "$migration_token"
    export PGPASSWORD="$migration_token"
    psql -h "$PGHOST" -U "$MIGRATION_USER" -d "$TARGET_DATABASE" -tA -F '|' -v ON_ERROR_STOP=1 \
        -c "select current_database(), current_user, has_database_privilege(current_user,current_database(),'CONNECT'), has_schema_privilege(current_user,'public','USAGE'), has_schema_privilege(current_user,'public','CREATE');"
    unset migration_token PGPASSWORD
fi