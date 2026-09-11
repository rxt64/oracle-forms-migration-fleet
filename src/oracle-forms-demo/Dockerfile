FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY OracleFormsDemo.csproj ./
RUN dotnet restore OracleFormsDemo.csproj
COPY . .
RUN dotnet publish OracleFormsDemo.csproj -c Release --no-self-contained -o /app

# Debian rather than Alpine: Oracle.ManagedDataAccess.Core is validated against glibc, and the
# managed driver's TLS/native interop paths are unreliable on musl.
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://+:8080
USER app
EXPOSE 8080
ENTRYPOINT ["dotnet", "OracleFormsDemo.dll"]
