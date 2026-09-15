# Deploying this demo

This file is hand-written and sits alongside generator output. `README.md`, `backend/`, and
`frontend/` are produced by the migration fleet and are overwritten whenever the demo is refreshed;
this file is not.

The `Deploy migrated Northstar demo` GitHub Actions workflow compiles both generated tiers, builds
the combined image in Azure Container Registry, previews and applies the Bicep deployment, and then
proves that the UI and PostgreSQL-backed account endpoint respond. The destination is a separate
Container App; it does not replace the source workflow replica or the migration workbench.

This public demo contains synthetic data only. The generated per-table CRUD endpoints have no
application-level authorization and must not be used for real customer or banking data.

## Refreshing the generated tree

`demo/northstar-migrated` is generator output, held to the generator by
`DemoFixtureTests.The_checked_in_demo_is_what_the_fleet_generates`. To refresh it after a generator
change:

```powershell
$env:FLEET_REGENERATE_DEMO = '1'
dotnet test tests/oracle-forms-migration-fleet.Tests --filter FullyQualifiedName~DemoFixtureTests
Remove-Item Env:FLEET_REGENERATE_DEMO
cd demo/northstar-migrated/frontend && npm install
```

`npm install` is only needed when the generated `package.json` changes; `package-lock.json` is
npm's output, not the generator's.
