# 003 — Implementation plan

> Spec Kit is not available in this repository; this plan was written by hand to match the structure of
> `specs/001-*` and `specs/002-*`.

## Shape

One new Hosting file holds the whole boundary. The pure planner in `Fleet/` is untouched, and the only
addition to `Fleet/Execution/` is a three-type interface the executor calls — small enough that the core
gains a hook rather than a dependency on hosting concerns.

```
HTTP request
  │
  ├── WorkbenchEndpoints.Actor(HttpContext)        owner + roles, headers only
  │
  ├── plan    → WorkbenchExecution.TryPlanRun
  │                └── WorkbenchTrustBoundary.Prepare(request, facts) → sanitized request
  │                     └── MigrationRunPlanner.Plan
  │
  └── execute → WorkbenchExecution.TryPrepareRun
                   ├── WorkbenchExecution.TryPrepare        ownership + path safety (unchanged)
                   ├── WorkbenchTrustBoundary.Prepare       sanitize + bind hashes
                   └── WorkbenchMutationAuthorizer          fail-closed, re-asked per phase
                        └── MigrationExecutor(root, adapters, authorizer)
```

## Decisions

**The snapshot hash is not on `SourceWorkspaceSummary`.** That record is serialized to the browser. A
second record, `SourceWorkspaceFacts`, carries the summary plus the hash and is returned only by
`Describe(owner, id)`. This is stronger than `[JsonIgnore]`: there is no path by which the value reaches a
serializer at all, so it cannot be re-exposed by a later change to serializer options.

**Hashes are structured, not concatenated.** `PlanInputHash` serializes a purpose-built canonical record
with fixed `JsonSerializerOptions`. Concatenating fields with a separator lets a value containing the
separator impersonate the next field; a serializer cannot be confused that way. Evidence is sorted so two
requests differing only in list order bind identically.

**The authorizer is optional on `MigrationExecutor`.** 29 existing call sites construct the executor with
the planner as the only gate, and that is correct for them: they supply their own request in-process. An
optional third parameter keeps their contract explicit and unchanged. The HTTP path, which serves an
untrusted caller, always passes one.

**Comparison of hashes is fixed-time.** These are public digests rather than secrets, so this is belt and
braces, but a boundary that leaks how far a forged value matched is a boundary worth not writing.

**Sanitization discards caller prose.** The `Source` and `Summary` on a declared item are replaced with
server-authored text. The operator's kind survives; their free text does not reach the planner, the run
report, or any reviewing agent.

## Order of work

1. `Fleet/Execution/PhaseMutationAuthorization.cs` — the interface and its two records. Build.
2. `MigrationExecutor` — optional authorizer, checked before mutating phases. Build.
3. `SourceWorkspaceService` — snapshot hash at acquisition, `SourceWorkspaceFacts`, `Describe`. Build.
4. `Hosting/WorkbenchTrustBoundary.cs` — actor, grant, store, service, sanitizer, hashes, authorizer. Build.
5. `WorkbenchExecution` — `TryPlanRun` and `TryPrepareRun`, the two methods the endpoints call.
6. `WorkbenchEndpoints` — both endpoints routed through them; `Actor` reads headers only.
7. `WizardApp.tsx` — `workspaceId` on the plan request when a copy exists.
8. Tests, then this spec folder.

## Risk

The plan endpoint's signature changed from a bound `MigrationRunRequest` parameter to a raw body read.
That is the same technique the execute endpoint already uses and keeps `workspaceId` out of the shared
run-request contract, but it means a malformed body now yields a blocked plan rather than an ASP.NET 400.
`MigrationRunPlanner.Plan(null)` already handles that case and returns a blocked plan naming the reason.
