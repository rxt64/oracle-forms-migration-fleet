using './main.bicep'

// The image and Oracle connection string are injected by Deploy-FormsDemo.ps1 and are absent from source control.
param location = 'eastus2'
param workloadToken = 'ofmfleet'
param environmentName = 'dev'
param containerAppEnvironmentName = 'cae-ofmfleet-dev-ykbpnrpd'
param containerRegistryName = 'acrofmfleedevykbpnrpd'
param databaseContainerAppName = 'ca-ofmfleet-db-dev-ykbpnrpd'