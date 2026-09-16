using './main.bicep'

param location = 'eastus2'
param workloadToken = 'ofmfleet'
param environmentName = 'dev'
param foundryAccountName = 'cog-czusrhcg4gpm2'
param foundryProjectName = 'oracle-forms-migration-fleet-dev'
param operatorPrincipalObjectIds = split(readEnvironmentVariable('WORKBENCH_OPERATOR_PRINCIPAL_OBJECT_IDS'), ',')