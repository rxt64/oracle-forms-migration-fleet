using './main.bicep'

param location = 'eastus2'
param workloadToken = 'ofmfleet'
param environmentName = 'dev'
param foundryAccountName = 'cog-czusrhcg4gpm2'
param foundryProjectName = 'oracle-forms-migration-fleet-dev'
param operatorPrincipalObjectIds = split(readEnvironmentVariable('WORKBENCH_OPERATOR_PRINCIPAL_OBJECT_IDS'), ',')
param validationPrincipalObjectIds = [
	'3f74daff-00b7-4606-bdb8-91aba713a54d'
]
param platformDatabaseHost = 'pg-ofmfleet-dev-ykbpnrpd.postgres.database.azure.com'
param platformDatabaseName = 'ofm_platform'
param platformDatabaseUser = 'id-ofmfleet-web-dev-ykbpnrpd'
param platformDatabaseSchema = 'ofm_platform'
param sandboxDatabaseHost = 'pg-ofmfleet-dev-ykbpnrpd.postgres.database.azure.com'
param sandboxDatabaseName = 'postgres'
param sandboxDatabaseUser = 'id-ofmfleet-web-dev-ykbpnrpd'