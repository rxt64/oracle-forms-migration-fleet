targetScope = 'resourceGroup'

@description('Azure region for the Oracle Forms workflow replica.')
param location string = 'eastus2'

@description('Short lowercase alphanumeric workload token used in resource names.')
@minLength(3)
@maxLength(10)
param workloadToken string = 'ofmfleet'

@description('Deployment environment tag and resource-name segment.')
@allowed([
  'dev'
  'staging'
  'prod'
])
param environmentName string = 'dev'

@description('Name of the existing Azure Container Apps managed environment.')
param containerAppEnvironmentName string = 'cae-ofmfleet-dev-ykbpnrpd'

@description('Name of the existing Azure Container Registry that contains the workflow replica image.')
param containerRegistryName string = 'acrofmfleedevykbpnrpd'

@description('Name of the existing internal Oracle database container app used by the workflow replica.')
param databaseContainerAppName string = 'ca-ofmfleet-db-dev-ykbpnrpd'

@description('Full workflow replica container image reference, including its registry and immutable tag.')
param containerImage string = ''

@secure()
@description('ODP.NET connection string for the internal disposable Oracle estate.')
param oracleConnectionString string = ''

var uniqueSuffix = take(uniqueString(resourceGroup().id), 8)
var identityName = 'id-${workloadToken}-forms-${environmentName}-${uniqueSuffix}'
var identityResourceId = resourceId('Microsoft.ManagedIdentity/userAssignedIdentities', identityName)
var containerAppName = 'ca-${workloadToken}-forms-${environmentName}-${uniqueSuffix}'
var containerRegistryLoginServer = '${containerRegistryName}.azurecr.io'
var oracleConnectionStringSecretName = 'oracle-connection-string'
var acrPullRoleId = '7f951dda-4ed3-4680-a7ca-43fe172d538d'
var tags = {
  workload: 'oracle-forms-migration-fleet'
  environment: environmentName
  'managed-by': 'bicep'
  'data-classification': 'confidential'
  component: 'forms-workflow-replica'
  'database-app': databaseContainerAppName
}

resource containerAppEnvironment 'Microsoft.App/managedEnvironments@2025-01-01' existing = {
  name: containerAppEnvironmentName
}

resource containerRegistry 'Microsoft.ContainerRegistry/registries@2023-07-01' existing = {
  name: containerRegistryName
}

resource formsIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: identityName
  location: location
  tags: tags
}

resource registryPull 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(containerRegistry.id, identityName, acrPullRoleId)
  scope: containerRegistry
  properties: {
    principalId: formsIdentity.properties.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', acrPullRoleId)
    description: 'Oracle Forms workflow replica identity can pull its image.'
  }
}

resource formsDemo 'Microsoft.App/containerApps@2025-01-01' = {
  name: containerAppName
  location: location
  tags: tags
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${identityResourceId}': {}
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
          identity: identityResourceId
          server: containerRegistryLoginServer
        }
      ]
      secrets: [
        {
          name: oracleConnectionStringSecretName
          value: oracleConnectionString
        }
      ]
    }
    template: {
      containers: [
        {
          name: 'oracle-forms-demo'
          image: containerImage
          env: [
            {
              name: 'PORT'
              value: '8080'
            }
            {
              name: 'ORACLE__CONNECTIONSTRING'
              secretRef: oracleConnectionStringSecretName
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
              initialDelaySeconds: 50
              periodSeconds: 10
              failureThreshold: 10
              timeoutSeconds: 3
            }
            {
              type: 'Liveness'
              httpGet: {
                path: '/healthz'
                port: 8080
                scheme: 'HTTP'
              }
              initialDelaySeconds: 10
              periodSeconds: 30
              failureThreshold: 3
              timeoutSeconds: 5
            }
          ]
          resources: {
            cpu: json('0.5')
            memory: '1Gi'
          }
        }
      ]
      scale: {
        minReplicas: 1
        maxReplicas: 1
      }
    }
  }
  dependsOn: [
    registryPull
  ]
}

output containerAppName string = formsDemo.name
output containerAppResourceId string = formsDemo.id
output fqdn string = formsDemo.properties.configuration.ingress.fqdn
output url string = 'https://${formsDemo.properties.configuration.ingress.fqdn}'
output managedIdentityName string = formsIdentity.name
output managedIdentityPrincipalId string = formsIdentity.properties.principalId
