# Supporting Azure infrastructure

This resource-group-scoped Bicep stack is a **preview-only companion** to the Microsoft Foundry hosted agent. It does not replace the `microsoft.foundry` provider in `azure.yaml`, create a resource group, deploy the hosted agent, attach the user-assigned identity to that agent, or enable a migration execution adapter.

The companion outputs are intentionally not referenced by `azure.yaml` yet. Adding unresolved azd environment references would make the existing hosted-agent deployment unsafe before these resources exist. After an approved provision step, capture only the non-secret outputs, set the corresponding azd environment values, and then add the required bindings to the hosted service as part of the execution-adapter implementation.

## Architecture

The stack defines these supporting resources in `eastus2` by default:

- User-assigned managed identity for a future, separately hosted migration worker.
- Log Analytics workspace using `PerGB2018` with 30-day retention and a 1 GB/day ingestion cap for dev cost control.
- Workspace-based Application Insights component.
- `Standard_LRS` StorageV2 account with shared-key access and anonymous blob access disabled, OAuth as the default, HTTPS/TLS 1.2 enforcement, seven-day blob/container soft delete, versioning, and private containers named `source-evidence` and `generated-artifacts`. Its public endpoint exists, but the default-deny network ACL has no initial bypass, IP rule, or virtual-network rule.
- RBAC-mode Key Vault with purge protection, 30-day soft delete, no access policies, no initial secrets, and a default-deny public network ACL.
- One Entra-only Azure SQL logical server and one General Purpose serverless database (`GP_S_Gen5`, 1 vCore maximum, 0.5 vCore minimum, 32 GiB maximum size, 60-minute auto-pause, locally redundant backup storage). Public network access is enabled, but no firewall rule or `Allow Azure Services` rule is created.
- Storage Blob Data Contributor scoped separately to the two migration containers and Key Vault Secrets User on the vault for the future worker identity. No Owner or Contributor role is granted.
- Blob service, Key Vault, and SQL database diagnostics using supported `allLogs` and `AllMetrics` category groups where available.

Names use the short workload token, environment name, and `uniqueString(resourceGroup().id)` and contain no user-specific literal. Stateful resources therefore cannot be reused accidentally when two environments share a resource group.

## Required inputs

Set these non-secret process environment variables before parameter validation or what-if:

| Variable | Purpose |
|---|---|
| `SUPPORT_SQL_ENTRA_ADMIN_OBJECT_ID` | Object ID of the Entra group that administers the logical server. |
| `SUPPORT_SQL_ENTRA_ADMIN_LOGIN` | Display/login name paired with that group object ID. |

The defaults in `dev.bicepparam` select dev settings in `eastus2` and use an Entra group administrator. The operator selects the existing resource group explicitly when invoking the preview helper. Set `sqlEntraAdministratorPrincipalType` to `User` only when group administration is not available. The template never reads `.azure`, and no subscription, tenant, endpoint, credential, or secret value is stored in source control.

## Local validation

Local compilation restores the pinned Azure Verified Modules but does not contact an Azure subscription:

```powershell
az bicep build --file .\infra\supporting\main.bicep --stdout > $null
az bicep build-params --file .\infra\supporting\dev.bicepparam --stdout > $null
```

Run a real Azure Resource Manager what-if only from an already authenticated shell, passing the existing resource group explicitly:

```powershell
.\infra\supporting\Preview-SupportingInfrastructure.ps1 -ResourceGroupName '<existing-dev-resource-group>'
```

The helper never logs in, has no apply path, suppresses raw CLI errors, and prints only sanitized resource type/name changes. It never prints property payloads, subscription IDs, tenant IDs, endpoints, credentials, or secrets.

## Post-provision boundary

Provisioning these resources does not make conversion, schema transformation, data movement, validation, or cutover executable. `MigrationWorkbenchCatalog.ExecutionAdapterConnected` remains `false`, and the user-assigned identity is not reported as attached to the hosted agent.

Before a future adapter can use Azure SQL, an Entra administrator must connect through an explicitly approved network path, create the worker identity as a contained database user, and grant only the database roles or object permissions required by that adapter. Azure Resource Manager does not assign that contained-database permission here. The adapter must still return artifacts and a successful attestation through the existing deterministic approval gates.

## Cost drivers

- Log Analytics and Application Insights ingestion and retention volume.
- Azure SQL serverless compute while active, storage, and backup storage; auto-pause reduces idle compute but not storage charges.
- Storage capacity, transactions, data retrieval, and egress.
- Key Vault operations. The managed identity and RBAC assignments have no direct charge.

Review current Azure pricing before provisioning. No budget or cost alert is created by this preview stack.