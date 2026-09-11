using './main.bicep'

param location = 'eastus2'
param workloadToken = 'ofmfleet'
param environmentName = 'dev'
param sqlEntraAdministratorObjectId = readEnvironmentVariable('SUPPORT_SQL_ENTRA_ADMIN_OBJECT_ID')
param sqlEntraAdministratorLogin = readEnvironmentVariable('SUPPORT_SQL_ENTRA_ADMIN_LOGIN')
param sqlEntraAdministratorPrincipalType = 'Group'
param sqlDatabaseName = 'migration-sandbox'