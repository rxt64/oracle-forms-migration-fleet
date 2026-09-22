targetScope = 'resourceGroup'

@description('Azure region containing the existing Container Apps environment and registry.')
param location string = 'eastus2'

@description('Deployment environment tag and resource-name segment.')
@allowed([
  'dev'
  'staging'
  'prod'
])
param environmentName string = 'dev'

@description('Existing Azure Container Apps managed environment name.')
param containerAppEnvironmentName string

@description('Existing Azure Container Registry name.')
param containerRegistryName string

@description('Existing workbench managed identity that may update only this target app after bootstrap.')
param workbenchIdentityName string

@description('Existing Azure Database for PostgreSQL Flexible Server host.')
param postgresHost string

@description('Dedicated generated-application database. The reserved postgres sandbox is forbidden.')
param targetDatabaseName string = 'ofm_dotnet_pilot'

@description('Create the target Container App only after the product supplies an exact ACR digest.')
param deployTarget bool = false

@description('Exact ACR image reference ending in @sha256:<64 lowercase hex characters>.')
param containerImage string = ''

@description('Maximum size in GiB of the durable workbench source and generated-artifact share.')
@minValue(10)
@maxValue(100)
param artifactShareQuotaGiB int = 20

var uniqueSuffix = take(uniqueString(resourceGroup().id), 8)
var environmentToken = take(environmentName, 3)
var targetIdentityName = 'id-ofmfleet-dotnet-${environmentName}-${uniqueSuffix}'
var targetIdentityResourceId = resourceId('Microsoft.ManagedIdentity/userAssignedIdentities', targetIdentityName)
var targetContainerAppName = 'ca-ofmfleet-dotnet-${environmentName}-${uniqueSuffix}'
var storageAccountName = 'stofmdot${environmentToken}${uniqueSuffix}'
var artifactShareName = 'workbench-artifacts'
var environmentStorageName = 'dotnet-pilot-artifacts'
var artifactVolumeName = 'workbench-artifacts'
var registryLoginServer = '${containerRegistryName}.azurecr.io'
var targetConnection = 'Host=${postgresHost};Database=${targetDatabaseName};Username=${targetIdentityName};SSL Mode=Require;Trust Server Certificate=false'
var acrPullRoleId = '7f951dda-4ed3-4680-a7ca-43fe172d538d'
var targetDeployerRoleId = guid(subscription().id, resourceGroup().id, 'ofmfleet-generated-application-deployer')
var tags = {
  workload: 'oracle-forms-migration-fleet'
  environment: environmentName
  component: 'dotnet-pilot'
  'managed-by': 'bicep'
  'data-classification': 'confidential'
}

resource containerAppEnvironment 'Microsoft.App/managedEnvironments@2025-01-01' existing = {
  name: containerAppEnvironmentName
}

resource containerRegistry 'Microsoft.ContainerRegistry/registries@2023-07-01' existing = {
  name: containerRegistryName
}

resource workbenchIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' existing = {
  name: workbenchIdentityName
}

module targetIdentity 'br/public:avm/res/managed-identity/user-assigned-identity:0.6.0' = {
  name: 'deploy-${targetIdentityName}'
  params: {
    name: targetIdentityName
    location: location
    tags: tags
  }
}

resource storageAccount 'Microsoft.Storage/storageAccounts@2025-06-01' = {
  name: storageAccountName
  location: location
  tags: union(tags, {
    component: 'durable-workbench-artifacts'
  })
  sku: {
    name: 'Standard_LRS'
  }
  kind: 'StorageV2'
  properties: {
    accessTier: 'Hot'
    allowBlobPublicAccess: false
    allowCrossTenantReplication: false
    // ACA SMB mounts currently require a storage account key. The key is sent only to the
    // managed-environment storage resource and is never exposed to either application container.
    allowSharedKeyAccess: true
    defaultToOAuthAuthentication: true
    minimumTlsVersion: 'TLS1_2'
    publicNetworkAccess: 'Enabled'
    supportsHttpsTrafficOnly: true
    networkAcls: {
      bypass: 'AzureServices'
      defaultAction: 'Allow'
    }
  }
}

resource fileService 'Microsoft.Storage/storageAccounts/fileServices@2025-06-01' = {
  parent: storageAccount
  name: 'default'
  properties: {
    protocolSettings: {
      smb: {
        versions: 'SMB3.0;SMB3.1.1'
      }
    }
    shareDeleteRetentionPolicy: {
      enabled: true
      days: 7
    }
  }
}

