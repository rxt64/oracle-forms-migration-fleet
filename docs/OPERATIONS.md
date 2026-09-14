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

## Full deploy

Use this when infrastructure, authentication, or configuration changed.

```powershell
.\infra\workbench\Deploy-Workbench.ps1 `
  -ResourceGroupName 'rg-oracle-forms-migration-fleet-dev-b9f0e875' `
  -ImageTag 'vN' `
  -FoundryAgentEndpoint '<canonical endpoint>'
```

The endpoint must be the canonical hosted-agent form or the script refuses to deploy:

```
https://<account>.services.ai.azure.com/api/projects/<project>/agents/<agent>/endpoint/protocols/openai/responses?api-version=v1
```

The script rotates the Entra client secret on every run. It **throws if two `container-app-auth-*`
credentials already exist**, which is intentional: you are meant to smoke-test the new revision, then
delete the superseded credential before rotating again.

```powershell
az ad app credential delete --id <appId> --key-id <oldKeyId>
```

## Image-only redeploy

Use this when **only application code changed**. It is faster, and it touches neither Microsoft Graph nor
the existing auth credential — which matters when Graph is unavailable (see Failure modes).

```powershell
az acr build --registry acrofmfleedevykbpnrpd `
  --image migration-fleet-workbench:vN `
  --file src\oracle-forms-migration-fleet\Dockerfile `
  src\oracle-forms-migration-fleet --no-logs -o json

az containerapp update --name ca-ofmfleet-dev-ykbpnrpd `
  --resource-group rg-oracle-forms-migration-fleet-dev-b9f0e875 `
  --image acrofmfleedevykbpnrpd.azurecr.io/migration-fleet-workbench:vN
```

## Verify a deploy

```powershell
az containerapp revision list -n ca-ofmfleet-dev-ykbpnrpd -g rg-oracle-forms-migration-fleet-dev-b9f0e875 `
  --query "[].{name:name,healthState:properties.healthState,runningState:properties.runningState,traffic:properties.trafficWeight,image:properties.template.containers[0].image}" -o json
```

Expect the newest revision `Healthy`, `RunningAtMaxScale`, `traffic: 100`, and the image tag you built.
Then load the workbench in a browser and exercise one real acquisition — a green revision only proves the
process started.

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

**`az acr build --no-logs` exits 0 even when the build failed.** This is the most dangerous one, because
it looks like success. Always assert the status explicitly:

```powershell
if ($buildRun.status -ne 'Succeeded') { throw "ACR build failed: $($buildRun.status)" }
```

**Streaming ACR logs crashes the Azure CLI on Windows.** The CLI writes build output through colorama to
a cp1252 console, so a single non-ASCII character — vite's `✓` is enough — aborts the client with
`UnicodeEncodeError: 'charmap' codec can't encode character '\u2713'`. The *server-side build keeps
going*; only the local streamer died. This is why the deploy script uses `--no-logs`. To read logs
safely, redirect to a file:

```powershell
az acr task logs --registry acrofmfleedevykbpnrpd --run-id <id> 2>&1 | Out-File "$env:TEMP\acr.log" -Encoding utf8
```

**Continuous access evaluation blocks Graph while ARM still works.** `az ad app ...` fails with
`InteractionRequired` / `TokenCreatedWithOutdatedPolicies` while `az acr build` and `az containerapp
update` succeed normally. Credential rotation needs Graph; an image-only redeploy does not. If you only
changed code, take the image-only path rather than re-authenticating.

**`minReplicas` is 0.** The first request after a new revision can take 60–90 seconds or return 504.
Warm it before concluding anything is broken.

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
