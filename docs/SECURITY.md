# Security model

This service accepts a URL to someone else's source repository and runs `git` against it inside a
container that holds a managed identity. That combination is the whole threat model: the input is
untrusted, and the process it runs in is not.

The invariants below are load-bearing. Each one has tests behind it. If you are about to relax one,
that is the review conversation — not an implementation detail.

## 1. The browser is never trusted with anything

The front end receives the evidence catalogue and its own plan. It never receives:

- the Foundry agent endpoint,
- any Azure or Entra token,
- the Application Insights connection string.

The endpoint is allowlisted and canonicalized **server-side** in `Deploy-Workbench.ps1`, which rejects a
non-canonical endpoint before deploying (wrong host, non-default port, embedded user info, a fragment,
an unexpected query, or a path that does not match the declared account and project).

## 2. Source classification never opens a file

`SourceInventory` classifies an acquired tree **by file name only**. It never reads file contents, so a
hostile archive cannot influence the result beyond the names it declares. Every classification rule must
stay name-based.

This matters more than it first appears: the inventory feeds the evidence ticks, and evidence gates
whether the workbench will authorize artifact generation. Parsing attacker-controlled file *contents* to
make that decision would put a parser on the path to an authorization decision.

## 3. Acquisition is sandboxed, quota'd, and disposable

`SourceWorkspaceService` enforces all of the following:

| Control | Value | Why |
|---|---|---|
| Scheme | `https` only | No `git://`, `ssh://`, or `file://` |
| Host | Allowlist: `github.com`, `www.github.com`, `dev.azure.com`, `gitlab.com`, `bitbucket.org` | Stops the server being used to reach arbitrary internal hosts |
| Embedded credentials | Rejected | A URL must never carry a token into the process |
| Leading `-` | Rejected | A URL or branch starting with `-` would be parsed by `git` as an option |
| Branch names | `IsSafeRefName` | Same argument-injection concern |
| `GIT_ALLOW_PROTOCOL` | `https` | Blocks protocol downgrade and `ext::` command execution |
| `core.symlinks` | `false` | A cloned symlink cannot reach outside the sandbox |
| Clone shape | `--depth 1 --single-branch --no-tags` | Minimum history, minimum surface |
| Clone timeout | 5 minutes | A hostile remote cannot hang a replica forever |
| Per workspace | 60,000 files / 512 MB | Zip-bomb and clone-bomb ceiling |
| Per archive | 256 MB written | Enforced *during* extraction, not after |
| All workspaces | 2 GiB | Container Apps allocates 4 GiB ephemeral at 1 vCPU and the image shares it |
| Lifetime | 4 hours, swept on a timer and at shutdown | Nothing lingers |

Zip entries are resolved with `Path.GetFullPath` and rejected unless they remain under the workspace
root, which is the standard Zip Slip defence. Extracted files are marked read-only.

Workspaces are **owner-scoped**: `Get` and `Release` both take the caller's identity, so one signed-in
user cannot read or delete another's workspace.

## 4. The agent refuses credential material

`SecretRejectingChatClient` rejects credential-looking input *before model inference*, and
`MigrationRunPlanner` rejects credential-like evidence metadata, artifact paths, attestation signers, and
approval notes. The refusal is fixed text and does not echo the supplied value back.

The eval seed dataset and several test fixtures deliberately contain strings such as
`password=hunter2` and `api_key: abc123`. They are negative-test inputs. Do not "sanitize" them.

## 5. Least privilege at runtime

The container runs as a non-root `app` user and holds a user-assigned managed identity with exactly two
Azure role assignments:

- **AcrPull**, scoped to the registry — to pull its own image.
- **Azure AI Foundry Agent Consumer**, scoped to the project — to call its own agent.

Sign-in is Microsoft Entra ID, single tenant. The same identity is separately pre-provisioned as a
PostgreSQL principal with only the sandbox schema privileges required by migration and reconciliation.
That database authorization is not an Azure RBAC assignment. The host fixes `SANDBOX_PGHOST`,
`SANDBOX_PGUSER`, and `SANDBOX_PGDATABASE`; a browser request cannot select another server or credential.

The generated back-end stack is host-owned for the same reason. `TARGET_BACKEND_STACK` names it —
`JavaSpringBoot` (the default when the variable is unset) or `AspNetCore` — and an unrecognised value
stops the host rather than falling back to a stack nobody configured. The value is part of the target
profile's canonical hash, so changing it records a new immutable profile version and strands every
approval issued against the previous one.

A run request still carries a `target` object, because the planner needs one, so the server compares it
to the project's target profile rather than trusting it. A request whose database, front end, or back
end is not the one the profile records is refused with `409` — both when an approval is requested, so no
approval record is created for a destination that was never configured, and again when a run is
prepared, so an approval that predates this check cannot carry one through. The plan-input hash does not
cover this on its own: it detects drift between an approval and the run it was issued for, and an
approval requested for the wrong stack hashes the wrong stack consistently. Both request readers accept
enum names only, so an ordinal cannot name a stack by number.

## 6. Approval-gated execution

Source analysis, normalization, conversion, build validation, PostgreSQL sandbox migration, and
reconciliation adapters are implemented. Conversion artifacts are confined to the owner-scoped session
workspace. Database writes occur only when the host has bound the PostgreSQL gateway and the planner has
accepted a separate execution approval. The target endpoint is host-owned, never caller-supplied.

Differential behavior testing, human acceptance, and production cutover have no execution adapter. A
lifecycle phase counts as performed only when its adapter succeeds and any required attestation cites a
real artifact. Anything that lets the UI hide a sandbox mutation or imply unperformed work is a security
bug, not a UX bug.

## Reporting

Report security concerns through
[GitHub private vulnerability reporting](https://github.com/rxt64/oracle-forms-migration-fleet/security/advisories/new).
Do not place credentials, exploit details, customer data, or other sensitive material in a public issue.
