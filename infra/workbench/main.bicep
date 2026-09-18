targetScope = 'resourceGroup'

@description('Azure region for the workbench resources.')
param location string = resourceGroup().location

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

@description('Existing Microsoft Foundry account name.')
param foundryAccountName string

@description('Existing Microsoft Foundry project name.')
param foundryProjectName string

@description('Exact hosted-agent Responses endpoint. Leave empty during the foundation deployment.')
param foundryAgentEndpoint string = ''

@description('Container image to deploy after it has been built in this stack Azure Container Registry.')
param containerImage string = ''

@description('Whether to deploy the workbench Container App. The foundation deployment leaves this false.')
param deployWorkbench bool = false

@description('Single-tenant Entra application client ID used by Container Apps authentication.')
param entraClientId string = ''

@description('Microsoft Entra object IDs allowed to use the confidential migration workbench.')
@minLength(1)
param operatorPrincipalObjectIds array

@description('Dedicated non-human identities permitted to run non-writing deployment verification.')
param validationPrincipalObjectIds array = []

@secure()
@description('Entra application credential consumed only by the Container Apps authentication provider.')
param entraClientSecret string = ''

// The workbench persists organizations, projects, memberships, target profiles, and approvals in an
// isolated schema on an EXISTING PostgreSQL flexible server. This template creates no database server:
// it only tells the container where that schema is. The identity below must already have been granted
// login and CREATE on the schema, and the server firewall must already admit the container app --
// neither is expressible here, and both are preconditions documented in infra/workbench/README.md.
@description('Host name of the existing PostgreSQL flexible server holding the platform schema. Required when deployWorkbench is true.')
param platformDatabaseHost string = ''

@description('Database on that server holding the platform schema.')
param platformDatabaseName string = 'postgres'

@description('Entra principal name the workbench authenticates to the platform database as. Never a password.')
param platformDatabaseUser string = ''

@description('Isolated schema for platform state. Kept separate from any migration target schema.')
param platformDatabaseSchema string = 'ofm_platform'

@description('Host name of the sandbox PostgreSQL server. The sandbox database must differ from the platform database.')
param sandboxDatabaseHost string = ''

@description('Sandbox database that generated migration DDL may modify.')
param sandboxDatabaseName string = 'postgres'

@description('Entra principal name used for sandbox migration writes. Never a password.')
param sandboxDatabaseUser string = ''

var uniqueSuffix = take(uniqueString(resourceGroup().id), 8)
var environmentToken = take(environmentName, 3)
var compactToken = take(workloadToken, 7)
var registryName = 'acr${compactToken}${environmentToken}${uniqueSuffix}'
var identityName = 'id-${workloadToken}-web-${environmentName}-${uniqueSuffix}'
var workspaceName = 'log-${workloadToken}-web-${environmentName}-${uniqueSuffix}'
var insightsName = 'appi-${workloadToken}-web-${environmentName}-${uniqueSuffix}'
var managedEnvironmentName = 'cae-${workloadToken}-${environmentName}-${uniqueSuffix}'
var workbenchName = 'ca-${workloadToken}-${environmentName}-${uniqueSuffix}'
var imageRepository = 'migration-fleet-workbench'
var sandboxServerName = split(sandboxDatabaseHost, '.')[0]
var authSecretName = 'microsoft-provider-authentication-secret'
var acrPullRoleId = '7f951dda-4ed3-4680-a7ca-43fe172d538d'
var foundryAgentConsumerRoleId = 'eed3b665-ab3a-47b6-8f48-c9382fb1dad6'
var tags = {
  workload: 'oracle-forms-migration-fleet'
  environment: environmentName
  'managed-by': 'bicep'
  'data-classification': 'confidential'
}

resource foundryAccount 'Microsoft.CognitiveServices/accounts@2025-06-01' existing = {
  name: foundryAccountName
}

resource foundryProject 'Microsoft.CognitiveServices/accounts/projects@2025-06-01' existing = {
  parent: foundryAccount
  name: foundryProjectName
}

resource workbenchIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: identityName
  location: location
  tags: tags
}

