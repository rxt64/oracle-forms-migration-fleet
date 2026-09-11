targetScope = 'resourceGroup'

@description('Azure region for the supporting resources.')
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

@description('Microsoft Entra object ID for the Azure SQL administrator. No SQL login is created.')
@minLength(36)
@maxLength(36)
param sqlEntraAdministratorObjectId string

@description('Microsoft Entra login name for the Azure SQL administrator.')
@minLength(1)
param sqlEntraAdministratorLogin string

@description('Microsoft Entra principal type for the Azure SQL administrator.')
@allowed([
  'Group'
  'User'
])
param sqlEntraAdministratorPrincipalType string = 'Group'

@description('Azure SQL database name.')
param sqlDatabaseName string = 'migration-sandbox'

var uniqueSuffix = take(uniqueString(resourceGroup().id), 8)
var statefulWorkloadToken = take(workloadToken, 7)
var environmentToken = take(environmentName, 3)
var identityName = 'id-${workloadToken}-${environmentName}-${uniqueSuffix}'
var logAnalyticsName = 'log-${workloadToken}-${environmentName}-${uniqueSuffix}'
var applicationInsightsName = 'appi-${workloadToken}-${environmentName}-${uniqueSuffix}'
var storageAccountName = 'st${statefulWorkloadToken}${environmentToken}${uniqueSuffix}'
var keyVaultName = 'kv-${statefulWorkloadToken}-${environmentToken}-${uniqueSuffix}'
var sqlServerName = 'sql-${workloadToken}-${environmentName}-${uniqueSuffix}'
var diagnosticSettingName = 'send-to-${logAnalyticsName}'
var storageBlobDataContributorRoleId = 'ba92f5b4-2d11-453d-a403-e96b0029c9fe'
var keyVaultSecretsUserRoleId = '4633458b-17de-408a-b874-0445c86b69e6'
var tags = {
  workload: 'oracle-forms-migration-fleet'
  environment: environmentName
  'managed-by': 'bicep'
  'data-classification': 'confidential'
}

module migrationWorkerIdentity 'br/public:avm/res/managed-identity/user-assigned-identity:0.6.0' = {
  name: 'deploy-${identityName}'
  params: {
    name: identityName
    location: location
    tags: tags
  }
}

module logAnalytics 'br/public:avm/res/operational-insights/workspace:0.16.0' = {
  name: 'deploy-${logAnalyticsName}'
  params: {
    name: logAnalyticsName
    location: location
    dailyQuotaGb: '1'
    dataRetention: 30
    skuName: 'PerGB2018'
    features: {
      disableLocalAuth: true
      enableLogAccessUsingOnlyResourcePermissions: true
      immediatePurgeDataOn30Days: true
    }
    forceCmkForQuery: false
    tags: tags
  }
}

module applicationInsights 'br/public:avm/res/insights/component:0.8.0' = {
  name: 'deploy-${applicationInsightsName}'
  params: {
    name: applicationInsightsName
    location: location
    workspaceResourceId: logAnalytics.outputs.resourceId
    applicationType: 'web'
    disableIpMasking: false
    kind: 'web'
    retentionInDays: 30
    tags: tags
  }
}

resource storageAccount 'Microsoft.Storage/storageAccounts@2025-06-01' = {
  name: storageAccountName
  location: location
  tags: tags
  sku: {
    name: 'Standard_LRS'
  }
  kind: 'StorageV2'
  properties: {
    accessTier: 'Hot'
    allowBlobPublicAccess: false
    allowSharedKeyAccess: false
    defaultToOAuthAuthentication: true
    minimumTlsVersion: 'TLS1_2'
    publicNetworkAccess: 'Enabled'
    supportsHttpsTrafficOnly: true
    networkAcls: {
      bypass: 'None'
      defaultAction: 'Deny'
      ipRules: []
      virtualNetworkRules: []
    }
  }
}

resource blobService 'Microsoft.Storage/storageAccounts/blobServices@2025-06-01' = {
  parent: storageAccount
  name: 'default'
  properties: {
    containerDeleteRetentionPolicy: {
      enabled: true
      days: 7
    }
    deleteRetentionPolicy: {
      enabled: true
      days: 7
    }
    isVersioningEnabled: true
  }
}

resource sourceEvidenceContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2025-06-01' = {
  parent: blobService
  name: 'source-evidence'
  properties: {
    publicAccess: 'None'
  }
}

resource generatedArtifactsContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2025-06-01' = {
  parent: blobService
  name: 'generated-artifacts'
  properties: {
    publicAccess: 'None'
  }
}

resource sourceEvidenceBlobDataContributor 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(sourceEvidenceContainer.id, identityName, storageBlobDataContributorRoleId)
  scope: sourceEvidenceContainer
  properties: {
    principalId: migrationWorkerIdentity.outputs.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', storageBlobDataContributorRoleId)
    description: 'Migration worker identity can read and write source evidence.'
  }
}

