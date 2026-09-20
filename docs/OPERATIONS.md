# Operations runbook

Everything here was learned by doing it. The traps in "Failure modes" are all real and all cost time.

Identifiers that map the Azure estate (subscription, tenant, Entra app IDs) are deliberately **not**
written down here — the lookup commands are given instead, so this file stays safe if the repository is
ever made public.

## Environment

| Thing | Dev value |
|---|---|
| Resource group | `rg-oracle-forms-migration-fleet-dev-b9f0e875` |
| Container App | `ca-ofmfleet-dev-ykbpnrpd` |
| Container registry | `acrofmfleedevykbpnrpd`, repository `migration-fleet-workbench` |
| Entra app | `Migration Fleet Workbench - dev` |
| Foundry | account `cog-czusrhcg4gpm2`, project `oracle-forms-migration-fleet-dev`, agent `oracle-forms-migration-fleet` |

Look up the pieces you need:

```powershell
az group show -n rg-oracle-forms-migration-fleet-dev-b9f0e875 --query id -o tsv
az ad app list --display-name 'Migration Fleet Workbench - dev' --query "[0].appId" -o tsv
```

## Deploy an application revision

Deployable images are built by the GitHub runner and tagged from the commit. Dispatch the same workflow
that runs automatically after a reviewed change reaches `main`:

```powershell
$sha = (git rev-parse origin/main).Trim()
gh workflow run deploy.yml --repo rxt64/oracle-forms-migration-fleet --ref main -f commit_sha=$sha
```

Manual deployment accepts only a full commit SHA reachable from `main` with a completed successful
`CI` push run for that exact SHA. Routine releases run automatically as a dependent CI job after the
required build/test, container, and guided-browser jobs pass.

The runner authenticates with OIDC, builds and pushes the commit-addressed image, configures the
host-owned PostgreSQL sandbox and model deployments, and updates the Container App. Do not run
`az acr build` or `docker build` from a workstation for a deployable image.

One deployment has one shared mutable sandbox. Its first `SandboxDatabaseWrite` request permanently
binds that tenant's sandbox to the requesting project. Other projects remain available for planning and
`ValidationOnly`, but require a separate deployment/database/identity boundary before they can request
sandbox writes.

This binding is an intentional reservation, not an approval side effect. Once a validated request claims
the sandbox, a later approval-storage failure does not release it; retry the request in the same project.

Migration runs are persisted before execution. Closing a tab only disconnects its event follower; run
history replays ordered events and terminal outcomes. Workers renew server-timed leases independently of
progress, and every event, completion, and external gateway call is fenced. Queued or leased-before-start
work may be reclaimed only by the replica that owns its local source bytes. Started work is never taken
over. A same-replica process restart records expired started work as `Interrupted`; a replacement replica
cannot claim local bytes it does not own, so retained history remains the reconciliation source.
Artifact manifests and hashes remain visible after workspace bytes expire.

Platform schema v3 adds durable run, event, and artifact tables. The startup guard rejects older binaries
after v3 is applied, so deploy migration-capable revisions forward; do not roll back to a v1/v2 binary.

The Foundry endpoint configured by infrastructure must use the canonical hosted-agent form:

```
https://<account>.services.ai.azure.com/api/projects/<project>/agents/<agent>/endpoint/protocols/openai/responses?api-version=v1
```

`Deploy-Workbench.ps1` is a privileged first-time infrastructure and Entra bootstrap tool, not the
application release path. It configures the dedicated `api://<application-client-id>` resource for v2
access tokens, rotates the Entra client secret, and **throws if two `container-app-auth-*`
credentials already exist**. Smoke-test a bootstrap revision, then delete the superseded credential.
It accepts only a 12-character commit tag that already exists in ACR and never builds an image. The
foundation/build-only/auth sequence is documented in [infra/workbench/README.md](../infra/workbench/README.md).

```powershell
az ad app credential delete --id <appId> --key-id <oldKeyId>
```

## Verify a deploy

```powershell
az containerapp revision list -n ca-ofmfleet-dev-ykbpnrpd -g rg-oracle-forms-migration-fleet-dev-b9f0e875 `
  --query "[].{name:name,healthState:properties.healthState,runningState:properties.runningState,traffic:properties.trafficWeight,image:properties.template.containers[0].image}" -o json
```

Expect the newest revision `Healthy`, `RunningAtMaxScale`, `traffic: 100`, and an ACR `sha256` image
digest. The workflow then performs authenticated bootstrap, project, platform-state, unauthorized-access,
and non-writing approval persistence checks through a short-lived managed-identity runner.

For a sandbox migration containing translated program units, inspect these exported artifacts:

- `database/postgresql/schema/program-unit-repairs.sql` contains only revisions PostgreSQL compiled;
- `database/postgresql/schema/program-unit-repair-audit.md` records attempted SQL, compiler acceptance,
  and outstanding diagnostics;
- `data/migration-report.md` records row-loading outcomes independently from routine compilation.

The sandbox phase intentionally reports failure when rows loaded but program units remain rejected. This
preserves the useful data-load result without issuing `SandboxMigrationCompleted` for an incomplete target.
On a rerun, accepted repair SQL is preserved, checked against the newly generated routine envelopes, and
recompiled. A model call occurs only for routines that remain unresolved after that revalidation.

Generated application validation requires Java 21, Maven, Node, and npm. The checked-in runtime Dockerfile
installs those tools. A generation run writes `reports/build-and-static-analysis.json` with the fixed
command, exit code, and bounded output for both Java/Spring Boot and React/TypeScript. A missing tool is a
phase failure, not a skipped or successful build.

## Rollback

Revisions are immutable, so rolling back is pointing traffic at the previous one:

```powershell
az containerapp update -n ca-ofmfleet-dev-ykbpnrpd -g rg-oracle-forms-migration-fleet-dev-b9f0e875 `
  --image acrofmfleedevykbpnrpd.azurecr.io/migration-fleet-workbench:<previousTag>
```

## Failure modes

**Continuous access evaluation blocks Graph while ARM still works.** `az ad app ...` fails with
`InteractionRequired` / `TokenCreatedWithOutdatedPolicies` while ARM calls still work. Credential
rotation in the privileged bootstrap needs Graph; a normal GitHub runner release does not rotate that
credential. If only application code changed, use the runner workflow rather than re-running bootstrap.

**The workbench stays warm at one replica.** A 504 is not an expected cold start. Inspect revision health,
startup logs, Easy Auth, and traffic weight before retrying.

**A request during a revision switch can 404.** Routes that exist will briefly appear missing while
traffic moves. Re-probe before debugging the route.

**Browsers serve a stale bundle.** Vite emits fixed names (`app.js`, `styles.css`), so
`WorkbenchEndpoints.ServeAsset` sends `Cache-Control: no-cache` plus `Last-Modified`. When verifying a
fresh deploy through browser automation, disable the HTTP cache (CDP `Network.setCacheDisabled`) or you
will test the previous build and believe your change did not ship.

**The repair model can return HTTP 429.** The compiler-driven loop retries a transient rate-limit response
once within its two-attempt budget. If capacity remains unavailable, the phase keeps the original compiler
diagnostics, writes the repair audit, and fails without an attestation. Increase review-model capacity or
rerun later; do not treat a missing model revision as successful migration.

**PowerShell:** `$env:ProgramFiles(x86)` is a parse error; use `${env:ProgramFiles(x86)}`.
