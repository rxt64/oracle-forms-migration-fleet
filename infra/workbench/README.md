# Azure-hosted operator workbench

This stack hosts the React/ASP.NET workbench on Azure Container Apps while preserving the existing Microsoft Foundry hosted agent. It creates:

- Azure Container Registry with admin and anonymous access disabled.
- A user-assigned managed identity with `AcrPull` on that registry and `Foundry Agent Consumer` on the existing Foundry project.
- A Log Analytics workspace and workspace-based Application Insights component.
- A Consumption-plan Container Apps environment and HTTPS-only Container App.
- Container Apps built-in authentication backed by a dedicated single-tenant Microsoft Entra application.

The browser never receives an Azure token or the hosted-agent endpoint. The ASP.NET backend acquires an Entra token with managed identity and forwards a narrow, non-streaming request to the configured agent. Prompts that resemble credentials are rejected before token acquisition. No Storage, Key Vault, Azure SQL, or production permission is granted to the public web process. The identity is separately pre-provisioned inside the sandbox PostgreSQL server; host-fixed environment variables bind that one destination, and writes still require explicit execution approval.

## Preview

From an already authenticated Azure CLI session:

```powershell
.\infra\workbench\Preview-WorkbenchInfrastructure.ps1 `
  -ResourceGroupName 'rg-oracle-forms-migration-fleet-dev-b9f0e875'
```

The preview compiles Bicep, runs resource-group what-if, and prints only sanitized resource type/name changes.

## Deploy application revisions

```powershell
gh workflow run deploy.yml --repo rxt64/oracle-forms-migration-fleet -f migrate-data=true
```

The GitHub runner builds a commit-addressed image, authenticates to Azure with OIDC, configures the host-owned sandbox target and model deployments, and updates the Container App. Pushes to `main` run the same path with sandbox configuration enabled. Deployable images are never built from a workstation.

`Deploy-Workbench.ps1` is retained only for privileged first-time infrastructure and Entra bootstrap in an empty environment. It is not the application release path; routine releases must use the GitHub workflow. Bootstrap requires permission to create resource-group deployments and role assignments and to create an Entra application. It never logs in on the user's behalf.

For a new environment, create the foundation, let GitHub build the image, and then consume that exact
commit tag during the privileged auth bootstrap:

```powershell
$commit = (git rev-parse origin/main).Trim()
$tag = $commit.Substring(0, 12)

./infra/workbench/Deploy-Workbench.ps1 `
  -ResourceGroupName '<resource-group>' `
  -FoundryAgentEndpoint '<canonical-hosted-agent-endpoint>' `
  -FoundationOnly

gh workflow run deploy.yml --repo rxt64/oracle-forms-migration-fleet --ref main `
  -f deploy-app=false -f migrate-data=false
# Wait for the build-only workflow to succeed before continuing.

./infra/workbench/Deploy-Workbench.ps1 `
  -ResourceGroupName '<resource-group>' `
  -FoundryAgentEndpoint '<canonical-hosted-agent-endpoint>' `
  -ImageTag $tag

gh workflow run deploy.yml --repo rxt64/oracle-forms-migration-fleet --ref main `
  -f deploy-app=true -f migrate-data=true
```

The script refuses a missing or non-commit-shaped image tag and never invokes an image build.

The separate `infra/supporting` stack remains the preview-only destination foundation for Blob Storage, Key Vault, and Azure SQL. Those services are deliberately not granted to the web identity. PostgreSQL sandbox execution is configured independently.