resource generatedArtifactsBlobDataContributor 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(generatedArtifactsContainer.id, identityName, storageBlobDataContributorRoleId)
  scope: generatedArtifactsContainer
  properties: {
    principalId: migrationWorkerIdentity.outputs.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', storageBlobDataContributorRoleId)
    description: 'Migration worker identity can read and write generated artifacts.'
  }
}

resource blobDiagnostics 'Microsoft.Insights/diagnosticSettings@2021-05-01-preview' = {
  name: diagnosticSettingName
  scope: blobService
  properties: {
    workspaceId: logAnalytics.outputs.resourceId
    logs: [
      {
        categoryGroup: 'allLogs'
        enabled: true
      }
    ]
    metrics: [
      {
        category: 'AllMetrics'
        enabled: true
      }
    ]
  }
}

module keyVault 'br/public:avm/res/key-vault/vault:0.14.0' = {
  name: 'deploy-${keyVaultName}'
  params: {
    name: keyVaultName
    location: location
    sku: 'standard'
    enableRbacAuthorization: true
    enableSoftDelete: true
    enablePurgeProtection: true
    softDeleteRetentionInDays: 30
    enableVaultForDeployment: false
    enableVaultForDiskEncryption: false
    enableVaultForTemplateDeployment: false
    publicNetworkAccess: 'Enabled'
    networkAcls: {
      bypass: 'None'
      defaultAction: 'Deny'
      ipRules: []
      virtualNetworkRules: []
    }
    secrets: []
    roleAssignments: [
      {
        name: guid(resourceGroup().id, keyVaultName, identityName, keyVaultSecretsUserRoleId)
        principalId: migrationWorkerIdentity.outputs.principalId
        principalType: 'ServicePrincipal'
        roleDefinitionIdOrName: keyVaultSecretsUserRoleId
        description: 'Migration worker identity can read secret values supplied after provisioning.'
      }
    ]
    diagnosticSettings: [
      {
        name: diagnosticSettingName
        workspaceResourceId: logAnalytics.outputs.resourceId
        logCategoriesAndGroups: [
          {
            categoryGroup: 'allLogs'
          }
        ]
        metricCategories: [
          {
            category: 'AllMetrics'
          }
        ]
      }
    ]
    tags: tags
  }
}

resource sqlServer 'Microsoft.Sql/servers@2023-08-01' = {
  name: sqlServerName
  location: location
  tags: tags
  properties: {
    administrators: {
      administratorType: 'ActiveDirectory'
      azureADOnlyAuthentication: true
      login: sqlEntraAdministratorLogin
      principalType: sqlEntraAdministratorPrincipalType
      sid: sqlEntraAdministratorObjectId
      tenantId: tenant().tenantId
    }
    minimalTlsVersion: '1.2'
    publicNetworkAccess: 'Enabled'
  }
}

resource sqlDatabase 'Microsoft.Sql/servers/databases@2023-08-01' = {
  parent: sqlServer
  name: sqlDatabaseName
  location: location
  tags: tags
  sku: {
    name: 'GP_S_Gen5'
    tier: 'GeneralPurpose'
    family: 'Gen5'
    capacity: 1
  }
  properties: {
    autoPauseDelay: 60
    maxSizeBytes: 34359738368
    minCapacity: json('0.5')
    readScale: 'Disabled'
    requestedBackupStorageRedundancy: 'Local'
    zoneRedundant: false
  }
}

resource sqlDatabaseDiagnostics 'Microsoft.Insights/diagnosticSettings@2021-05-01-preview' = {
  name: diagnosticSettingName
  scope: sqlDatabase
  properties: {
    workspaceId: logAnalytics.outputs.resourceId
    logs: [
      {
        categoryGroup: 'allLogs'
        enabled: true
      }
    ]
    metrics: [
      {
        category: 'AllMetrics'
        enabled: true
      }
    ]
  }
}

output migrationWorkerIdentityName string = migrationWorkerIdentity.outputs.name
output migrationWorkerIdentityClientId string = migrationWorkerIdentity.outputs.clientId
output logAnalyticsWorkspaceName string = logAnalytics.outputs.name
output applicationInsightsName string = applicationInsights.outputs.name
output storageAccountName string = storageAccount.name
output storageBlobEndpoint string = storageAccount.properties.primaryEndpoints.blob
output sourceEvidenceContainerName string = sourceEvidenceContainer.name
output generatedArtifactsContainerName string = generatedArtifactsContainer.name
output keyVaultName string = keyVault.outputs.name
output keyVaultUri string = keyVault.outputs.uri
output sqlServerName string = sqlServer.name
output sqlServerFqdn string = sqlServer.properties.fullyQualifiedDomainName
output sqlDatabaseName string = sqlDatabase.name