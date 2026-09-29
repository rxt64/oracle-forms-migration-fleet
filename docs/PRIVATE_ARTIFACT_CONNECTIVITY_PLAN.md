# Private Artifact Connectivity Plan

Status: planning only, September 23, 2026. Nothing here is implemented, approved, or provisioned. Silence
is not approval. The workbench remains frozen until its ongoing release completes; this plan changes no
infrastructure, delivery tracker, Azure resource, or GitHub configuration.

## Current Boundary And Blocker

- `cae-ofmfleet-dev-ykbpnrpd` is the existing Consumption Container Apps environment. It has no VNet integration or workload profiles and cannot be retrofitted into a different network topology.
- The workbench and internal-only Oracle app `ca-ofmfleet-db-dev-ykbpnrpd` share that environment. Oracle TCP 1521 is not public.
- The installed Meridian fixture is original source-lab material on the existing Oracle container. Durable-volume or immutable-image recovery is unverified; moving or replacing the app risks losing it. It is not customer data, but preserve it until recovery is proved.
- `stofmdotdevykbpnrpd` has public networking and Shared Key disabled, with no private endpoint or VNet path. The unapplied Azure Files template would enable both; that broadening is refused and excluded from the current release.
- `ofm_dotnet_pilot` and its isolated runtime/workbench identities are ready. No generated target SQL or customer data has been applied.
- The gateway uploads a bundle to Blob and dispatches its URI to GitHub. A GitHub-hosted runner cannot fetch private-only Blob from the public internet. Credentials must never enter generated code, workflow artifacts, prompts, or chat.
- GitHub App configuration is missing or unconfirmed. If any required value is absent, the gateway stays unregistered and correctly reports `GatewayUnavailable`.

## Options

### A. Bounded PostgreSQL Artifact Store And Product Relay (recommended first)

Keep the current workbench, Oracle app, environment, storage account, and PostgreSQL foundation untouched.
Add an `ofm_platform` artifact store with bounded chunks, content hash, operation/run binding, immutable
completion state, expiry, and per-tenant/run quotas.

The workbench stores the bundle and dispatch claim transactionally. A narrow product relay lets the
trusted workflow download the completed bundle and upload its result. Using a configured audience, the
relay validates GitHub Actions OIDC issuer, audience, repository, workflow/ref, operation, expiry, and
content hash. The app keeps managed-identity PostgreSQL access. No PAT, SAS, key, PEM, database credential,
or Azure credential enters generated content or build steps.

Restore checkpoints record hash, byte count, completion, operation binding, and row-version/ETag-equivalent
concurrency. Resume accepts exact matches, refuses partial/different bytes, and never blind-overwrites.
Result admission retains the source, plan, target, artifact, workflow-run, and image-digest bindings.

Trade-off: this new bounded platform-data feature increases PostgreSQL storage, backup, I/O, retention,
and maintenance. It is the minimum practical topology change because neither app moves and Oracle stays
private. Approve only after limits, cleanup, backup impact, relay threat model, and measured growth review.

### B. Private Blob In A New VNet-Integrated Container Apps Environment

Create a VNet with approved non-overlapping CIDRs, new-environment and private-endpoint subnets, Blob
private endpoint, and private DNS links. Deploy a parallel workbench with only container-scoped Blob data
access. Workspace checkpoints must bind Blob ETag and content hash; restore refuses stale, partial, or
different bytes. Keep all old apps untouched during validation.

Do not assume a storage private endpoint makes the internal Oracle app reachable. The old environment has
no VNet; its internal app is not automatically routable from a new environment. Direct source access needs
a separately proved private connector, Private Link/proxy, or product export relay. Oracle TCP stays
non-public. Otherwise, reprovision the owned Meridian fixture from checked-in scripts after separate cost
and execution approval. That is source provisioning, not migration, native Forms qualification, or UI
equivalence evidence. Do not copy customer SQL or source data by hand.

GitHub-hosted runners still cannot reach private Blob directly, so this option also needs the authenticated
product relay, a reviewed private runner, or another approved broker. Prefer OIDC without long-lived keys.

Trade-off: strongest Blob isolation and better large-artifact economics, but adds parallel compute,
network/DNS operations, auth/release duplication, and source risk. It is the scale path, not first unblocker.

## Authentication And Authorization Review

- Preserve the hostname where possible. Otherwise review Entra redirect/logout URIs, origins, Easy Auth tenant/audience, operator allowlist, and callbacks before traffic switches.
- Recreate only proved identity scopes: ACR pull, Foundry consumer, required Key Vault read, target PostgreSQL grants, and relay/platform-artifact or container-scoped Blob data access.
- Review GitHub-to-relay separately from Azure release federation. Pin repository, `generated-target.yml`, `refs/heads/main`, audience, optional environment, and short token lifetime; confirm release subjects and role scopes still match the reviewed workflow.
- Generated code and untrusted commands receive no Azure, PostgreSQL, GitHub App, or relay credential. Credential-bearing publish steps stay isolated and trusted.

## Phases And Approval Gates

1. **Freeze and inventory:** await the release; retain old apps/data; record revisions, fixture checkpoint, auth, roles, and storage posture.
2. **Design review:** choose A/B; approve quotas, retention, threat model and, for B, exact CIDRs, DNS, source path, hostname, and cost. Guess no CIDR.
3. **Scoped what-if:** require a named deployment and sanitized diff. Reject deletes, replacements, public-network or Shared Key enablement, broad RBAC, boundary expansion, and unexplained existing-resource changes.
4. **Implementation review:** test OIDC claims, operation binding, create-only writes, ETag/row-version conflicts, hashes, partial upload, expiry, quota, cleanup, resume, and refusal.
5. **Parallel validation:** only after explicit approval, prove Easy Auth, identities, relay transfer/restore, result binding, and source connectivity without touching old traffic/source.
6. **Product acceptance:** run through the GUI and existing gates. `SyntheticExport` success proves neither native Forms nor behavior equivalence.
7. **Traffic or cleanup decision:** require separate explicit approval. Never auto-delete an old app, fixture, storage object, role, endpoint, or database.

## Cost Envelope

Planning ballpark, not a live quote: one private endpoint is about **$7.30/month** and private DNS about
**$0.50/month**, plus Blob, transfer, relay, parallel compute, and logging. PostgreSQL relay cost is
incremental storage, backup, I/O, and app compute. Measure bundles and obtain regional pricing/sign-off.

## GitHub App Owner Checklist

- Check for a product-owned App first; restricted installation APIs do not prove none exists.
- Record non-secret `AppId`, repository-only `InstallationId`, owner/repository, and Key Vault **secret URI**. Never transmit the PEM through chat, source, logs, or environment variables.
- Install only on `rxt64/oracle-forms-migration-fleet`, with **Actions: read and write**; no PAT, contents write, packages, organization permission, webhooks, or all-repository installation.
- Confirm least-privilege secret read and define key rotation/revocation.

## Decisions Required Before Any Apply

1. Select PostgreSQL relay first or private Blob/new environment, and name the cost/security owners.
2. Approve artifact maximum size, total quota, retention, backup treatment, and deletion policy.
3. Approve the relay OIDC trust tuple and whether the current hostname must be preserved.
4. For option B, approve exact CIDRs/DNS design and one proved source-connectivity approach.
5. Confirm existing GitHub App identifiers or authorize a repository owner to create/install one.
6. Approve the exact no-delete what-if and known modifications. Only then request separate apply approval;
  this plan and reviewer silence grant no deployment authority.