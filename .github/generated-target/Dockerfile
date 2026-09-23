# Host-owned container recipe for a fleet-generated application tier.
#
# This file is owned by the product and lives in the product's repository. It is deliberately NOT the
# `backend/Dockerfile` the generator emits beside the code: that file arrives inside an artifact built from
# source the fleet treats as untrusted, and building it would let the estate under migration choose what
# runs on the builder. The build context is assembled so nothing from the artifact can shadow this file:
# the generated tree is placed under `app/`, and this recipe is copied in afterwards.
#
# Nothing here executes a script, a task, or a lifecycle hook that came out of the artifact. `npm ci` runs
# with `--ignore-scripts` for the same reason, and `dotnet` is invoked against explicit project paths.
#
# No credential is baked in. The database connection arrives as configuration at run time and carries no
# password: the generated API authenticates to Azure Database for PostgreSQL with the managed identity
# named by AZURE_CLIENT_ID.

# ---------------------------------------------------------------------------------------------------
# Browser bundle. Built and kept so the image carries exactly the assets this run generated.
# ---------------------------------------------------------------------------------------------------
FROM mcr.microsoft.com/devcontainers/javascript-node:1-22-bookworm AS client
WORKDIR /client
COPY app/frontend/package.json app/frontend/package-lock.json* ./
RUN npm ci --ignore-scripts --no-audit --no-fund
COPY app/frontend/ ./
RUN npm run build

# ---------------------------------------------------------------------------------------------------
# API. Restored and published from the generated project file, with the test project excluded.
# ---------------------------------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY app/backend/Api/Api.csproj Api/
RUN dotnet restore Api/Api.csproj
COPY app/backend/Api/ Api/
RUN dotnet publish Api/Api.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app ./

# The generated browser bundle. `ApiHost` registers default-file, static-file, and SPA fallback handling,
# so these assets are the screens the deployed application serves from the same origin as its API. The
# fallback is registered after the /api catch-all, so an unrouted API path still answers a JSON 404.
COPY --from=client /client/dist ./wwwroot/

ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "GeneratedApplication.Api.dll"]
