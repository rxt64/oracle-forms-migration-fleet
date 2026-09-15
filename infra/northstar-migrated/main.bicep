@description('Azure region containing the existing Container Apps environment.')
param location string = resourceGroup().location

@description('Fully qualified image reference produced by the deployment workflow.')
param containerImage string

@description('Name of the destination Container App.')
param containerAppName string = 'ca-ofmfleet-migrated-dev-ykbpnrpd'

@description('Existing Container Apps managed environment name.')
param containerAppEnvironmentName string = 'cae-ofmfleet-dev-ykbpnrpd'

@description('Existing Azure Container Registry name.')
param containerRegistryName string = 'acrofmfleedevykbpnrpd'

@description('Existing user-assigned identity used for ACR pull and PostgreSQL Entra authentication.')
param identityName string = 'id-ofmfleet-web-dev-ykbpnrpd'

@description('Azure Database for PostgreSQL host containing the migrated sandbox data.')
param postgresHost string = 'pg-ofmfleet-dev-ykbpnrpd.postgres.database.azure.com'

@description('PostgreSQL database containing the migrated schema and rows.')
param postgresDatabase string = 'postgres'

var identityResourceId = resourceId('Microsoft.ManagedIdentity/userAssignedIdentities', identityName)
var registryLoginServer = '${containerRegistryName}.azurecr.io'

resource containerAppEnvironment 'Microsoft.App/managedEnvironments@2025-01-01' existing = {
  name: containerAppEnvironmentName
}

resource containerRegistry 'Microsoft.ContainerRegistry/registries@2023-07-01' existing = {
  name: containerRegistryName
}

resource appIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' existing = {
  name: identityName
}

resource migratedApp 'Microsoft.App/containerApps@2025-01-01' = {
  name: containerAppName
  location: location
  tags: {
    application: 'northstar-online-banking'
    component: 'migrated-application'
    'data-classification': 'synthetic'
    'managed-by': 'bicep'
  }
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
          server: registryLoginServer
        }
      ]
    }
    template: {
      containers: [
        {
          name: 'northstar-migrated'
          image: containerImage
          env: [
            {
              name: 'AZURE_CLIENT_ID'
              value: appIdentity.properties.clientId
            }
            {
              name: 'PGHOST'
              value: postgresHost
            }
            {
              name: 'PGDATABASE'
              value: postgresDatabase
            }
            {
              name: 'PGUSER'
              value: identityName
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
                path: '/'
                port: 8080
                scheme: 'HTTP'
              }
              initialDelaySeconds: 10
              periodSeconds: 5
              failureThreshold: 24
              timeoutSeconds: 3
            }
            {
              type: 'Readiness'
              httpGet: {
                path: '/'
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
        minReplicas: 1
        maxReplicas: 1
      }
    }
  }
}

output fqdn string = migratedApp.properties.configuration.ingress.fqdn
output url string = 'https://${migratedApp.properties.configuration.ingress.fqdn}'