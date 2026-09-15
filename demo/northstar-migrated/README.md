# Northstar Online Banking — migrated application tier

Generated from the converted Oracle schema. Oracle is not in the data path: the back end talks to
Azure Database for PostgreSQL using Entra authentication, so no database password exists.

## What is here

- 4 JPA entities, repositories, and REST controllers
- A typed React client and a table screen over the first entity

## What is deliberately not here

- **Forms behaviour.** `.fmb` modules are a proprietary binary this build cannot read, so no trigger,
  block, or navigation rule was extracted. The screens are CRUD over tables, not the original forms.
- **PL/SQL logic.** Package and trigger bodies were not translated; see the conversion report.
- **Authorization.** Every endpoint is open. Do not expose this until access control is added.

## Running it

```bash
export PGHOST=<server>.postgres.database.azure.com
export PGDATABASE=postgres
export PGUSER=<managed identity name>
cd backend && mvn spring-boot:run
```

## Deployment

The `Deploy migrated Northstar demo` GitHub Actions workflow compiles both generated tiers, builds
the combined image in Azure Container Registry, previews and applies the Bicep deployment, and then
proves that the UI and PostgreSQL-backed account endpoint respond. The destination is a separate
Container App; it does not replace the source workflow replica or the migration workbench.

This public demo contains synthetic data only. The generated application has no application-level
authorization and must not be used for real customer or banking data.
