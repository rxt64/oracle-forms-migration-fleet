targetScope = 'resourceGroup'

@description('Azure region of the existing Oracle Forms migration fleet resource group.')
@allowed([
  'eastus2'
])
param location string = 'eastus2'

@description('Existing VNet that contains the Forms 6i and Oracle 9i source hosts.')
param existingVnetName string = 'vnet-ofm-forms6i-j6mrrerz'

@description('Existing Log Analytics workspace used by the public workbench.')
param existingLogAnalyticsWorkspaceName string = 'log-ofmfleet-web-dev-ykbpnrpd'

@description('Existing user-assigned identity used by the public workbench.')
param existingWorkbenchIdentityName string = 'id-ofmfleet-web-dev-ykbpnrpd'

@description('Dedicated subnet name for the internal Container Apps environment.')
param infrastructureSubnetName string = 'snet-ofmfleet-private-workbench'

@description('Approved dedicated Container Apps infrastructure subnet prefix.')
@allowed([
  '10.246.0.64/27'
])
param infrastructureSubnetPrefix string = '10.246.0.64/27'

@description('Dedicated subnet name for private endpoints used by the private workbench.')
param privateEndpointSubnetName string = 'snet-ofmfleet-private-endpoints'

@description('Approved dedicated private endpoint subnet prefix.')
@allowed([
  '10.246.0.96/27'
])
param privateEndpointSubnetPrefix string = '10.246.0.96/27'

@description('Internal Container Apps managed environment name.')
param managedEnvironmentName string = 'cae-ofmfleet-private-dev'

var tags = {
  workload: 'oracle-forms-migration-fleet'
  environment: 'dev'
  component: 'private-source-gateway-workbench'
  'managed-by': 'bicep'
  'data-classification': 'confidential'
}

resource existingVnet 'Microsoft.Network/virtualNetworks@2024-05-01' existing = {
  name: existingVnetName
}

resource existingLogAnalytics 'Microsoft.OperationalInsights/workspaces@2023-09-01' existing = {
  name: existingLogAnalyticsWorkspaceName
}

resource existingWorkbenchIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' existing = {
  name: existingWorkbenchIdentityName
}

resource infrastructureSubnet 'Microsoft.Network/virtualNetworks/subnets@2024-05-01' = {
  parent: existingVnet
  name: infrastructureSubnetName
  properties: {
    addressPrefix: infrastructureSubnetPrefix
    delegations: [
      {
        name: 'Microsoft.App.environments'
        properties: {
          serviceName: 'Microsoft.App/environments'
        }
      }
    ]
    privateEndpointNetworkPolicies: 'Disabled'
    privateLinkServiceNetworkPolicies: 'Enabled'
  }
}

resource privateEndpointSubnet 'Microsoft.Network/virtualNetworks/subnets@2024-05-01' = {
  parent: existingVnet
  name: privateEndpointSubnetName
  dependsOn: [
    infrastructureSubnet
  ]
  properties: {
    addressPrefix: privateEndpointSubnetPrefix
    privateEndpointNetworkPolicies: 'Disabled'
    privateLinkServiceNetworkPolicies: 'Enabled'
  }
}

resource managedEnvironment 'Microsoft.App/managedEnvironments@2025-01-01' = {
  name: managedEnvironmentName
  location: location
  tags: tags
  properties: {
    appLogsConfiguration: {
      destination: 'log-analytics'
      logAnalyticsConfiguration: {
        customerId: existingLogAnalytics.properties.customerId
        sharedKey: existingLogAnalytics.listKeys().primarySharedKey
      }
    }
    vnetConfiguration: {
      infrastructureSubnetId: infrastructureSubnet.id
      internal: true
    }
    workloadProfiles: [
      {
        name: 'Consumption'
        workloadProfileType: 'Consumption'
      }
    ]
    zoneRedundant: false
  }
}

output managedEnvironmentId string = managedEnvironment.id
output managedEnvironmentDefaultDomain string = managedEnvironment.properties.defaultDomain
output managedEnvironmentInboundStaticIp string = managedEnvironment.properties.staticIp
output existingWorkbenchIdentityId string = existingWorkbenchIdentity.id
output privateEndpointSubnetId string = privateEndpointSubnet.id