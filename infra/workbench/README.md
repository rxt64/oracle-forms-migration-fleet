# Azure-hosted operator workbench

This stack hosts the React/ASP.NET workbench on Azure Container Apps while preserving the existing Microsoft Foundry hosted agent. It creates:

- Azure Container Registry with admin and anonymous access disabled.
- A user-assigned managed identity with `AcrPull` on that registry and `Foundry Agent Consumer` on the existing Foundry project.
- A Log Analytics workspace and workspace-based Application Insights component.
- A Consumption-plan Container Apps environment and HTTPS-only Container App.
- Container Apps built-in authentication backed by a dedicated single-tenant Microsoft Entra application.

The browser never receives an Azure token or the hosted-agent endpoint. The ASP.NET backend acquires an Entra token with managed identity and forwards a narrow, non-streaming request to the configured agent. Prompts that resemble credentials are rejected before token acquisition. No Storage, Key Vault, SQL, or migration-execution permission is granted to the public web process.

## Preview

From an already authenticated Azure CLI session:

```powershell
.\infra\workbench\Preview-WorkbenchInfrastructure.ps1 `
  -ResourceGroupName 'rg-oracle-forms-migration-fleet-dev-b9f0e875'
```

The preview compiles Bicep, runs resource-group what-if, and prints only sanitized resource type/name changes.

## Deploy

```powershell
.\infra\workbench\Deploy-Workbench.ps1 `
  -ResourceGroupName 'rg-oracle-forms-migration-fleet-dev-b9f0e875' `
  -FoundryAgentEndpoint 'https://cog-czusrhcg4gpm2.services.ai.azure.com/api/projects/oracle-forms-migration-fleet-dev/agents/oracle-forms-migration-fleet/endpoint/protocols/openai/responses?api-version=v1'
```

The command is intentionally two-phase. It first provisions resources without a public app, builds the image inside ACR, creates or updates the Entra application and rotates only its dedicated credential, then deploys the authenticated Container App with the Foundry connection enabled. The credential is passed as a secure ARM parameter and a Container Apps secret; it is never printed or stored in the repository.

Deployment requires permission to create resource-group deployments and role assignments, push ACR builds, and create an Entra application. It never logs in on the user's behalf.

The separate `infra/supporting` stack remains the preview-only destination foundation for Blob Storage, Key Vault, and Azure SQL. Those services are deliberately not granted to the web identity and do not imply that a migration execution adapter exists.