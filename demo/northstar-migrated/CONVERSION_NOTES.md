# Application conversion notes

Application: Northstar Online Banking

The generated back end talks to Azure Database for PostgreSQL with Entra authentication. Oracle is
not in its data path and no database password exists anywhere in the output.

## Recognised workflow

The schema declares every table and column the retail banking workflows read and write, so a
working replacement for them was generated instead of CRUD screens. Recognition is structural:
the application name was never consulted. These routes are implemented over PostgreSQL:

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

The browser client carries these modules: Home, Open Account, Online Registration, Interest Calculator, Customer Login, Manager Login, Account Statement, Transaction Entry, Account Requests.
Nothing in either tier was recovered from a Forms module.

These routes are the whole HTTP surface. No per-table CRUD controller was generated, because
one would publish every column of every table — the migrated password hashes included — and
accept unvalidated writes with no session or role check. Any other path under `/api` answers a
JSON 404.

## Generated

| File | Purpose |
| --- | --- |
| `backend/pom.xml` | Spring Boot build for the migrated back end, with the Azure PostgreSQL Entra JDBC authentication plugin. |
| `backend/src/main/resources/application.yml` | Datasource bound to Azure Database for PostgreSQL using Entra, with schema validation rather than generation. |
| `backend/src/main/java/com/northstar/migrated/MigratedApplication.java` | Spring Boot entry point. |
| `backend/src/main/java/com/northstar/migrated/domain/BankAccountRequest.java` | JPA entity for BANK_ACCOUNT_REQUEST. |
| `backend/src/main/java/com/northstar/migrated/repository/BankAccountRequestRepository.java` | Spring Data repository for BANK_ACCOUNT_REQUEST. |
| `backend/src/main/java/com/northstar/migrated/domain/BankAccount.java` | JPA entity for BANK_ACCOUNT. |
| `backend/src/main/java/com/northstar/migrated/repository/BankAccountRepository.java` | Spring Data repository for BANK_ACCOUNT. |
| `backend/src/main/java/com/northstar/migrated/domain/BankStaffUser.java` | JPA entity for BANK_STAFF_USER. |
| `backend/src/main/java/com/northstar/migrated/repository/BankStaffUserRepository.java` | Spring Data repository for BANK_STAFF_USER. |
| `backend/src/main/java/com/northstar/migrated/domain/BankTransaction.java` | JPA entity for BANK_TRANSACTION. |
| `backend/src/main/java/com/northstar/migrated/repository/BankTransactionRepository.java` | Spring Data repository for BANK_TRANSACTION. |
| `backend/src/main/java/com/northstar/migrated/banking/BankingContracts.java` | Request and response shapes carried over from the source application. |
| `backend/src/main/java/com/northstar/migrated/banking/BankingValidation.java` | Field validation and normalisation carried over from the source application. |
| `backend/src/main/java/com/northstar/migrated/banking/PasswordHashing.java` | SHA-256 hashing with a constant-time comparison against the migrated bytea column. |
| `backend/src/main/java/com/northstar/migrated/banking/BankingSessionStore.java` | In-memory opaque bearer sessions with a role and an expiry. |
| `backend/src/main/java/com/northstar/migrated/banking/BankingSequences.java` | Advances each identifier sequence past the migrated rows under an advisory lock, and fails startup if it cannot. |
| `backend/src/main/java/com/northstar/migrated/banking/BankingRepository.java` | Transactional PostgreSQL access for every workflow. |
| `backend/src/main/java/com/northstar/migrated/banking/BankingController.java` | The source application's HTTP routes, status codes, and response shapes. |
| `backend/src/main/java/com/northstar/migrated/banking/BankingApiFallbackController.java` | One JSON 404 shape for any /api path the workflow service does not implement. |
| `backend/src/main/java/com/northstar/migrated/banking/BankingSpaController.java` | Serves the browser client for its own routes without ever intercepting /api. |
| `backend/src/test/java/com/northstar/migrated/banking/BankingControllerTest.java` | MockMvc tests for routing, validation, role protection, health, and the JSON 404. |
| `backend/src/test/java/com/northstar/migrated/banking/GeneratedSurfaceTest.java` | Asserts the unauthorized per-table CRUD controllers are absent from the build and the context. |
| `frontend/index.html` | Browser client shell carrying the source application's nine modules. |
| `frontend/public/app.js` | Browser client behaviour, calling every workflow route. |
| `frontend/public/styles.css` | Browser client presentation, unchanged from the source application. |
| `frontend/tests/app.test.js` | Executable browser interaction test for generated Northstar navigation. |
| `frontend/package.json` | Vite build for the static browser client. No framework dependency. |
| `frontend/package-lock.json` | Pinned dependency lock for offline generated UI verification. |
| `frontend/vite.config.ts` | Vite production build configuration. |
| `backend/Dockerfile` | Container build for the migrated back end. No credential is baked into the image. |
| `backend/.dockerignore` | Keeps build output and notes out of the image context. |
| `README.md` | What was generated, and what still has to be built by hand. |

## Not generated, and why

- **No behaviour came from a Forms module.** The workflows above were recognised from the
  schema and generated from this fleet's own template. Any screen or rule outside the listed
  modules and routes still has to be rebuilt against the real application.

## Before this replaces anything

1. Compile and run it. Generated code that has never been built is not working software, and this
   phase deliberately produces no attestation for that reason.
2. Re-enrol credentials. The migrated password hashes are unsalted SHA-256, because that is what
   the source stored, and the generated service can compare them but not strengthen them.
3. Check the routes against the real application. No per-table CRUD controller was generated, so
   anything the source did outside the listed routes is not served by anything yet.
4. Reconcile the migrated data against the source. Row counts are not a reconciliation.

Statements the parser did not interpret: 58.
