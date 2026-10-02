targetScope = 'resourceGroup'

@description('Azure region of the existing Oracle Forms migration fleet resource group.')
@allowed([
  'eastus2'
])
param location string = 'eastus2'

@description('Existing internal Container Apps environment created by main.bicep.')
param managedEnvironmentName string = 'cae-ofmfleet-private-dev'

@description('Exact defaultDomain output returned by the created managed environment.')
@minLength(1)
param managedEnvironmentDefaultDomain string

@description('Exact inbound staticIp output returned by the created managed environment. This is DNS data, never PostgreSQL egress authorization.')
@minLength(7)
param managedEnvironmentInboundStaticIp string

@description('Existing VNet containing the private workbench and source hosts.')
param existingVnetName string = 'vnet-ofm-forms6i-j6mrrerz'

@description('Exact privateEndpointSubnetId output returned by main.bicep.')
@minLength(1)
param privateEndpointSubnetId string

@description('Existing Forms 6i VM NSG. Only one source-gateway ingress rule is added.')
param existingFormsNsgName string = 'nsg-ofm-forms6i-j6mrrerz'

@description('Existing user-assigned identity shared with the public workbench.')
param existingWorkbenchIdentityName string = 'id-ofmfleet-web-dev-ykbpnrpd'

@description('Existing trusted Azure Container Registry.')
param existingRegistryName string = 'acrofmfleedevykbpnrpd'

@description('Existing Application Insights component shared with the public workbench.')
param existingApplicationInsightsName string = 'appi-ofmfleet-web-dev-ykbpnrpd'

@description('Immutable workbench image from the trusted main release, including @sha256:<digest>.')
@minLength(80)
param containerImage string

@description('Existing workbench Entra application client ID used by Container Apps authentication.')
param workbenchAuthClientId string = '0ff0fa49-fce8-4801-ab93-e862a62fd6ab'

@secure()
@description('Existing workbench Entra application credential supplied only by the trusted release workflow.')
param workbenchAuthClientSecret string

@description('Microsoft Entra object IDs allowed to use the confidential private workbench.')
@minLength(1)
param operatorPrincipalObjectIds array

@description('Dedicated validation principal object IDs permitted during trusted release verification.')
param validationPrincipalObjectIds array = []

@description('Dedicated validation client application IDs permitted during trusted release verification.')
param validationClientApplicationIds array = []

@description('Client ID of the separately approved source-gateway API app registration.')
@minLength(36)
@maxLength(36)
param sourceGatewayApplicationClientId string

@description('Existing PostgreSQL Flexible Server host for platform authorization state.')
param platformDatabaseHost string = 'pg-ofmfleet-dev-ykbpnrpd.postgres.database.azure.com'

@description('Existing database holding platform authorization state.')
param platformDatabaseName string = 'ofm_platform'

@description('Existing Entra database principal name. Never a password.')
param platformDatabaseUser string = 'id-ofmfleet-web-dev-ykbpnrpd'

@description('Existing isolated platform-state schema.')
param platformDatabaseSchema string = 'ofm_platform'

@description('Existing sandbox PostgreSQL database host.')
param sandboxDatabaseHost string = 'pg-ofmfleet-dev-ykbpnrpd.postgres.database.azure.com'

@description('Existing isolated sandbox database.')
param sandboxDatabaseName string = 'ofm_dotnet_pilot'

@description('Existing Entra sandbox database principal name. Never a password.')
param sandboxDatabaseUser string = 'id-ofmfleet-web-dev-ykbpnrpd'

@description('Existing Foundry hosted-agent endpoint.')
@minLength(1)
param foundryAgentEndpoint string

@description('Existing Azure OpenAI endpoint used by the deployed workbench.')
param azureOpenAiEndpoint string = 'https://cog-czusrhcg4gpm2.openai.azure.com/'

@description('Existing conversion model deployment.')
param azureAiModelDeploymentName string = 'gpt-5.4-mini'

