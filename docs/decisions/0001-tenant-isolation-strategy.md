# ADR-0001 — Tenant isolation: shared schema + RLS + composite foreign keys

Status: **Proposed** (awaiting approval) · Date: 2026-09-10 · Deciders: product owner, lead engineer

## Context

The platform holds the receivables ledger of SMEs that may compete with each other, in
one deployment. `CLAUDE.md` requires a `TenantId` on every business table and
server-side enforcement. Pilot scale is ≤ 20 tenants, ≤ 50k invoices each (A-16).

Options considered:

1. **Database per tenant** — strongest isolation; migration and connection management
   cost scales linearly with tenants; cross-tenant platform queries become painful;
   overkill at 20 tenants on one node.
2. **Schema per tenant** — good isolation, but PostgreSQL migration tooling across N
   schemas is error-prone and the failure mode ("schema 14 missed a migration") is
   silent and nasty.
3. **Shared schema with `tenant_id`** — simplest operationally, but a single missing
   `WHERE` clause is a cross-tenant breach.

## Decision

**Shared database, shared schema, `tenant_id` on every business row, defended by three
independent layers**, each of which must fail for a breach to occur:

- **L1 Application** — EF Core global query filter, tenant from the validated token
  claim only (SEC-20).
- **L2 Database** — Row-Level Security, `ENABLE` **and** `FORCE`, policy on
  `current_setting('app.tenant_id')` set transaction-locally; the application role has
  no `BYPASSRLS` and does not own the tables (SEC-21, SEC-23).
- **L3 Schema** — composite foreign keys `(tenant_id, id)` so a cross-tenant *reference*
  is unrepresentable (DM-10).

Each layer is tested **independently**: T-74 proves L2 works with L1 bypassed; T-75
proves L3 works with L1 and L2 bypassed.

## Consequences

**Positive:** one migration path; simple operations; cheap platform-wide reporting;
defence in depth rather than a single control; the failure mode of the most likely bug
(a forgotten filter) is caught by L2 rather than becoming a breach.

**Negative:** blast radius is concentrated in one database; RLS adds a small per-query
cost; the `set_config` discipline must be honoured by every background job (SEC-22),
and connection pooling requires the transaction-local setting (SEC-23) — the classic
bug this pattern invites.

**Revisit when:** ~100 tenants, a tenant with a contractual isolation requirement, or a
regulated customer. The migration path to schema-per-tenant remains open because every
table already carries `tenant_id`.
