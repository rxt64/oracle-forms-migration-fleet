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
$env:WORKBENCH_OPERATOR_PRINCIPAL_OBJECT_IDS = '<entra-object-id>[,<entra-object-id>]'

.\infra\workbench\Preview-WorkbenchInfrastructure.ps1 `
  -ResourceGroupName 'rg-oracle-forms-migration-fleet-dev-b9f0e875'
```

The allowlist accepts comma-separated Microsoft Entra object IDs and is read at Bicep parameter-build
time; it is not stored in source control. The preview compiles Bicep, runs resource-group what-if, and
prints only sanitized resource type/name changes.

## Deploy application revisions

```powershell
$sha = (git rev-parse origin/main).Trim()
gh workflow run deploy.yml --repo rxt64/oracle-forms-migration-fleet --ref main -f commit_sha=$sha
```

The selected SHA must be reachable from `main` and have a completed successful push CI run containing
all required jobs. The GitHub runner builds a commit-addressed image, resolves its ACR digest,
authenticates to Azure with OIDC, configures the host-owned sandbox target and model deployments, and
updates the Container App. Pushes to `main` invoke the same workflow only after all required CI jobs pass.
Deployable images are never built from a workstation.

The existing PostgreSQL server must contain two distinct databases before an application revision is deployed:

- `ofm_platform` stores organizations, projects, memberships, target profiles, and approvals in its `ofm_platform` schema.
- `postgres` is the sandbox migration target where generated DDL and copied data may be applied.

The deployment script refuses to use the same host/database pair for both purposes. PostgreSQL does not permit ordinary SQL statements to cross database boundaries, so sandbox DDL cannot address the authorization tables. The managed identity must be provisioned in both databases with only the permissions each role requires. This repository does not create either database or grant those database permissions.

This stack exposes one shared sandbox database, so it deliberately supports sandbox mutation for one
project per tenant. The first project that requests `SandboxDatabaseWrite` is persisted as the owner;
another project receives a conflict and cannot reassign that boundary. Other projects may still plan and
use `ValidationOnly`. Run a separate workbench deployment with a separate database or identity when
multiple projects need independently mutable sandboxes.

The ownership reservation is written after the request passes membership, target, source, and note
validation but before the approval record. It is not rolled back if approval persistence later fails:
the project-to-sandbox assignment is a durable deployment boundary, while an approval is a separate
human decision that may be requested again within that owner project.

The Container App remains fixed at one replica. Browser disconnects resume through PostgreSQL event
replay. When `workbenchArtifactStorageName` names an existing Container Apps environment storage link,
the app mounts its classic Azure Files share at `/mnt/workbench-sources`; source snapshots and generated
artifacts then survive revision replacement. Started work is never executed twice, and the single replica
remains the execution concurrency boundary. Without that parameter the explicit `/tmp/workbench-sources`
root preserves the earlier replica-local behavior.

The development parameter file expects environment storage `dotnet-pilot-artifacts`, prepared by
`infra/dotnet-pilot`. Apply and approve that additive foundation before applying this workbench template.
The Azure Files account key is held by the managed-environment storage resource because Container Apps
SMB mounts do not support identity-based share access; neither the workbench nor a generated app receives it.

`Deploy-Workbench.ps1` is retained only for privileged first-time infrastructure and Entra bootstrap in an empty environment. It is not the application release path; routine releases must use the GitHub workflow. Bootstrap requires permission to create resource-group deployments and role assignments and to create or update an Entra application. It exposes that dedicated application as `api://<application-client-id>` and requests v2 access tokens so managed identities receive tokens from the same issuer the workbench validates. It never logs in on the user's behalf.

For the existing development environment, routine releases use the validated workflow above.
`Deploy-Workbench.ps1 -FoundationOnly` can still create first-time foundational resources, but the
current repository workflow deliberately targets the existing named development Container App and is
not a generic empty-environment image publisher. A new environment therefore needs its own reviewed
exact-SHA image-publication workflow before the privileged script can consume a commit tag; do not
reintroduce a workstation build or bypass CI to bridge that bootstrap boundary.

The privileged deployment consumes an image that already exists in ACR:

```powershell
$commit = (git rev-parse origin/main).Trim()
$tag = $commit.Substring(0, 12)

./infra/workbench/Deploy-Workbench.ps1 `
  -ResourceGroupName '<resource-group>' `
  -FoundryAgentEndpoint '<canonical-hosted-agent-endpoint>' `
  -FoundationOnly

./infra/workbench/Deploy-Workbench.ps1 `
  -ResourceGroupName '<resource-group>' `
  -FoundryAgentEndpoint '<canonical-hosted-agent-endpoint>' `
  -ImageTag $tag
```

The script refuses a missing or non-commit-shaped image tag, resolves it to an ACR digest, and requires
the image's immutable revision annotation to match that commit prefix. It never invokes an image build.

## Deployment verification and rollback

The workbench release workflow deploys only an exact commit whose required CI jobs passed, resolves the
published image to an ACR digest, and waits for that digest to become the healthy latest-ready revision.
It then starts a short-lived Azure Container Instance with the dedicated `id-ofmfleet-pgverify` managed
identity. That runner obtains a real token for the workbench, verifies authenticated bootstrap and
project access, persists and revokes a `ValidationOnly` approval in the `Deployment validation` project,
and confirms a second non-allowlisted identity is rejected. It never calls the execution endpoint and
cannot project a migration mutation grant.

The ACI runner is deleted after every attempt. If verification fails after the image switch, the
workflow restores the digest-pinned previous template only when its immutable image labels declare
support for the platform schema the new revision may have applied. An incompatible or unlabelled
predecessor requires forward recovery and remains untouched. Image rollback does not reverse PostgreSQL
schema migrations or other external writes; platform migrations must remain additive, and any unknown
external-write outcome requires reconciliation rather than an automatic retry.

The separate `infra/supporting` stack remains the preview-only destination foundation for Blob Storage, Key Vault, and Azure SQL. Those services are deliberately not granted to the web identity. PostgreSQL sandbox execution is configured independently.