@description('Existing review model deployment.')
param azureAiReviewModelDeploymentName string = 'gpt-5.6-sol'

param containerAppName string = 'ca-ofmfleet-private-dev'
param sourceGatewayPrivateDnsZoneName string = 'ofm.source.internal'
param sourceGatewayHostName string = 'gateway'
param postgresPrivateDnsZoneName string = 'privatelink.postgres.database.azure.com'
param postgresPrivateEndpointName string = 'pe-ofmfleet-postgres-dev'
@allowed([
  '10.246.0.4'
])
param sourceGatewayPrivateIp string = '10.246.0.4'
@allowed([
  '10.246.0.64/27'
])
param infrastructureSubnetPrefix string = '10.246.0.64/27'

var authSecretName = 'microsoft-provider-authentication-secret'
var sourceGatewayAuthority = 'https://${sourceGatewayHostName}.${sourceGatewayPrivateDnsZoneName}/'
var sourceGatewayTokenScope = 'api://${sourceGatewayApplicationClientId}/.default'
var appFqdn = '${containerAppName}.${managedEnvironmentDefaultDomain}'
var sandboxServerName = split(sandboxDatabaseHost, '.')[0]
var tags = {
  workload: 'oracle-forms-migration-fleet'
  environment: 'dev'
  component: 'private-source-gateway-workbench'
  'managed-by': 'bicep'
  'data-classification': 'confidential'
}

resource existingManagedEnvironment 'Microsoft.App/managedEnvironments@2025-01-01' existing = {
  name: managedEnvironmentName
}

resource existingVnet 'Microsoft.Network/virtualNetworks@2024-05-01' existing = {
  name: existingVnetName
}

resource existingFormsNsg 'Microsoft.Network/networkSecurityGroups@2024-05-01' existing = {
  name: existingFormsNsgName
}

resource existingWorkbenchIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' existing = {
  name: existingWorkbenchIdentityName
}

resource existingRegistry 'Microsoft.ContainerRegistry/registries@2023-07-01' existing = {
  name: existingRegistryName
}

resource existingApplicationInsights 'Microsoft.Insights/components@2020-02-02' existing = {
  name: existingApplicationInsightsName
}

resource existingPostgresServer 'Microsoft.DBforPostgreSQL/flexibleServers@2024-08-01' existing = {
  name: sandboxServerName
}

resource allowPrivateWorkbenchToGateway 'Microsoft.Network/networkSecurityGroups/securityRules@2024-05-01' = {
  parent: existingFormsNsg
  name: 'Allow-Private-Workbench-Source-Gateway'
  properties: {
    priority: 105
    access: 'Allow'
    direction: 'Inbound'
    protocol: 'Tcp'
    sourcePortRange: '*'
    destinationPortRange: '443'
    sourceAddressPrefix: infrastructureSubnetPrefix
    destinationAddressPrefix: '${sourceGatewayPrivateIp}/32'
    description: 'Internal Container Apps workbench to the Forms 6i source gateway only.'
  }
}

resource acaPrivateDnsZone 'Microsoft.Network/privateDnsZones@2024-06-01' = {
  name: managedEnvironmentDefaultDomain
  location: 'global'
  tags: tags
}

resource acaPrivateDnsLink 'Microsoft.Network/privateDnsZones/virtualNetworkLinks@2024-06-01' = {
  parent: acaPrivateDnsZone
  name: 'link-${existingVnetName}'
  location: 'global'
  properties: {
    registrationEnabled: false
    virtualNetwork: {
      id: existingVnet.id
    }
  }
}

resource acaWildcardRecord 'Microsoft.Network/privateDnsZones/A@2024-06-01' = {
  parent: acaPrivateDnsZone
  name: '*'
  properties: {
    ttl: 60
    aRecords: [
      {
        ipv4Address: managedEnvironmentInboundStaticIp
      }
    ]
  }
}

resource sourceGatewayPrivateDnsZone 'Microsoft.Network/privateDnsZones@2024-06-01' = {
  name: sourceGatewayPrivateDnsZoneName
  location: 'global'
  tags: tags
}

