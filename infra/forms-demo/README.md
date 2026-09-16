# Oracle Forms workflow replica

This directory deploys a **browser-executable workflow replica** of the banking case study. It is a
locally implemented ASP.NET application for demonstrating customer and manager workflows against the
disposable Oracle v2 estate. It does **not** deploy the upstream `.fmb` modules, Oracle Forms runtime,
Forms Services, or Oracle WebLogic Server.

## Why the Oracle Forms source is not deployed

The upstream material contains binary `.fmb` source modules, but no compiled `.fmx` executables,
licensed Forms runtime, WebLogic domain, Forms Services configuration, deployment package, or complete
runtime configuration. An `.fmb` file is not directly browser-executable. Building and hosting it
requires proprietary Oracle tooling and appropriately licensed runtime components that are neither
present nor redistributable through this repository.

The replica is therefore an independently implemented demonstration surface, not a conversion or
execution of those modules.

## Version scope

The synthetic Forms XML used by the fleet declares `FormsVersion="12.2.1.4"` and follows the
Forms2XML shape. It is hand-authored test evidence, not an export from a licensed Oracle Forms
12.2.1.4 runtime. The backing disposable database is Oracle Database Free 23. The demonstrated target
is Azure Database for PostgreSQL 16. See [../../docs/COMPATIBILITY.md](../../docs/COMPATIBILITY.md).

Forms 6i, 10g, 11g, other 12c patch levels, and 14c/14.1.2 are not demonstrated compatibility claims.
They require their own authorized representative artifacts and differential acceptance tests.

## Architecture and credential boundary

- ACR Tasks builds `src/oracle-forms-demo/Dockerfile` server-side; local Docker is not used.
- The public HTTPS Container App runs one 0.5 vCPU / 1 GiB replica in the existing shared environment.
- A user-assigned identity has `AcrPull` only at the existing registry scope. ACR admin credentials are
  not enabled or used.
- The server connects to the existing internal-only Oracle database app over TCP 1521 using the
  same-environment Container App service name, Oracle service `FREEPDB1`, and user `BANKING`. In this
  Consumption environment, the app name resolves directly to the internal service; the longer
  ingress FQDN resolves to an environment VIP that does not route this app-to-app TCP connection.
- The deployment script reads the database app's `banking-schema-password` secret into memory, creates
  the ODP.NET connection string in memory, and submits it through a current-user-only temporary ARM
  parameter file that is deleted in `finally`.
- The connection string is stored as the encrypted native Container Apps secret
  `oracle-connection-string`. This is the deliberate disposable-demo exception because tenant policy
  makes Key Vault private-only while the shared Consumption environment has no VNet path to a private
  endpoint.
- The browser receives no Oracle host, password, or connection string. Database calls are server-side.

## Prerequisites

- PowerShell 7.2 or later
- Azure CLI with the Bicep component
- Rights to build in the existing ACR, read the database Container App secret, deploy resource-group
  resources, and assign registry-scoped `AcrPull`
- An Azure subscription containing the shared migration-fleet development resources
- Resource group `rg-oracle-forms-migration-fleet-dev-b9f0e875` in `eastus2`

Select the intended subscription before deployment:

```powershell
az account set --subscription '<subscription-id-or-name>'
```

## Deploy

From the repository root:

```powershell
pwsh -File .\infra\forms-demo\Deploy-FormsDemo.ps1 `
  -ResourceGroupName rg-oracle-forms-migration-fleet-dev-b9f0e875
```

The default image tag is a timestamp. A supplied tag must be a valid OCI/Docker tag and must not
already exist in the `oracle-forms-demo` repository; the script rejects tag reuse before building.
It validates the configured ACR, Container Apps environment, and database app before reading the
credential or deploying. The database must remain internal-only TCP ingress on port 1521 in the same
Container Apps environment.

## Verify

The script prints the public HTTPS URL after deployment. Use it as `$url` below:

```powershell
$url = 'https://ca-ofmfleet-forms-dev-ykbpnrpd.<environment-domain>'

Invoke-RestMethod "$url/healthz"
Invoke-RestMethod "$url/api/health"
```

`GET /healthz` checks the web process without accessing Oracle. `GET /api/health` performs the database
check and should report a healthy result after the Oracle estate is ready. Neither response should
contain the database host or credential.

Open the URL in a browser and use only the synthetic demo identities:

| Workflow | User | Password |
|---|---|---|
| Customer | `500001` | `demo1234` |
| Manager | `branch.manager` | `manager-demo-1` |

These credentials are application demo data, not Azure or Oracle administration credentials.

## Cost control

The replica defaults to one running 0.5 vCPU / 1 GiB instance for predictable demonstrations. The
Oracle estate separately defaults to one 2 vCPU / 4 GiB instance and is the larger idle cost. Scale
both apps to zero when the complete demo is idle:

```powershell
$resourceGroup = 'rg-oracle-forms-migration-fleet-dev-b9f0e875'
az containerapp update -g $resourceGroup -n ca-ofmfleet-forms-dev-ykbpnrpd --min-replicas 0 --max-replicas 1
az containerapp update -g $resourceGroup -n ca-ofmfleet-db-dev-ykbpnrpd --min-replicas 0 --max-replicas 1
```

Restore the database first, allow Oracle to finish seeding, and then restore the replica:

```powershell
az containerapp update -g $resourceGroup -n ca-ofmfleet-db-dev-ykbpnrpd --min-replicas 1 --max-replicas 1
az containerapp update -g $resourceGroup -n ca-ofmfleet-forms-dev-ykbpnrpd --min-replicas 1 --max-replicas 1
```

The database uses ephemeral storage, so a new database replica returns to the synthetic seed state.

## Targeted teardown

Delete only the workflow replica resources. Preserve the shared Container Apps environment, shared
ACR, and internal Oracle database app:

```powershell
$resourceGroup = 'rg-oracle-forms-migration-fleet-dev-b9f0e875'
$appName = 'ca-ofmfleet-forms-dev-ykbpnrpd'
$identityName = 'id-ofmfleet-forms-dev-ykbpnrpd'
$registryName = 'acrofmfleedevykbpnrpd'
$principalId = az identity show -g $resourceGroup -n $identityName --query principalId -o tsv
$registryId = az acr show -g $resourceGroup -n $registryName --query id -o tsv

az containerapp delete -g $resourceGroup -n $appName --yes
az role assignment delete --assignee-object-id $principalId --scope $registryId --role AcrPull
az identity delete -g $resourceGroup -n $identityName
az acr repository delete -n $registryName --repository oracle-forms-demo --yes
az deployment group delete -g $resourceGroup -n oracle-forms-demo
```

Do not delete `cae-ofmfleet-dev-ykbpnrpd`, `acrofmfleedevykbpnrpd`, or
`ca-ofmfleet-db-dev-ykbpnrpd` as part of this teardown.

## Provenance and no-copy boundary

The upstream case-study repository does not declare a license. No upstream `.fmb`, SQL, PDF, report,
library, executable, or configuration file is copied into this deployment. The browser replica and
the synthetic database estate are independently implemented from public functional descriptions and
do not claim source compatibility, binary conversion, or Oracle Forms runtime equivalence. Any use of
the upstream source for analysis remains subject to the source owner's authorization and licensing.