resource registry 'Microsoft.ContainerRegistry/registries@2023-07-01' = {
  name: registryName
  location: location
  tags: tags
  sku: {
    name: 'Basic'
  }
  properties: {
    adminUserEnabled: false
    dataEndpointEnabled: false
    networkRuleBypassOptions: 'AzureServices'
    publicNetworkAccess: 'Enabled'
  }
}

resource registryPull 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(registry.id, workbenchIdentity.id, acrPullRoleId)
  scope: registry
  properties: {
    principalId: workbenchIdentity.properties.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', acrPullRoleId)
    description: 'Workbench identity can pull its application image.'
  }
}

resource foundryAgentConsumer 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(foundryProject.id, workbenchIdentity.id, foundryAgentConsumerRoleId)
  scope: foundryProject
  properties: {
    principalId: workbenchIdentity.properties.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', foundryAgentConsumerRoleId)
    description: 'Workbench identity can invoke hosted agents in this Foundry project.'
  }
}

resource logAnalytics 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: workspaceName
  location: location
  tags: tags
  properties: {
    features: {
      disableLocalAuth: true
      enableLogAccessUsingOnlyResourcePermissions: true
    }
    publicNetworkAccessForIngestion: 'Enabled'
    publicNetworkAccessForQuery: 'Enabled'
    retentionInDays: 30
    sku: {
      name: 'PerGB2018'
    }
    workspaceCapping: {
      dailyQuotaGb: 1
    }
  }
}

resource applicationInsights 'Microsoft.Insights/components@2020-02-02' = {
  name: insightsName
  location: location
  kind: 'web'
  tags: tags
  properties: {
    Application_Type: 'web'
    DisableIpMasking: false
    Flow_Type: 'Bluefield'
    IngestionMode: 'LogAnalytics'
    RetentionInDays: 30
    WorkspaceResourceId: logAnalytics.id
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
        customerId: logAnalytics.properties.customerId
        sharedKey: logAnalytics.listKeys().primarySharedKey
      }
    }
    zoneRedundant: false
  }
}

