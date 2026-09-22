using './main.bicep'

param location = 'eastus2'
param environmentName = 'dev'
param containerAppEnvironmentName = 'cae-ofmfleet-dev-ykbpnrpd'
param containerRegistryName = 'acrofmfleedevykbpnrpd'
param workbenchIdentityName = 'id-ofmfleet-web-dev-ykbpnrpd'
param postgresHost = 'pg-ofmfleet-dev-ykbpnrpd.postgres.database.azure.com'
param targetDatabaseName = 'ofm_dotnet_pilot'
param deployTarget = false
param artifactShareQuotaGiB = 20