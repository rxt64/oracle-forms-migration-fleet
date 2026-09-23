# Generated .NET pilot platform foundation

This stack prepares reusable Azure infrastructure for a generated ASP.NET Core application without
building or deploying that application. It reuses the existing registry, Container Apps environment,
PostgreSQL Flexible Server, Log Analytics destination, and workbench identity. It does not modify or
replace `ca-ofmfleet-mig-dev-ykbpnrpd`, the existing Java migration demo.

## Default behavior

`Setup-DotNetPilot.ps1` is preview-only unless `-ApplyFoundation` is supplied. The Bicep parameter file
sets `deployTarget = false`, and the setup script always overrides that parameter to false. It cannot
accept an image reference, build an image, deploy generated code, or apply product schema SQL.

Preview from an already authenticated Azure CLI session:

```powershell
./infra/dotnet-pilot/Setup-DotNetPilot.ps1 `
  -SubscriptionId 'd4394e57-c076-4c92-a870-5de6bf44f255' `
  -ResourceGroupName 'rg-oracle-forms-migration-fleet-dev-b9f0e875'
```

Do not use `-ApplyFoundation` until the operator approves the exact mutations and cost below.

Read-only ARM what-if against the authorized subscription/resource group on September 21, 2026
completed with status `Succeeded`, no diagnostics, no Modify/Delete changes, and these six Creates:

- `Microsoft.ManagedIdentity/userAssignedIdentities/id-ofmfleet-dotnet-dev-ykbpnrpd`
- `Microsoft.Storage/storageAccounts/stofmdotdevykbpnrpd`
- its `fileServices/default` child and `shares/workbench-artifacts` child
- `Microsoft.App/managedEnvironments/cae-ofmfleet-dev-ykbpnrpd/storages/dotnet-pilot-artifacts`
- one `AcrPull` role assignment under `acrofmfleedevykbpnrpd`

All four existing Container Apps, including `ca-ofmfleet-mig-dev-ykbpnrpd`, were reported as `Ignore`.
PostgreSQL database/principal/grant operations occur only in the explicitly approved setup-script apply
path and therefore are not represented in ARM what-if.

## Exact proposed mutations

Foundation application (`deployTarget=false`) adds only:

| Resource | Name | Purpose |
|---|---|---|
| User-assigned identity | `id-ofmfleet-dotnet-dev-ykbpnrpd` | Runtime ACR pull and PostgreSQL Entra principal for the generated app only. |
| Storage account | `stofmdotdevykbpnrpd` | Standard LRS account for classic Azure Files; TLS 1.2 and HTTPS required, public blob access disabled. |
| File service/share | `default/workbench-artifacts` | Transaction Optimized SMB share, 20 GiB quota, seven-day soft delete. |
| ACA environment storage | `cae-ofmfleet-dev-ykbpnrpd/dotnet-pilot-artifacts` | Read/write Azure Files link used by the workbench volume. |
| Role assignment | `AcrPull` on `acrofmfleedevykbpnrpd` | Assigned only to the new runtime identity. |
| PostgreSQL database | `ofm_dotnet_pilot` | Dedicated application database on the existing server; never `postgres` or `ofm_platform`. |
| PostgreSQL principal/grants | `id-ofmfleet-dotnet-dev-ykbpnrpd` | Database owner; `CONNECT`, `CREATE`, and `TEMPORARY` only in the dedicated database, plus `USAGE`/`CREATE` on its `public` schema. Public database connect and public schema create are revoked. |

The existing workbench template is prepared to mount that environment storage at
`/mnt/workbench-sources` and set `WORKBENCH_SOURCE_ROOT` to the mount. Applying that separate workbench
template later creates a new workbench revision; it is not part of foundation setup.

After the product owns a controlled deployment adapter and supplies an exact verified image digest,
`deployTarget=true` additionally proposes:

| Resource | Name | Purpose |
|---|---|---|
| Container App | `ca-ofmfleet-dotnet-dev-ykbpnrpd` | New HTTPS-only .NET target, 0-1 replicas, 0.5 vCPU/1 GiB, `/healthz` startup/liveness/readiness probes. |
| Custom role | `Oracle Forms Migration Fleet Generated Application Deployer` | Only `Microsoft.App/containerApps/read`, `write`, and `revisions/read`; no delete, IAM, registry, environment, or database action. |
| Role assignment | Custom role on the new Container App resource ID | Assigned to `id-ofmfleet-web-dev-ykbpnrpd`; it cannot update the Java demo or another app. |

The target template rejects mutable tags operationally: the eventual gateway must pass an ACR reference
ending in `@sha256:<64 lowercase hex characters>`. GitHub must build and push that image. No workstation
Docker or ACR build is part of this stack.

## Incremental cost estimate