resource workbench 'Microsoft.App/containerApps@2025-01-01' = if (deployWorkbench) {
  name: workbenchName
  location: location
  tags: tags
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${workbenchIdentity.id}': {}
    }
  }
  properties: {
    managedEnvironmentId: managedEnvironment.id
    configuration: {
      activeRevisionsMode: 'Single'
      ingress: {
        allowInsecure: false
        external: true
        targetPort: 8088
        transport: 'auto'
      }
      registries: [
        {
          identity: workbenchIdentity.id
          server: registry.properties.loginServer
        }
      ]
      secrets: [
        {
          name: authSecretName
          value: entraClientSecret
        }
      ]
    }
    template: {
      containers: [
        {
          name: 'workbench'
          image: containerImage
          env: [
            {
              name: 'PORT'
              value: '8088'
            }
            {
              name: 'AZURE_CLIENT_ID'
              value: workbenchIdentity.properties.clientId
            }
            {
              name: 'FOUNDRY_AGENT_ENDPOINT'
              value: foundryAgentEndpoint
            }
            {
              name: 'APPLICATIONINSIGHTS_CONNECTION_STRING'
              value: applicationInsights.properties.ConnectionString
            }
            {
              name: 'WORKBENCH_AUTH_MODE'
              value: 'ContainerApps'
            }
            {
              name: 'WORKBENCH_AUTH_TENANT_ID'
              value: tenant().tenantId
            }
            {
              name: 'WORKBENCH_AUTH_CLIENT_ID'
              value: entraClientId
            }
            {
              // Both forms the platform may present, matching allowedAudiences below exactly.
              name: 'WORKBENCH_AUTH_AUDIENCE'
              value: '${entraClientId},api://${entraClientId}'
            }
            {
              name: 'WORKBENCH_AUTH_ISSUER'
              value: '${environment().authentication.loginEndpoint}${tenant().tenantId}/v2.0'
            }
            {
              name: 'PLATFORM_PGHOST'
              value: platformDatabaseHost
            }
            {
              name: 'PLATFORM_PGDATABASE'
              value: platformDatabaseName
            }
            {
              name: 'PLATFORM_PGUSER'
              value: platformDatabaseUser
            }
            {
              name: 'PLATFORM_PGSCHEMA'
              value: platformDatabaseSchema
            }
            {
              name: 'SANDBOX_PGHOST'
              value: sandboxDatabaseHost
            }
            {
              name: 'SANDBOX_PGDATABASE'
              value: sandboxDatabaseName
            }
            {
              name: 'SANDBOX_PGUSER'
              value: sandboxDatabaseUser
            }
            {
              name: 'SANDBOX_PGSCHEMA'
              value: 'public'
            }
            {
              name: 'SANDBOX_AZURE_TENANT_ID'
              value: tenant().tenantId
            }
            {
              name: 'SANDBOX_AZURE_SUBSCRIPTION_ID'
              value: subscription().subscriptionId
            }
            {
              name: 'SANDBOX_AZURE_RESOURCE_GROUP'
              value: resourceGroup().name
            }
            {
              name: 'SANDBOX_AZURE_RESOURCE_ID'
              value: resourceId('Microsoft.DBforPostgreSQL/flexibleServers', sandboxServerName)
            }
            {
              name: 'SANDBOX_AZURE_REGION'
              value: location
            }
          ]
          probes: [
            {
              type: 'Liveness'
              httpGet: {
                path: '/readiness'
                port: 8088
                scheme: 'HTTP'
              }
              initialDelaySeconds: 10
              periodSeconds: 30
            }
            {
              type: 'Readiness'
              httpGet: {
                path: '/readiness'
                port: 8088
                scheme: 'HTTP'
              }
              initialDelaySeconds: 5
              periodSeconds: 10
            }
          ]
          resources: {
            cpu: json('1.0')
            memory: '2Gi'
          }
        }
      ]
      // Cloned source lives on the replica's own disk and is scoped to one signed-in operator, so a
      // second replica could not serve a workspace the first one created. Keeping a warm replica
      // also removes the cold start that made the first request after a release time out.
      scale: {
        minReplicas: 1
        maxReplicas: 1
      }
    }
  }
}

resource workbenchAuth 'Microsoft.App/containerApps/authConfigs@2025-01-01' = if (deployWorkbench) {
  parent: workbench
  name: 'current'
  properties: {
    globalValidation: {
      excludedPaths: [
        '/readiness'
      ]
      redirectToProvider: 'azureactivedirectory'
      unauthenticatedClientAction: 'RedirectToLoginPage'
    }
    httpSettings: {
      requireHttps: true
    }
    identityProviders: {
      azureActiveDirectory: {
        registration: {
          clientId: entraClientId
          clientSecretSettingName: authSecretName
          openIdIssuer: '${environment().authentication.loginEndpoint}${tenant().tenantId}/v2.0'
        }
        validation: {
          allowedAudiences: [
            entraClientId
            'api://${entraClientId}'
          ]
          defaultAuthorizationPolicy: {
            allowedPrincipals: {
              identities: union(operatorPrincipalObjectIds, validationPrincipalObjectIds)
            }
          }
        }
      }
    }
    login: {
      tokenStore: {
        enabled: false
      }
    }
    platform: {
      enabled: true
    }
  }
}

output registryName string = registry.name
output registryLoginServer string = registry.properties.loginServer
output imageRepository string = imageRepository
output foundryAccountName string = foundryAccount.name
output foundryProjectName string = foundryProject.name
output workbenchIdentityName string = workbenchIdentity.name
output workbenchIdentityClientId string = workbenchIdentity.properties.clientId
output workbenchIdentityPrincipalId string = workbenchIdentity.properties.principalId
output containerAppEnvironmentName string = managedEnvironment.name
output containerAppName string = workbenchName
output workbenchFqdn string = '${workbenchName}.${managedEnvironment.properties.defaultDomain}'
output workbenchUrl string = 'https://${workbenchName}.${managedEnvironment.properties.defaultDomain}'
output applicationInsightsName string = applicationInsights.name