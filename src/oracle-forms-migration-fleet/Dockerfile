FROM node:22-alpine AS client-build
WORKDIR /src/ClientApp
COPY ClientApp/package.json ClientApp/package-lock.json ./
RUN npm ci
COPY ClientApp/ ./
RUN npm run build

FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS build
WORKDIR /src
COPY . .
COPY --from=client-build /src/wwwroot ./wwwroot
# Publish ReadyToRun: precompile IL to native code for the target runtime so
# the JIT does far less work during container start. Restore/build/publish are
# collapsed into one publish because ReadyToRun must be set at restore time
# too (otherwise NETSDK1094). --self-contained false keeps the framework-
# dependent layout, so this costs image size but not a runtime dependency.
RUN dotnet publish -c Release -r linux-musl-x64 --self-contained false -p:PublishReadyToRun=true -p:SkipClientBuild=true -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine AS final
# git clones customer source into a per-session sandbox; ca-certificates lets it verify the remote.
RUN apk add --no-cache git ca-certificates \
 && mkdir -p /var/workbench-sources \
 && chown -R app:app /var/workbench-sources
WORKDIR /app
COPY --from=build /app .
ENV WORKBENCH_SOURCE_ROOT=/var/workbench-sources
USER app
EXPOSE 8088
ENTRYPOINT ["dotnet", "oracle-forms-migration-fleet.dll"]
