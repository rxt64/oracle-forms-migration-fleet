# Host-owned container recipe for a fleet-generated application tier.
#
# This file is owned by the product and lives in the product's repository. It is deliberately NOT the
# `backend/Dockerfile` the generator emits beside the code: that file arrives inside an artifact built from
# source the fleet treats as untrusted, and building it would let the estate under migration choose what
# runs on the builder. The build context is assembled so nothing from the artifact can shadow this file:
# the generated tree is placed under `app/`, and this recipe is copied in afterwards.
#
# This recipe is also the ONLY place generated code is ever executed. The builder runs no `dotnet` and no
# `npm` over the artifact on the runner itself, because a process running as the runner's own user can
# append to $GITHUB_ENV and $GITHUB_PATH, write $BASH_ENV, replace a tool on $PATH, or leave a background
# process behind, and none of that is detectable by hashing a file afterwards. Every `RUN` below executes
# in a container whose filesystem is discarded, which holds no Azure or GitHub credential, which has no
# Docker socket, and which cannot reach the runner's workspace or its command files.
#
# Nothing here executes a script, a task, or a lifecycle hook that came out of the artifact. `npm ci` runs
# with `--ignore-scripts` for the same reason, and `dotnet` is invoked against explicit project paths.
#
# No `--mount=type=secret` and no `--mount=type=ssh` appears anywhere below, so `docker build` is invoked
# without either and a generated `RUN` has nothing to read even if it looked.
#
# No credential is baked into the shipped image. The database connection arrives as configuration at run
# time and carries no password: the generated API authenticates to Azure Database for PostgreSQL with the
# managed identity named by AZURE_CLIENT_ID.

# ---------------------------------------------------------------------------------------------------
# Browser bundle. Tested and built in one stage, and the stage that ships consumes this stage's `dist`,
# so the test cannot be pruned out of the build graph the way an unreferenced stage would be.
# ---------------------------------------------------------------------------------------------------
FROM mcr.microsoft.com/devcontainers/javascript-node:1-22-bookworm AS client
WORKDIR /client
COPY app/frontend/package.json app/frontend/package-lock.json* ./
RUN npm ci --ignore-scripts --no-audit --no-fund
COPY app/frontend/ ./
RUN npm run test -- --run
RUN npm run build

# ---------------------------------------------------------------------------------------------------
# API. The generated acceptance suite runs here, against a PostgreSQL that exists only inside this build
# stage, and the publish that produces the shipped binaries is a later instruction in the same stage.
# One stage is what makes the suite un-skippable: a separate test stage nothing copies from is pruned by
# BuildKit before it runs.
#
# The disposable database listens on loopback inside this container and is thrown away with the stage. It
# carries a throwaway password so the generated host treats it as a local development target; a
# passwordless connection string would send the generated code down its Microsoft Entra token path, which
# is the one thing that must not happen in a build.
# ---------------------------------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS api
WORKDIR /src
ENV TARGET_POSTGRES_CONNECTION="Host=127.0.0.1;Port=5432;Database=generated;Username=postgres;Password=generated-suite-local" \
    GENERATED_SUITE_REQUIRE_TARGET=true \
    DOTNET_NOLOGO=1 \
    DOTNET_CLI_TELEMETRY_OPTOUT=1

RUN set -eux; \
    apt-get update; \
    apt-get install -y --no-install-recommends postgresql; \
    rm -rf /var/lib/apt/lists/*

COPY app/backend/ ./backend/

RUN set -eux; \
    pgbin="$(ls -d /usr/lib/postgresql/*/bin | sort -V | tail -n 1)"; \
    install -d -m 0700 -o postgres -g postgres /var/lib/postgresql/generated; \
    install -d -m 0775 -o postgres -g postgres /var/run/postgresql; \
    install -m 0600 -o postgres -g postgres /dev/null /tmp/generated-pg-password; \
    printf '%s' 'generated-suite-local' > /tmp/generated-pg-password; \
    su postgres -c "$pgbin/initdb --pgdata=/var/lib/postgresql/generated --username=postgres --auth-local=trust --auth-host=scram-sha-256 --pwfile=/tmp/generated-pg-password"; \
    rm -f /tmp/generated-pg-password; \
    su postgres -c "$pgbin/pg_ctl --pgdata=/var/lib/postgresql/generated --log=/tmp/generated-pg.log --timeout=120 --wait --options='-c listen_addresses=127.0.0.1 -p 5432' start"; \
    su postgres -c "$pgbin/createdb --host=127.0.0.1 --username=postgres generated"; \
    dotnet test backend/GeneratedBackend.slnx -c Release --nologo; \
    su postgres -c "$pgbin/pg_ctl --pgdata=/var/lib/postgresql/generated --mode=immediate stop"

RUN dotnet publish backend/Api/Api.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=api /app ./

# The generated browser bundle. `ApiHost` registers default-file, static-file, and SPA fallback handling,
# so these assets are the screens the deployed application serves from the same origin as its API. The
# fallback is registered after the /api catch-all, so an unrouted API path still answers a JSON 404.
COPY --from=client /client/dist ./wwwroot/

ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "GeneratedApplication.Api.dll"]
