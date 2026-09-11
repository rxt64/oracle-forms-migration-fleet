using './main.bicep'

param location = 'eastus2'
param workloadToken = 'ofmfleet'
param environmentName = 'dev'
param foundryAccountName = 'cog-czusrhcg4gpm2'
param foundryProjectName = 'oracle-forms-migration-fleet-dev'
param operatorPrincipalObjectIds = [
	'dd84da40-177f-47b9-9c4d-4b657ee4de36'
]