resource sourceGatewayPrivateDnsLink 'Microsoft.Network/privateDnsZones/virtualNetworkLinks@2024-06-01' = {
  parent: sourceGatewayPrivateDnsZone
  name: 'link-${existingVnetName}'
  location: 'global'
  properties: {
    registrationEnabled: false
    virtualNetwork: {
      id: existingVnet.id
    }
  }
}

resource sourceGatewayRecord 'Microsoft.Network/privateDnsZones/A@2024-06-01' = {
  parent: sourceGatewayPrivateDnsZone
  name: sourceGatewayHostName
  properties: {
    ttl: 300
    aRecords: [
      {
        ipv4Address: sourceGatewayPrivateIp
      }
    ]
  }
}

resource postgresPrivateDnsZone 'Microsoft.Network/privateDnsZones@2024-06-01' = {
  name: postgresPrivateDnsZoneName
  location: 'global'
  tags: tags
}

resource postgresPrivateDnsLink 'Microsoft.Network/privateDnsZones/virtualNetworkLinks@2024-06-01' = {
  parent: postgresPrivateDnsZone
  name: 'link-${existingVnetName}'
  location: 'global'
  properties: {
    registrationEnabled: false
    virtualNetwork: {
      id: existingVnet.id
    }
  }
}

resource postgresPrivateEndpoint 'Microsoft.Network/privateEndpoints@2024-05-01' = {
  name: postgresPrivateEndpointName
  location: location
  tags: tags
  properties: {
    subnet: {
      id: privateEndpointSubnetId
    }
    privateLinkServiceConnections: [
      {
        name: 'postgresqlServer'
        properties: {
          privateLinkServiceId: existingPostgresServer.id
          groupIds: [
            'postgresqlServer'
          ]
          requestMessage: 'Private source-gateway workbench PostgreSQL route.'
        }
      }
    ]
  }
}

resource postgresPrivateDnsZoneGroup 'Microsoft.Network/privateEndpoints/privateDnsZoneGroups@2024-05-01' = {
  parent: postgresPrivateEndpoint
  name: 'default'
  properties: {
    privateDnsZoneConfigs: [
      {
        name: 'postgresqlServer'
        properties: {
          privateDnsZoneId: postgresPrivateDnsZone.id
        }
      }
    ]
  }
}

