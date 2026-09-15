# Demo legacy Oracle estate

This directory deploys a disposable Oracle Database Free source environment for demonstrating the
Oracle Forms Migration Fleet workflow. Its banking schema independently implements the functional
requirements described by the public
[Trisha11r/oracle_apps_case_study](https://github.com/Trisha11r/oracle_apps_case_study) repository.

## Version scope

This demo database is **Oracle Database Free 23**, built from
`gvenzl/oracle-free:23-slim-faststart`. It proves the synthetic Northstar schema and PL/SQL path on
that image only; it is not a claim that every Oracle Database release or customer schema converts
without assessment and testing. See [../../docs/COMPATIBILITY.md](../../docs/COMPATIBILITY.md) for the
complete evidence boundary.

The deployment represents the **database tier only**. It does not include Oracle Forms Builder,
Forms Services, Oracle E-Business Suite, or a browser-accessible banking application. Those products
require separately licensed Oracle software, and no redistributable Forms runtime container is
available for this demo.

## Provenance and limits

The upstream repository contains 12 binary `.fmb` modules, a SQL script, a PL/SQL trigger/function
PDF, and supporting files. It does not declare a license. Consequently, no upstream file is copied or
redistributed here. The workbench can clone the public upstream URL directly when the source owner has
confirmed they have the right to analyze it.

The local database artifacts distinguish source-backed material from demo material:

| File | Classification | Purpose |
|---|---|---|
| `oracle/initdb/001_schema.sql` | Independently designed synthetic schema | Implements the published account opening, approval, login, transaction, statement, and interest-calculation requirements without copying or claiming compatibility with the upstream schema |
| `oracle/initdb/002_seed_data.sql` | Synthetic | Adds invented people, accounts, and transactions; none are production or customer data |
| `oracle/initdb/003_plsql.sql` | Independently implemented behavior | Provides a representative package, triggers, and statement view because the authoritative logic is embedded in binary Forms modules and a PDF |
| `oracle/initdb/004_verify.sql` | Local verification | Checks seed counts, invalid objects, and a known balance, then writes `SEED OK` or `SEED INCOMPLETE` to the container log |

This is not schema-compatible with, or a behavioral replica of, the Forms application. Missing Forms
XML exports, runtime configuration, reports, libraries, and executable tests remain real discovery
gaps for the migration assessment to report. Source inventory also found an apparent
`GROUP1_ACCOUNTREQUEST` versus `GRP1_ACCOUNTREQUEST` foreign-key naming mismatch in the upstream SQL;
that should be validated with the source owner rather than silently repaired here.

## Architecture

- Oracle Database Free 23 runs as one 2 vCPU / 4 GiB Azure Container Apps replica.
- ACR Tasks builds the seeded image server-side; local Docker is not required.
- The Oracle base image is imported from `gvenzl/oracle-free:23-slim-faststart` into the existing
  private Azure Container Registry before the seeded image is built.
- A user-assigned managed identity has registry-scoped `AcrPull` and no broader Azure role.
- Port 1521 uses internal-only TCP ingress in the existing Container Apps environment. It has no
  public endpoint.
- Database files use the replica's ephemeral storage. Replica replacement returns the estate to the
  seeded state; do not store anything valuable in it.
- Random Oracle passwords are passed as secure ARM parameters and stored as encrypted Container Apps
  secrets. They are never printed and the restricted temporary parameter files are deleted.

The subscription's governance policy forces new Key Vaults to disable public network access. The
existing Consumption-only Container Apps environment has no VNet or private endpoint, so it cannot
reach a private-only vault. Container Apps secrets are the deliberately scoped choice for these two
disposable demo credentials. A durable estate should instead use a VNet-integrated environment,
private endpoint, Key Vault, persistent storage, backup, and recovery controls.

## Prerequisites

- PowerShell 7.2 or later
- Azure CLI with the Bicep component
- An authenticated Azure session with rights to deploy resources and assign `AcrPull`
- The existing resource group, Container Apps environment, and private container registry named in
  `dev.bicepparam`

Select the intended subscription before deploying:

```powershell
az account set --subscription d4394e57-c076-4c92-a870-5de6bf44f255
```

## Deploy

From the repository root:

```powershell
pwsh -File .\infra\legacy-estate\Deploy-LegacyEstate.ps1 `
  -ResourceGroupName rg-oracle-forms-migration-fleet-dev-b9f0e875
```

The script imports the base image when absent, builds the seeded image in ACR, compiles the Bicep
parameter file, generates both passwords, and deploys the estate. Initial Oracle startup can take
several minutes. The default timestamp image tag is immutable in practice; if `-ImageTag` is supplied,
it must not already exist in ACR. Reusing a tag is rejected so every password rotation creates a new
revision and a freshly initialized database.

The current development deployment resolves internally as:

```text
ca-ofmfleet-db-dev-ykbpnrpd.internal.jollyground-7a57bcec.eastus2.azurecontainerapps.io:1521
```

Only workloads with network access inside that Container Apps environment can connect to this host.
For administrative checks, use `az containerapp exec` instead of exposing the listener.

## Verify

Check that the active revision is healthy:

```powershell
az containerapp revision list `
  --resource-group rg-oracle-forms-migration-fleet-dev-b9f0e875 `
  --name ca-ofmfleet-db-dev-ykbpnrpd `
  --query "[].{name:name,active:properties.active,health:properties.healthState,running:properties.runningState,replicas:properties.replicas}" `
  --output table
```

Read a bounded log tail and look for `SEED OK`:

```powershell
az containerapp logs show `
  --resource-group rg-oracle-forms-migration-fleet-dev-b9f0e875 `
  --name ca-ofmfleet-db-dev-ykbpnrpd `
  --type console `
  --tail 300
```

A successful first boot reports 8 account requests, 5 registered accounts, 2 staff users,
12 transactions, 0 invalid objects, a balance of 3300 for account 500001, and `SEED OK`. Any `ORA-`,
`PLS-`, `SP2-`, or `SEED INCOMPLETE` line is a failed verification. SQL*Plus exits nonzero on script
or verification errors, preventing a partial initialization from being treated as successful.

Open SQL*Plus inside the running container without retrieving the password to the local shell:

```powershell
az containerapp exec `
  --resource-group rg-oracle-forms-migration-fleet-dev-b9f0e875 `
  --name ca-ofmfleet-db-dev-ykbpnrpd `
  --command 'sqlplus BANKING/$APP_USER_PASSWORD@localhost/FREEPDB1'
```

Callers with appropriate Container App RBAC can retrieve a generated password when necessary:

```powershell
az containerapp secret show `
  --resource-group rg-oracle-forms-migration-fleet-dev-b9f0e875 `
  --name ca-ofmfleet-db-dev-ykbpnrpd `
  --secret-name oracle-sys-password `
  --query value `
  --output tsv
```

Treat that output as a credential. Do not paste it into source control, logs, issue comments, or agent
prompts.

## Cost control

The normal demo configuration keeps one 2 vCPU / 4 GiB replica running. Scale it to zero whenever the
demo is idle:

```powershell
az containerapp update `
  --resource-group rg-oracle-forms-migration-fleet-dev-b9f0e875 `
  --name ca-ofmfleet-db-dev-ykbpnrpd `
  --min-replicas 0 `
  --max-replicas 1
```

Restore the always-on demo replica before a session:

```powershell
az containerapp update `
  --resource-group rg-oracle-forms-migration-fleet-dev-b9f0e875 `
  --name ca-ofmfleet-db-dev-ykbpnrpd `
  --min-replicas 1 `
  --max-replicas 1
```

A new replica starts from the seed state.

## Tear down

The estate deliberately reuses the shared Container Apps environment and ACR, so do not delete those
shared resources. Remove only the database app, its scoped role assignment and identity, its images,
and the deployment record:

```powershell
$resourceGroup = 'rg-oracle-forms-migration-fleet-dev-b9f0e875'
$identityName = 'id-ofmfleet-db-dev-ykbpnrpd'
$containerAppName = 'ca-ofmfleet-db-dev-ykbpnrpd'
$registryName = 'acrofmfleedevykbpnrpd'
$principalId = az identity show -g $resourceGroup -n $identityName --query principalId -o tsv
$registryId = az acr show -g $resourceGroup -n $registryName --query id -o tsv

az containerapp delete -g $resourceGroup -n $containerAppName --yes
az role assignment delete --assignee-object-id $principalId --scope $registryId --role AcrPull
az identity delete -g $resourceGroup -n $identityName
az acr repository delete -n $registryName --repository oracle-forms-legacy-db --yes
az deployment group delete -g $resourceGroup -n oracle-forms-legacy-estate
```

The imported `oracle-free` base repository is shared by future estate builds and is intentionally left
in ACR. Delete it separately only when no other build depends on it.
