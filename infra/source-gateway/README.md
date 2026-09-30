# Private Source Gateway Infrastructure

These artifacts implement the generation/validation-only scope recorded in
`.azure/deployment-plan.md`. They do not apply Azure resources, mutate the Forms VM, create Entra
objects, create an Oracle account, extract a source, or deploy a migration.

## Artifacts

- `main.bicep`: stage 1, adding only the `.64/27` delegated subnet and internal workload-profiles ACA environment.
- `application.bicep`: stage 2, adding the private app, private DNS, and one exact NSG rule.
- `Preview-SourceGateway.ps1`: pinned-subscription compilation and non-applying group what-if wrapper.
- `Install-SourceGateway.ps1`: trusted main-push x86 artifact verification plus LocalService/TLS/service bootstrap.
- `New-*`, `Protect-*`, `Complete-*`: one-time public-key transfer and LocalService DPAPI CurrentUser provisioning.
- `source-registry.json`: nonsecret single-source binding for `meridian-native-6i` and project `prj-d616e6e807e14b6bb5a468a33da3d744`.
- `Dockerfile.private-workbench`: public-root trust overlay for a release-approved immutable workbench image.

## Local Validation

Compilation and parsing are non-mutating:

```powershell
az bicep build --file infra/source-gateway/main.bicep
az bicep build --file infra/source-gateway/application.bicep
pwsh -NoLogo -NoProfile -File infra/source-gateway/Test-SourceGatewayScripts.ps1
checkov -d infra/source-gateway --framework bicep --quiet

$errors = @()
Get-ChildItem infra/source-gateway/*.ps1 | ForEach-Object {
    $tokens = $null
    $parseErrors = $null
    [void][System.Management.Automation.Language.Parser]::ParseFile($_.FullName, [ref]$tokens, [ref]$parseErrors)
    $errors += $parseErrors
}
if ($errors.Count) { throw ($errors.Message -join '; ') }
```

CI runs the same Bicep/parser/behavior checks and Checkov 3.3.19 gate. The PowerShell regression uses
mocked native-command exits; it does not install a service, create a certificate, or handle a credential.

Stage 1 what-if is read-only and never changes the Azure CLI default subscription:

```powershell
pwsh infra/source-gateway/Preview-SourceGateway.ps1 -Stage Foundation
```

Stage 2 what-if is intentionally impossible until stage 1 has been separately approved and applied,
because the returned `defaultDomain` and inbound `staticIp` are deployment outputs. Set the existing
workbench auth secret only in the trusted release process; the wrapper places it in a restricted temporary
parameter file and deletes it in `finally`, so it is never a command argument or transcript line.

```powershell
$env:OFM_WORKBENCH_AUTH_CLIENT_SECRET = '<trusted-release-secret>'
pwsh infra/source-gateway/Preview-SourceGateway.ps1 -Stage Application `
    -ManagedEnvironmentDefaultDomain '<stage-1-defaultDomain>' `
    -ManagedEnvironmentInboundStaticIp '<stage-1-staticIp>' `
    -ContainerImage 'acrofmfleedevykbpnrpd.azurecr.io/migration-fleet-workbench-source-gateway@sha256:<digest>' `
    -SourceGatewayApplicationClientId '<gateway-app-client-id>' `
    -FoundryAgentEndpoint '<existing-hosted-agent-endpoint>' `
    -OperatorPrincipalObjectIds '<operator-object-id>'
Remove-Item Env:OFM_WORKBENCH_AUTH_CLIENT_SECRET
```

## Required External Outputs

The following are not invented or auto-selected:

1. Entra administrator output: gateway API application client ID, `SourceGateway.Invoke` application
   role, and assignment only to UAMI principal `595d3a27-a2a8-4ac4-b33d-98715cbcd684`.
2. Existing workbench auth registration update: add only the stage-2 output
   `requiredWorkbenchRedirectUri`; retain the public app's existing platform and authentication state.
3. Successful main-push CI: full main SHA and run ID whose `source-worker-win-x86-<sha>` manifest passes
   repository, workflow, event, ref, commit, run, runtime, length, and SHA-256 checks.
4. Host bootstrap output: the public root CER and SHA-256. Only those public bytes may be committed under
   `trust/`; the private key remains non-exportable on the Forms VM.
5. Trusted overlay workflow output: immutable
   `migration-fleet-workbench-source-gateway@sha256:<digest>` built from the release-approved base digest.
6. Oracle DBA output: a read-only `OFM_GATEWAY_ORACLE_MERIDIAN_RO` connect value transferred through the one-time public
   key flow. It is never supplied in chat, Git, ARM, a command argument, or a log.
7. Stable ACA outbound decision: the ACA environment `staticIp` is ingress and must not be placed in the
   PostgreSQL firewall. Current Microsoft documentation says outbound IPs may change and documents NAT
   Gateway for deterministic workload-profile egress. No PostgreSQL firewall rule is generated until an
   exact supported `/32` and its cost are separately approved.

## Later Mutation Commands

The commands below are examples of what a separately approved execution would invoke. They are not
authorized by generation/validation approval and were not run.

```powershell
az deployment group create --subscription d4394e57-c076-4c92-a870-5de6bf44f255 `
    --resource-group rg-oracle-forms-migration-fleet-dev-b9f0e875 `
    --name ofm-source-gateway-foundation `
    --template-file infra/source-gateway/main.bicep
```

Application deployment must use a trusted release mechanism with a secure parameter channel; do not put
`workbenchAuthClientSecret` on a command line. VM bootstrap requires separate VM-mutation approval and then:

```powershell
.\Install-SourceGateway.ps1 `
    -ExpectedMainCommitSha '<full-main-sha>' `
    -TrustedCiRunId '<successful-ci-run-id>' `
    -GatewayApplicationClientId '<gateway-app-client-id>'
```

The installer fixes the service name to `OFMSourceGateway`, runs it as LocalService with a restricted
service SID, pins the stable content root, grants both LocalService and the restricted SID read-only Forms
input/home access, separate credential/output/temp modify access, and leaf private-key read access. It
reuses its persisted certificate binding on rerun and refuses unmanaged trust instead of rotating it. It
creates no Oracle credential.

## Cost And Security Delta

The generated ACA/DNS scope retains the plan estimate of about `$19.25/month` at scale zero and about
`$58.67/month` at continuous `0.5 vCPU/1 GiB` activity, plus logs/data processing, below the delegated
`$70/month` ceiling. A NAT Gateway/static public IP is not included: its fixed cost can push continuous
usage over the ceiling and requires a new cost/security decision.

There is no public gateway listener, no TLS bypass, no hardcoded secret, no external role assignment, no
PostgreSQL firewall guess, and no use or change of corporate default subscription
`0832b3b6-22b3-4c47-8d8b-572054b97257`.

Official networking references:

- https://learn.microsoft.com/azure/container-apps/networking
- https://learn.microsoft.com/azure/container-apps/custom-virtual-networks#azure-nat-gateway-integration