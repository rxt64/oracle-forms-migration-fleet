# Model review of the generated schema

Application: Northstar Online Banking
Target: PostgreSql

These findings come from a language model reading the generated DDL. They are **unverified**:
no statement below was executed against a database, and none of them gated, approved, or
changed anything in this run. Treat each one as a lead to confirm, not as a result.

## Claimed to fail on the target engine (2)

- **generated DDL**: The supplied content is XML, YAML, and Java source rather than PostgreSQL DDL. Executing it as SQL will fail immediately at the XML opening token.
- **bank_account_request**: The application maps and queries bank_account_request, but no CREATE TABLE statement defines it in the supplied DDL. With Hibernate ddl-auto set to validate, startup fails unless this relation already exists externally.
  - Suggested: `CREATE TABLE bank_account_request (request_id bigint PRIMARY KEY, branch_code text NOT NULL, account_kind text NOT NULL, honorific text, given_name text NOT NULL, family_name text NOT NULL, date_of_birth date NOT NULL, work_phone text, home_phone text, street_address text NOT NULL, region_code text NOT NULL, postal_code text NOT NULL, email_address text NOT NULL, request_status text NOT NULL, submitted_at timestamp NOT NULL, decided_at timestamp);`

