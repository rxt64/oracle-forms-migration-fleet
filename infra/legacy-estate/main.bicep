targetScope = 'resourceGroup'

@description('Azure region for the disposable legacy estate resources.')
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

@description('Name of the existing Azure Container Registry that contains the Oracle image.')
param containerRegistryName string = 'acrofmfleedevykbpnrpd'

// The image tag and both passwords are supplied by Deploy-LegacyEstate.ps1 at deployment time. They
// default to empty so dev.bicepparam can stay in source control without carrying a credential.
@description('Full Oracle Database Free container image reference, including its registry and tag.')
param oracleImage string = ''

@secure()
@description('Password for the Oracle SYS and SYSTEM users.')
param oracleSysPassword string = ''

@secure()
@description('Password for the BANKING application schema user.')
param bankingSchemaPassword string = ''

var uniqueSuffix = take(uniqueString(resourceGroup().id), 8)
var identityName = 'id-${workloadToken}-db-${environmentName}-${uniqueSuffix}'
var identityResourceId = resourceId('Microsoft.ManagedIdentity/userAssignedIdentities', identityName)
var containerAppName = 'ca-${workloadToken}-db-${environmentName}-${uniqueSuffix}'
var containerRegistryLoginServer = '${containerRegistryName}.azurecr.io'
var oracleSysPasswordSecretName = 'oracle-sys-password'
var bankingSchemaPasswordSecretName = 'banking-schema-password'
var acrPullRoleId = '7f951dda-4ed3-4680-a7ca-43fe172d538d'
var tags = {
  workload: 'oracle-forms-migration-fleet'
  environment: environmentName
  'managed-by': 'bicep'
  'data-classification': 'confidential'
  estate: 'legacy-source-demo'
}

resource containerAppEnvironment 'Microsoft.App/managedEnvironments@2025-01-01' existing = {
  name: containerAppEnvironmentName
}

resource containerRegistry 'Microsoft.ContainerRegistry/registries@2023-07-01' existing = {
  name: containerRegistryName
}

module legacyDatabaseIdentity 'br/public:avm/res/managed-identity/user-assigned-identity:0.6.0' = {
  name: 'deploy-${identityName}'
  params: {
    name: identityName
    location: location
    tags: tags
  }
}

// The passwords are held as Container Apps secrets rather than in Key Vault. Tenant governance in
// this subscription forces every new vault to publicNetworkAccess='Disabled' and silently reverts
// any attempt to re-enable it, and this Container Apps environment is Consumption-only with no
// VNet, so a private-only vault is unreachable from the app by construction. Standing up a VNet
// and a private endpoint purely to hold two throwaway passwords for a disposable demo database is
// not a trade worth making. Container Apps secrets are encrypted at rest by the platform and are
// only readable by callers who already hold RBAC on the container app.

resource registryPull 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(containerRegistry.id, identityName, acrPullRoleId)
  scope: containerRegistry
  properties: {
    principalId: legacyDatabaseIdentity.outputs.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', acrPullRoleId)
    description: 'Legacy database identity can pull the Oracle image.'
  }
}

resource legacyDatabase 'Microsoft.App/containerApps@2025-01-01' = {
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
        external: false
        exposedPort: 1521
        targetPort: 1521
        transport: 'tcp'
      }
      registries: [
        {
          identity: identityResourceId
          server: containerRegistryLoginServer
        }
      ]
      secrets: [
        {
          name: oracleSysPasswordSecretName
          value: oracleSysPassword
        }
        {
          name: bankingSchemaPasswordSecretName
          value: bankingSchemaPassword
        }
      ]
    }
    template: {
      containers: [
        {
          name: 'oracle-free'
          image: oracleImage
          env: [
            {
              name: 'ORACLE_PASSWORD'
              secretRef: oracleSysPasswordSecretName
            }
            {
              name: 'APP_USER'
              value: 'BANKING'
            }
            {
              name: 'APP_USER_PASSWORD'
              secretRef: bankingSchemaPasswordSecretName
            }
          ]
          probes: [
            {
              type: 'Startup'
              tcpSocket: {
                port: 1521
              }
              initialDelaySeconds: 30
              periodSeconds: 15
              failureThreshold: 40
              timeoutSeconds: 5
            }
            {
              type: 'Liveness'
              tcpSocket: {
                port: 1521
              }
              periodSeconds: 30
              failureThreshold: 5
              timeoutSeconds: 5
            }
          ]
          resources: {
            cpu: json('2.0')
            memory: '4Gi'
          }
        }
      ]
      scale: {
        minReplicas: 1
        maxReplicas: 1
      }
    }
  }
  // AcrPull must exist before Container Apps tries to pull the image.
  dependsOn: [
    registryPull
  ]
}

output containerAppName string = legacyDatabase.name
output containerAppResourceId string = legacyDatabase.id
output internalFqdn string = legacyDatabase.properties.configuration.ingress.fqdn
output managedIdentityName string = legacyDatabaseIdentity.outputs.name
output managedIdentityPrincipalId string = legacyDatabaseIdentity.outputs.principalId