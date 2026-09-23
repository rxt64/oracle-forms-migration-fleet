# .NET Pilot Azure Foundation Validation

Validation date: September 23, 2026

## Scope

This operation completed only the generated .NET target foundation in subscription
`d4394e57-c076-4c92-a870-5de6bf44f255`, resource group
`rg-oracle-forms-migration-fleet-dev-b9f0e875`. It did not apply workbench Bicep, deploy a generated
application, execute generated SQL, or read or change source business data.

## Live Foundation

- PostgreSQL Flexible Server: `pg-ofmfleet-dev-ykbpnrpd`, PostgreSQL 16, Central US, Entra-only.
- Dedicated database: `ofm_dotnet_pilot`.
- Database owner: `id-ofmfleet-pgverify`, the named provisioning administrator.
- Runtime principal: `id-ofmfleet-dotnet-dev-ykbpnrpd`.
  - Azure client ID: `b081fc02-c473-4fc9-ab7f-24e69b1a97a0`.
  - Azure principal ID: `eaea7400-98c1-4e84-994e-e1d5e51a4b5f`.
  - PostgreSQL grants: target database `CONNECT` and `TEMPORARY`; target `public` schema `USAGE` and
    `CREATE`; no database-level `CREATE`, ownership, or `ofm_platform` access.
- Migration principal: `id-ofmfleet-web-dev-ykbpnrpd`, separately parameterized for product-owned
  migrations.
  - Azure client ID: `f4492b02-ee64-4295-a1db-7c7677134e42`.
  - PostgreSQL grants: target database `CONNECT` and `TEMPORARY`; target `public` schema `USAGE` and
    `CREATE`; explicit `CONNECT` retained on `ofm_platform`.
- Protected platform database: `ofm_platform` now revokes default `PUBLIC CONNECT`; the owner,
  provisioning administrator, and named workbench migration principal retain required access.
- Runtime Azure RBAC: only `AcrPull` on `acrofmfleedevykbpnrpd`.
- Durable storage: `stofmdotdevykbpnrpd`, Standard LRS, East US 2, HTTPS-only, public blob access off,
  shared-key access off. ACA environment storage `dotnet-pilot-artifacts` points read/write to
  `workbench-artifacts`.
- Target Container App `ca-ofmfleet-dotnet-dev-ykbpnrpd`: absent, as required by `deployTarget=false`.

## Verification Evidence

The final one-shot verifier deployment `ofm-dotnet-foundation-probe-final` succeeded with correlation ID
`b3fabc2b-c5fc-4106-b515-52ec3eba926b`. Its what-if contained exactly one non-Ignore change: create
`aci-ofmfleet-dotnet-foundation`.

The verifier used `postgres:16-alpine` and existing identities `id-ofmfleet-pgverify`,
`id-ofmfleet-dotnet-dev-ykbpnrpd`, and `id-ofmfleet-web-dev-ykbpnrpd`. It terminated with exit code `0`
at `2026-09-23T20:16:12.621Z`. Logs contained:

```text
ofm_dotnet_pilot|id-ofmfleet-dotnet-dev-ykbpnrpd|t|f|t|t
ISOLATION_OK|id-ofmfleet-dotnet-dev-ykbpnrpd|ofm_platform|CONNECT_DENIED
ofm_dotnet_pilot|id-ofmfleet-web-dev-ykbpnrpd|t|t|t
```

The first row is an actual runtime managed-identity query showing database, user, target `CONNECT=true`,
database-level `CREATE=false`, and target schema `USAGE/CREATE=true`. The second row is an actual failed
runtime connection to `ofm_platform`. The third row is an actual workbench managed-identity query showing
database, user, target `CONNECT=true`, and target schema `USAGE/CREATE=true`.

No access token was printed or placed on a command line. `Setup-DotNetPilotDatabase.sh` obtains tokens
inside ACI through IMDS and places them only in `PGPASSWORD` for the child `psql` process.

## Network And Cleanup

The final verifier reported egress `57.162.99.8`. A temporary firewall rule
`dotnet-foundation-probe` allowed exactly `57.162.99.8/32` and was removed after verification. Final
firewall inventory contains only the pre-existing exact single-IP rules; no `0.0.0.0` rule exists.

`aci-ofmfleet-dotnet-foundation` is retained as terminated evidence with restart policy `Never` and exit
code `0`. `aci-ofmfleet-dotnet-probe` is also terminated. Neither incurs running ACI compute cost.

## Validation And Remaining Blocker

- `Setup-DotNetPilotDatabase.sh`: Git Bash syntax passed.
- `Setup-DotNetPilot.ps1`: PowerShell parser passed.
- `main.bicep` and `database-probe.bicep`: Bicep compilation passed.
- ARM validation for the verifier passed.
- Final verifier what-if: one ACI create, no other change.

The final `main.bicep` foundation what-if is intentionally not applied. It would modify
`stofmdotdevykbpnrpd` from `allowSharedKeyAccess=false` to `true` and from
`publicNetworkAccess=Disabled` to `Enabled` to support an ACA SMB mount. That is a security broadening and
conflicts with live policy posture. The database foundation is complete; the future workbench artifact
mount remains blocked until the parent workbench owner validates a private-network-compatible Azure Files
design or explicitly approves the exact storage exposure. No workbench revision was deployed here.