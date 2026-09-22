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
RUN dotnet publish -c Release -r linux-x64 --self-contained false -p:PublishReadyToRun=true -p:SkipClientBuild=true -o /app

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS final
# The SDK, not the ASP.NET Core runtime: the generated-application verification leg shells out to
# `dotnet restore/build/test` inside the sandbox, and a runtime-only image has no MSBuild to do that with.
# git clones customer source into a per-session sandbox; ca-certificates lets it verify the remote.
RUN apt-get update \
 && apt-get install --yes --no-install-recommends git ca-certificates openjdk-21-jdk maven nodejs npm bubblewrap \
 && rm -rf /var/lib/apt/lists/* \
 && chown root:root /usr/bin/bwrap \
 && chmod u+s /usr/bin/bwrap \
 && (getent group app > /dev/null || groupadd --system app) \
 && (getent passwd app > /dev/null || useradd --system --gid app --home-dir /home/app --shell /usr/sbin/nologin app) \
 && mkdir -p /var/workbench-sources \
 && mkdir -p /home/app/.m2 /home/app/.npm /home/app/.nuget \
 && chown -R app:app /var/workbench-sources /home/app
COPY --from=build /src/Fleet/Execution/Templates/Verification /tmp/ofm-verification
# The NuGet leg of this cache is a folder feed built by restoring the trusted seed project and copying
# the packages NuGet resolved. Nothing is globbed out of the repository, so the feed cannot pick up a
# .nupkg someone checked in by hand.
RUN mkdir -p /opt/ofm-verification-cache/m2 /opt/ofm-verification-cache/npm /opt/ofm-verification-cache/nuget \
 && mvn --batch-mode --no-transfer-progress \
	 -Dmaven.repo.local=/opt/ofm-verification-cache/m2 \
	 dependency:get \
	 -Dartifact=org.apache.maven.surefire:surefire-junit-platform:3.2.5 \
	 -Dtransitive=true \
 && mvn --batch-mode --no-transfer-progress \
	 -Dmaven.repo.local=/opt/ofm-verification-cache/m2 \
	 dependency:get \
	 -Dartifact=org.junit.platform:junit-platform-launcher:1.10.5 \
	 -Dtransitive=true \
 && mvn --batch-mode --no-transfer-progress \
	 -f /tmp/ofm-verification/pom.xml \
	 -Dmaven.repo.local=/opt/ofm-verification-cache/m2 \
	 -Dspring-boot.repackage.skip=true \
	 -DskipTests package \
 && npm ci --prefix /tmp/ofm-verification/Generic --ignore-scripts --no-audit --no-fund \
	 --cache=/opt/ofm-verification-cache/npm \
 && npm ci --prefix /tmp/ofm-verification/Northstar --ignore-scripts --no-audit --no-fund \
	 --cache=/opt/ofm-verification-cache/npm \
 && dotnet restore /tmp/ofm-verification/DotNet/VerificationCache.csproj \
	 --packages /tmp/ofm-nuget-packages --verbosity minimal \
 && find /tmp/ofm-nuget-packages -type f -name '*.nupkg' \
	 -exec cp --no-clobber --target-directory=/opt/ofm-verification-cache/nuget {} + \
 && for package in npgsql.9.0.3 azure.identity.1.13.2 microsoft.net.test.sdk.17.14.1 xunit.2.9.3 xunit.runner.visualstudio.3.1.4; do \
	 test -f "/opt/ofm-verification-cache/nuget/$package.nupkg" \
	 || { echo "Offline NuGet feed is missing $package.nupkg."; exit 1; }; \
	done \
 && rm -rf /tmp/ofm-verification /tmp/ofm-nuget-packages \
 && chmod -R a-w /opt/ofm-verification-cache
WORKDIR /app
COPY --from=build /app .
ENV WORKBENCH_SOURCE_ROOT=/var/workbench-sources \
	HOME=/home/app \
	MAVEN_OPTS=-Xmx512m \
	DOTNET_CLI_TELEMETRY_OPTOUT=1 \
	WORKBENCH_VERIFICATION_MAVEN_REPOSITORY=/opt/ofm-verification-cache/m2 \
	WORKBENCH_VERIFICATION_NPM_CACHE=/opt/ofm-verification-cache/npm \
	WORKBENCH_VERIFICATION_NUGET_CACHE=/opt/ofm-verification-cache/nuget
USER app
EXPOSE 8088
ENTRYPOINT ["dotnet", "oracle-forms-migration-fleet.dll"]
