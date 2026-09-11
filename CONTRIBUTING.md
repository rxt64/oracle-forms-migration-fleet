# Contributing

This repository uses a pull-request workflow. `main` is always the state that has been built, tested,
and — when infrastructure changed — deployed and smoke-tested. Nothing lands on `main` directly.

## Branch naming

| Prefix | Use for |
|---|---|
| `feat/` | New behaviour a user can observe |
| `fix/` | A defect with a reproducible failure |
| `docs/` | Documentation only, no shipped code change |
| `infra/` | Bicep, deployment script, or CI changes |
| `chore/` | Dependency bumps and repository hygiene |

Use a short, specific slug: `fix/xml-export-misclassification`, not `fix/bug`.

## The loop

```bash
git checkout main && git pull
git checkout -b fix/short-slug
# make the change, add a test that fails without it
dotnet test
git commit
git push -u origin fix/short-slug
gh pr create --fill
```

## What a change must include

**A test that fails without the fix.** This is the single most important rule here. The whole product
claim is that the planner is deterministic and gated, so a regression in gating logic is a correctness
failure, not a cosmetic one. When you fix a misclassification, add both the case that was wrong *and* a
case proving the fix is not over-broad — otherwise the next person cannot tell the difference between a
narrow fix and one that simply disabled the rule.

**Evidence that you ran it.** Paste the `dotnet test` summary line into the PR. CI runs the same command,
but the author should have seen it pass first.

**No new secrets, ever.** See [docs/SECURITY.md](docs/SECURITY.md) for the invariants. The deployment
script writes the Entra client secret only to a temp file it deletes in a `finally` block; keep it that
way. Test fixtures intentionally contain fake credential strings such as `password=hunter2` so the
secret-rejection paths can be tested — do not "clean those up".

## Commit messages

Subject line in the imperative, under ~72 characters, describing the effect rather than the edit:

```
Stop counting build descriptors as Forms XML exports
```

Not `update SourceInventory.cs`. Use the body to explain *why*, especially the reasoning a reviewer
cannot reconstruct from the diff. If the change is driven by a real-world failure, say what failed.

## Comments in code

Write a comment only to state what the code cannot show on its own — a constraint, a rejected
alternative, or a non-obvious consequence. Do not restate the next line, and do not explain your change
to the reviewer in a code comment; that belongs in the commit message or the PR.

## Running things locally

See the Prerequisites and "Build, test, run" sections in the [README](README.md). Two traps worth
knowing before your first build:

- The csproj only rebuilds the React client **if `ClientApp/node_modules` already exists**. On a fresh
  clone, run `npm ci` in `src/oracle-forms-migration-fleet/ClientApp` first, or the app will start and
  serve nothing because `wwwroot/` was never generated.
- `wwwroot/` is generated and git-ignored. Never commit it.

## Reviewing

A reviewer should be able to answer three questions from the PR alone: what failed before, what test now
proves it cannot fail the same way again, and what the change does *not* cover. If a PR leaves a known
gap open on purpose, it must say so explicitly — an acknowledged limitation is fine, a silent one is not.
