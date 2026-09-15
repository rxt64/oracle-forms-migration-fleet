# Northstar Online Banking — migrated application tier

Generated from the converted Oracle schema. Oracle is not in the data path: the back end talks to
Azure Database for PostgreSQL using Entra authentication, so no database password exists.

## Demonstrated version scope

This generated pilot represents an Oracle Forms `12.2.1.4`-style synthetic XML export backed by
Oracle Database Free 23, migrated to Azure Database for PostgreSQL 16. The Forms XML is hand-authored
test evidence, not an export from a licensed Forms runtime. This is not a general compatibility claim
for other Oracle Forms or Oracle Database releases. See `docs/COMPATIBILITY.md` in the fleet repository.

## What is here

- 4 JPA entities and Spring Data repositories
- A workflow service that reimplements the source application's endpoints over PostgreSQL:

  - `GET /healthz`
  - `GET /api/health`
  - `POST /api/customer/login`
  - `POST /api/manager/login`
  - `POST /api/online-registration`
  - `POST /api/account-requests`
  - `POST /api/interest`
  - `GET /api/customer/statement`
  - `POST /api/customer/transactions`
  - `GET /api/manager/requests`
  - `POST /api/manager/requests/{requestId}/approve`
  - `DELETE /api/session`

- A browser client with the source application's modules: Home, Open Account, Online Registration, Interest Calculator, Customer Login, Manager Login, Account Statement, Transaction Entry, Account Requests.
- Spring Boot tests over the generated routes, run with `mvn test`.

## Where the behaviour came from

The schema declares every table and column these workflows use, so the generator recognised the
workflow and emitted a working replacement instead of CRUD screens. Balance, simple interest, and
approval are reimplemented from schema evidence — sequences, the CR/DR constraint, the request
status constraint — and from this fleet's workflow template. **Nothing here was recovered from a
Forms module.** The only Forms XML available for this estate covers a single block.

## What is deliberately not here

- **Per-table CRUD controllers.** They would publish every column of every table, the migrated
  password hashes included, and accept unvalidated writes with no session or role check, so none
  was generated. Any path under `/api` that is not a workflow route answers 404.
- **Stronger credentials.** Passwords stay unsalted SHA-256 because that is what was migrated.
- **Screens outside the modules listed above.**

## Running it

```bash
export PGHOST=<server>.postgres.database.azure.com
export PGDATABASE=postgres
export PGUSER=<managed identity name>
cd backend && mvn spring-boot:run
```
