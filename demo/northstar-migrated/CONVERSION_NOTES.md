# Application conversion notes

Application: Northstar Online Banking

The generated back end talks to Azure Database for PostgreSQL with Entra authentication. Oracle is
not in its data path and no database password exists anywhere in the output.

## Generated

| File | Purpose |
| --- | --- |
| `backend/pom.xml` | Spring Boot build for the migrated back end, with the Azure PostgreSQL Entra JDBC starter. |
| `backend/src/main/resources/application.yml` | Datasource bound to Azure Database for PostgreSQL using Entra, with schema validation rather than generation. |
| `backend/src/main/java/com/northstar/migrated/MigratedApplication.java` | Spring Boot entry point. |
| `backend/src/main/java/com/northstar/migrated/domain/BankAccountRequest.java` | JPA entity for BANK_ACCOUNT_REQUEST. |
| `backend/src/main/java/com/northstar/migrated/repository/BankAccountRequestRepository.java` | Spring Data repository for BANK_ACCOUNT_REQUEST. |
| `backend/src/main/java/com/northstar/migrated/api/BankAccountRequestController.java` | REST endpoints for BANK_ACCOUNT_REQUEST. |
| `backend/src/main/java/com/northstar/migrated/domain/BankAccount.java` | JPA entity for BANK_ACCOUNT. |
| `backend/src/main/java/com/northstar/migrated/repository/BankAccountRepository.java` | Spring Data repository for BANK_ACCOUNT. |
| `backend/src/main/java/com/northstar/migrated/api/BankAccountController.java` | REST endpoints for BANK_ACCOUNT. |
| `backend/src/main/java/com/northstar/migrated/domain/BankStaffUser.java` | JPA entity for BANK_STAFF_USER. |
| `backend/src/main/java/com/northstar/migrated/repository/BankStaffUserRepository.java` | Spring Data repository for BANK_STAFF_USER. |
| `backend/src/main/java/com/northstar/migrated/api/BankStaffUserController.java` | REST endpoints for BANK_STAFF_USER. |
| `backend/src/main/java/com/northstar/migrated/domain/BankTransaction.java` | JPA entity for BANK_TRANSACTION. |
| `backend/src/main/java/com/northstar/migrated/repository/BankTransactionRepository.java` | Spring Data repository for BANK_TRANSACTION. |
| `backend/src/main/java/com/northstar/migrated/api/BankTransactionController.java` | REST endpoints for BANK_TRANSACTION. |
| `frontend/src/types.ts` | TypeScript shapes matching the generated entities. |
| `frontend/src/api.ts` | Typed fetch client for the generated endpoints. |
| `frontend/package.json` | Pinned React, TypeScript, and Vite build dependencies. |
| `frontend/tsconfig.json` | Strict TypeScript compiler configuration for the generated UI. |
| `frontend/vite.config.ts` | Vite production build configuration. |
| `frontend/index.html` | React application host page. |
| `frontend/src/main.tsx` | React browser entry point. |
| `frontend/src/vite-env.d.ts` | Vite ambient types for import.meta.env. |
| `frontend/src/App.tsx` | React screen generated from Forms block REQUEST_BLOCK. |
| `backend/Dockerfile` | Container build for the migrated back end. No credential is baked into the image. |
| `backend/.dockerignore` | Keeps build output and notes out of the image context. |
| `README.md` | What was generated, and what still has to be built by hand. |

## Not generated, and why

- **BANK_ACCOUNT_REQUEST_FORM.BANK_ACCOUNT_REQUEST_FORM.WHEN-NEW-FORM-INSTANCE** — Forms trigger logic is PL/SQL bound to a client-side event model with no PostgreSQL or React equivalent. It was not translated; the generated screen has the field but not this behaviour.
- **BANK_ACCOUNT_REQUEST_FORM.REQUEST_BLOCK.WHEN-BUTTON-PRESSED** — Forms trigger logic is PL/SQL bound to a client-side event model with no PostgreSQL or React equivalent. It was not translated; the generated screen has the field but not this behaviour.
- **BANK_ACCOUNT_REQUEST_FORM.REQUEST_BLOCK.WHEN-VALIDATE-RECORD** — Forms trigger logic is PL/SQL bound to a client-side event model with no PostgreSQL or React equivalent. It was not translated; the generated screen has the field but not this behaviour.
- **BANK_ACCOUNT_REQUEST_FORM.REQUEST_BLOCK.POST-QUERY** — Forms trigger logic is PL/SQL bound to a client-side event model with no PostgreSQL or React equivalent. It was not translated; the generated screen has the field but not this behaviour.
- **BANK_ACCOUNT_REQUEST_FORM.REFRESH_SUMMARY** — A program unit inside the module was not translated. It has to be reimplemented in the back end.

## Before this replaces anything

1. Compile and run it. Generated code that has never been built is not working software, and this
   phase deliberately produces no attestation for that reason.
2. Add authentication and authorization. Every endpoint is currently open.
3. Test the translated PL/pgSQL against the original behaviour, then decide for each rule
   whether it stays in the database or moves into this tier. Nothing here calls it yet.
4. Reconcile the migrated data against the source. Row counts are not a reconciliation.

Statements the parser did not interpret: 58.
