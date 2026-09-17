# ADR 001: Azure-Primary Migration Platform and Source Lab

## Status

Accepted for planning and incremental implementation. This ADR does not authorize new production resources or Oracle license acceptance.

## Decision

Azure is the durable home for the workbench, run state, evidence, primary synthetic Oracle source lab, and migration targets. OCI is an optional, temporary secondary source lab for cross-cloud connector qualification. The baseline product must work without OCI.

## Verified Environment Evidence

Read-only inventory on 2026-09-17 found one enabled Azure subscription available to the current identity. Its tenant-specific name and identifier are intentionally not committed.

The dedicated resource group `rg-oracle-forms-migration-fleet-dev-b9f0e875` contains 16 resources:

- Foundry account `cog-czusrhcg4gpm2` and project `oracle-forms-migration-fleet-dev`.
- Log Analytics workspace and Application Insights instance.
- Azure Container Registry `acrofmfleedevykbpnrpd`.
- Container Apps environment `cae-ofmfleet-dev-ykbpnrpd`.
- Workbench, synthetic Oracle database, Forms workflow replica, and migrated Northstar Container Apps.
- PostgreSQL Flexible Server `pg-ofmfleet-dev-ykbpnrpd` in `centralus`.
- Four user-assigned identities and one Container Instance verification worker.

Resource presence does not prove spare quota, approved budget, network suitability for a certified Oracle VM, or permission to modify shared resources. Those remain deployment-time preflights.

## Consequences

- New source-lab, platform, worker, and target resources require distinct names, tags, and ownership boundaries.
- Existing resources are treated as shared until their ownership is proven; no resource-group-wide deletion is permitted.
- Azure costs must be estimated before provisioning. Budget alerts are not spending caps.
- OCI resources must be disposable, exported before trial cutoff, and must never contain company data or credentials without applicable approval.
- Oracle installers and Marketplace images require independent entitlement validation; cloud credits do not grant middleware rights.
- A concrete Azure policy or certification blocker may justify temporary OCI source hosting, but not moving durable control-plane state or migration targets to OCI.

## Alternatives Rejected

- **OCI-primary platform**: rejected because the trial is temporary and the durable Azure platform already exists.
- **Manual migration outside the workbench**: rejected because it produces artifacts without adding reusable product capability.
- **Assume existing Azure resources are free or dedicated**: rejected because neither cost allocation nor exclusive ownership was established by inventory.