resource artifactShare 'Microsoft.Storage/storageAccounts/fileServices/shares@2025-06-01' = {
  parent: fileService
  name: artifactShareName
  properties: {
    accessTier: 'TransactionOptimized'
    enabledProtocols: 'SMB'
    shareQuota: artifactShareQuotaGiB
  }
}

resource environmentStorage 'Microsoft.App/managedEnvironments/storages@2025-01-01' = {
  parent: containerAppEnvironment
  name: environmentStorageName
  properties: {
    azureFile: {
      accessMode: 'ReadWrite'
      accountKey: storageAccount.listKeys().keys[0].value
      accountName: storageAccount.name
      shareName: artifactShare.name
    }
  }
}

resource registryPull 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(containerRegistry.id, targetIdentityResourceId, acrPullRoleId)
  scope: containerRegistry
  properties: {
    principalId: targetIdentity.outputs.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', acrPullRoleId)
    description: 'Generated .NET pilot runtime can pull images from this registry only.'
  }
}

resource targetDeployerRole 'Microsoft.Authorization/roleDefinitions@2022-04-01' = if (deployTarget) {
  name: targetDeployerRoleId
  properties: {
    roleName: 'Oracle Forms Migration Fleet Generated Application Deployer'
    description: 'Update and inspect generated Container Apps without delete, environment, registry, or role-assignment permissions.'
    type: 'CustomRole'
    assignableScopes: [
      resourceGroup().id
    ]
    permissions: [
      {
        actions: [
          'Microsoft.App/containerApps/read'
          'Microsoft.App/containerApps/write'
          'Microsoft.App/containerApps/revisions/read'
        ]
        notActions: []
        dataActions: []
        notDataActions: []
      }
    ]
  }
}

resource targetApp 'Microsoft.App/containerApps@2025-01-01' = if (deployTarget) {
  name: targetContainerAppName
  location: location
  tags: tags
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${targetIdentityResourceId}': {}
    }
  }
  properties: {
    managedEnvironmentId: containerAppEnvironment.id
    configuration: {
      activeRevisionsMode: 'Single'
      ingress: {
        allowInsecure: false
        external: true
        targetPort: 8080
        transport: 'auto'
      }
      registries: [
        {
          identity: targetIdentityResourceId
          server: registryLoginServer
        }
      ]
    }
    template: {
      containers: [
        {
          name: 'generated-dotnet-api'
          image: containerImage
          env: [
            {
              name: 'AZURE_CLIENT_ID'
              value: targetIdentity.outputs.clientId
            }
            {
              name: 'TARGET_POSTGRES_CONNECTION'
              value: targetConnection
            }
            {
              name: 'PORT'
              value: '8080'
            }
          ]
          probes: [
            {
              type: 'Startup'
              httpGet: {
                path: '/healthz'
                port: 8080
                scheme: 'HTTP'
              }
              initialDelaySeconds: 5
              periodSeconds: 5
              failureThreshold: 24
              timeoutSeconds: 3
            }
            {
              type: 'Liveness'
              httpGet: {
                path: '/healthz'
                port: 8080
                scheme: 'HTTP'
              }
              periodSeconds: 30
              failureThreshold: 3
              timeoutSeconds: 5
            }
            {
              type: 'Readiness'
              httpGet: {
                path: '/healthz'
                port: 8080
                scheme: 'HTTP'
              }
              periodSeconds: 10
              failureThreshold: 6
              timeoutSeconds: 3
            }
          ]
          resources: {
            cpu: json('0.5')
            memory: '1Gi'
          }
        }
      ]
      scale: {
        minReplicas: 0
        maxReplicas: 1
      }
    }
  }
  dependsOn: [
    environmentStorage
    registryPull
  ]
}

resource workbenchTargetDeployment 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (deployTarget) {
  name: guid(targetApp.id, workbenchIdentity.id, targetDeployerRoleId)
  scope: targetApp
  properties: {
    principalId: workbenchIdentity.properties.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: targetDeployerRole.id
    description: 'Workbench may update only the generated .NET pilot Container App after deterministic authorization.'
  }
}

output targetIdentityName string = targetIdentityName
output targetIdentityClientId string = targetIdentity.outputs.clientId
output targetIdentityPrincipalId string = targetIdentity.outputs.principalId
output targetDatabaseName string = targetDatabaseName
output targetContainerAppName string = targetContainerAppName
output artifactStorageAccountName string = storageAccount.name
output artifactShareName string = artifactShare.name
output environmentStorageName string = environmentStorage.name
output artifactVolumeName string = artifactVolumeName
output targetContainerAppResourceId string = deployTarget ? targetApp.id : ''