resource privateWorkbench 'Microsoft.App/containerApps@2025-01-01' = {
  name: containerAppName
  location: location
  tags: tags
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${existingWorkbenchIdentity.id}': {}
    }
  }
  properties: {
    managedEnvironmentId: existingManagedEnvironment.id
    workloadProfileName: 'Consumption'
    configuration: {
      activeRevisionsMode: 'Single'
      ingress: {
        allowInsecure: false
        external: true
        targetPort: 8088
        transport: 'auto'
        stickySessions: {
          affinity: 'sticky'
        }
      }
      registries: [
        {
          identity: existingWorkbenchIdentity.id
          server: existingRegistry.properties.loginServer
        }
      ]
      secrets: [
        {
          name: authSecretName
          value: workbenchAuthClientSecret
        }
      ]
    }
    template: {
      containers: [
        {
          name: 'workbench'
          image: containerImage
          env: [
            { name: 'PORT', value: '8088' }
            { name: 'AZURE_CLIENT_ID', value: existingWorkbenchIdentity.properties.clientId }
            { name: 'FOUNDRY_AGENT_ENDPOINT', value: foundryAgentEndpoint }
            { name: 'APPLICATIONINSIGHTS_CONNECTION_STRING', value: existingApplicationInsights.properties.ConnectionString }
            { name: 'WORKBENCH_AUTH_MODE', value: 'ContainerApps' }
            { name: 'WORKBENCH_AUTH_TENANT_ID', value: tenant().tenantId }
            { name: 'WORKBENCH_AUTH_CLIENT_ID', value: workbenchAuthClientId }
            { name: 'WORKBENCH_AUTH_AUDIENCE', value: '${workbenchAuthClientId},api://${workbenchAuthClientId}' }
            { name: 'WORKBENCH_AUTH_ISSUER', value: '${environment().authentication.loginEndpoint}${tenant().tenantId}/v2.0' }
            { name: 'PLATFORM_PGHOST', value: platformDatabaseHost }
            { name: 'PLATFORM_PGDATABASE', value: platformDatabaseName }
            { name: 'PLATFORM_PGUSER', value: platformDatabaseUser }
            { name: 'PLATFORM_PGSCHEMA', value: platformDatabaseSchema }
            { name: 'TARGET_BACKEND_STACK', value: 'AspNetCore' }
            { name: 'SANDBOX_PGHOST', value: sandboxDatabaseHost }
            { name: 'SANDBOX_PGDATABASE', value: sandboxDatabaseName }
            { name: 'SANDBOX_PGUSER', value: sandboxDatabaseUser }
            { name: 'SANDBOX_PGSCHEMA', value: 'public' }
            { name: 'SANDBOX_AZURE_TENANT_ID', value: tenant().tenantId }
            { name: 'SANDBOX_AZURE_SUBSCRIPTION_ID', value: subscription().subscriptionId }
            { name: 'SANDBOX_AZURE_RESOURCE_GROUP', value: resourceGroup().name }
            { name: 'SANDBOX_AZURE_RESOURCE_ID', value: resourceId('Microsoft.DBforPostgreSQL/flexibleServers', sandboxServerName) }
            { name: 'SANDBOX_AZURE_REGION', value: location }
            { name: 'ASPNETCORE_ENVIRONMENT', value: 'Production' }
            { name: 'AZURE_OPENAI_ENDPOINT', value: azureOpenAiEndpoint }
            { name: 'AZURE_AI_MODEL_DEPLOYMENT_NAME', value: azureAiModelDeploymentName }
            { name: 'AZURE_AI_REVIEW_MODEL_DEPLOYMENT_NAME', value: azureAiReviewModelDeploymentName }
            { name: 'SourceGateway__Authority', value: sourceGatewayAuthority }
            { name: 'SourceGateway__TokenScope', value: sourceGatewayTokenScope }
            { name: 'WORKBENCH_SOURCE_ROOT', value: '/tmp/workbench-sources' }
          ]
          probes: [
            {
              type: 'Liveness'
              httpGet: { path: '/readiness', port: 8088, scheme: 'HTTP' }
              initialDelaySeconds: 10
              periodSeconds: 30
            }
            {
              type: 'Readiness'
              httpGet: { path: '/readiness', port: 8088, scheme: 'HTTP' }
              initialDelaySeconds: 5
              periodSeconds: 10
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
        rules: [
          {
            name: 'private-http'
            http: {
              metadata: {
                concurrentRequests: '10'
              }
            }
          }
        ]
      }
    }
  }
}

resource privateWorkbenchAuth 'Microsoft.App/containerApps/authConfigs@2025-01-01' = {
  parent: privateWorkbench
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
          clientId: workbenchAuthClientId
          clientSecretSettingName: authSecretName
          openIdIssuer: '${environment().authentication.loginEndpoint}${tenant().tenantId}/v2.0'
        }
        validation: {
          allowedAudiences: [
            workbenchAuthClientId
            'api://${workbenchAuthClientId}'
          ]
          defaultAuthorizationPolicy: {
            allowedApplications: union([workbenchAuthClientId], validationClientApplicationIds)
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

output privateWorkbenchUrl string = 'https://${appFqdn}'
output requiredWorkbenchRedirectUri string = 'https://${appFqdn}/.auth/login/aad/callback'
output sourceGatewayAuthority string = sourceGatewayAuthority
output sourceGatewayTokenScope string = sourceGatewayTokenScope
output postgresPrivateEndpointId string = postgresPrivateEndpoint.id
output postgresPrivateDnsZoneId string = postgresPrivateDnsZone.id