Estimate date: **September 21, 2026**, USD pay-as-you-go public list pricing. The Azure Retail Pricing API
returned no rows for the ACA Consumption and Standard LRS Azure Files filters, so the values below use
the public Azure pricing pages and explicit formulas. Contract pricing, taxes, Log Analytics ingestion,
cross-region network transfer, backups, and future PostgreSQL storage expansion are not included.

### Before target deployment

- Managed identity, RBAC, custom role definition, and ACA environment storage link: no direct charge.
- Dedicated database on the existing `Standard_B1ms`, 32 GiB PostgreSQL server: no new compute resource
  and no immediate storage charge while the server remains within its existing allocation.
- Azure Files Transaction Optimized LRS: `$0.0600/GiB-month` used storage. Five GiB is about `$0.30/month`;
  the full 20 GiB quota, if used, is about `$1.20/month`.
- Example monthly transactions: 100,000 writes + 100,000 lists + 1,000,000 reads + 100,000 other
  operations add about `$0.47`. A heavier one-million-of-each pattern adds about `$3.30`.
- Expected foundation range: about **$0.30-$4.50/month**, driven by stored bytes and operations.

### After target deployment

ACA Consumption public rates are `$0.000024/vCPU-second`, `$0.000003/GiB-second`, and `$0.40/million`
requests after the subscription's monthly free grant of 180,000 vCPU-seconds, 360,000 GiB-seconds, and
two million requests. The target scales to zero, so no-request compute is `$0`.

| Active time at 0.5 vCPU/1 GiB | Gross compute | After full unused monthly free grant |
|---|---:|---:|
| 40 hours/month | $2.16 | $0.00 |
| 160 hours/month | $8.64 | $3.24 |
| 730 hours/month | $39.42 | $34.02 |

Add roughly `$0.30-$4.50/month` for Azure Files. Requests over the free grant add `$0.40/million`.
Because the target is East US 2 and PostgreSQL is Central US, inter-region data transfer is a variable
additional charge and a latency risk; measure it before production sizing. Log Analytics ingestion from
the shared environment is also usage-based.

Sources:

- https://azure.microsoft.com/pricing/details/container-apps/
- https://azure.microsoft.com/pricing/details/storage/files/

## Required permissions

Preview requires Azure CLI authentication, resource read access, and permission to run resource-group
what-if. Applying the foundation requires:

- resource deployment and storage account key-list permission in the named resource group;
- `Microsoft.Authorization/roleAssignments/write` for the ACR pull assignment;
- later, `Microsoft.Authorization/roleDefinitions/write` and role-assignment permission for the custom
  target deployer role;
- a named Microsoft Entra administrator on `pg-ofmfleet-dev-ykbpnrpd`, `psql` on the setup host, and
  network reachability to port 5432.

The live server currently lists the operator user (`dd84da40-177f-47b9-9c4d-4b657ee4de36`),
`id-ofmfleet-pgverify`, and `id-ofmfleet-web-dev-ykbpnrpd` as Entra administrators. The apply command must
use the registered principal name matching the caller token. The script verifies that name against the
live administrator list before deploying the foundation.

The setup script puts the short-lived PostgreSQL token only in `PGPASSWORD`, restores the prior process
value in `finally`, and never prints it. No credential is written to a generated artifact or Bicep file.

## Product-owned blockers before target deployment

1. `MigrationExecutor.DefaultAdapters` has no production application deployment adapter. The product
   must add a deterministic, approval-gated gateway that verifies the run, source SHA, generated artifact
   hashes, CI evidence, ACR repository, and exact digest immediately before the Azure write.
2. The generated .NET API currently creates `NpgsqlDataSource` from a passwordless connection string but
   does not acquire a PostgreSQL Microsoft Entra token. The runtime must use its managed identity to supply
   a renewable access token; build/test execution must remain network-isolated from Azure credentials.
3. CI builds only the workbench and demos. A generated-app workflow must build from retained artifacts,
   run the generated acceptance suite without Azure deployment credentials, publish to a dedicated ACR
   repository with immutable source metadata, and return the verified digest to the product.
4. Product schema migration remains the generated application's responsibility. This setup script creates
   only the database, principal, and grants; it deliberately executes no generated schema SQL.
5. The separate workbench infrastructure change that mounts Azure Files needs its own what-if and approval.

## Non-destructive shutdown

The target defaults to `minReplicas: 0`; with no traffic it incurs no ACA compute charge. To freeze future
product deployments, remove the exact-app custom role assignment or disable the deployment gateway after
capturing run evidence. Do not drop `ofm_dotnet_pilot` or delete the share as a shutdown shortcut.

Azure Files continues charging for retained bytes. Export and verify artifacts first, then obtain explicit
destructive approval before deleting the share or storage account. The dedicated database shares an
existing server and has no independent stop control; preserve it for evidence or revoke the runtime
principal's `CONNECT` grant when access must be disabled.
