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
RUN apk add --no-cache git ca-certificates openjdk21-jdk maven nodejs npm bubblewrap \
 && chown root:root /usr/bin/bwrap \
 && chmod u+s /usr/bin/bwrap \
 && mkdir -p /var/workbench-sources \
 && mkdir -p /home/app/.m2 /home/app/.npm \
 && chown -R app:app /var/workbench-sources /home/app
COPY --from=build /src/Fleet/Execution/Templates/Verification /tmp/ofm-verification
RUN mkdir -p /opt/ofm-verification-cache/m2 /opt/ofm-verification-cache/npm \
 && mvn --batch-mode --no-transfer-progress \
	 -f /tmp/ofm-verification/pom.xml \
	 -Dmaven.repo.local=/opt/ofm-verification-cache/m2 \
	 dependency:go-offline \
 && npm ci --prefix /tmp/ofm-verification/Generic --ignore-scripts --no-audit --no-fund \
	 --cache=/opt/ofm-verification-cache/npm \
 && npm ci --prefix /tmp/ofm-verification/Northstar --ignore-scripts --no-audit --no-fund \
	 --cache=/opt/ofm-verification-cache/npm \
 && rm -rf /tmp/ofm-verification \
 && chmod -R a-w /opt/ofm-verification-cache
WORKDIR /app
COPY --from=build /app .
ENV WORKBENCH_SOURCE_ROOT=/var/workbench-sources \
	HOME=/home/app \
	MAVEN_OPTS=-Xmx512m \
	WORKBENCH_VERIFICATION_MAVEN_REPOSITORY=/opt/ofm-verification-cache/m2 \
	WORKBENCH_VERIFICATION_NPM_CACHE=/opt/ofm-verification-cache/npm
USER app
EXPOSE 8088
ENTRYPOINT ["dotnet", "oracle-forms-migration-fleet.dll"]
