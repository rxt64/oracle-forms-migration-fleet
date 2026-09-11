using './main.bicep'

// The image tag and both passwords are injected by Deploy-LegacyEstate.ps1 and deliberately absent from source control.
param location = 'eastus2'
param workloadToken = 'ofmfleet'
param environmentName = 'dev'
param containerAppEnvironmentName = 'cae-ofmfleet-dev-ykbpnrpd'
param containerRegistryName = 'acrofmfleedevykbpnrpd'