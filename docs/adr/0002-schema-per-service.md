# ADR-0002: One database, one schema and role per service

- Status: Accepted
- Date: 2026-11-25 (recorded)

## Context
Eight services need persistence. Separate databases cost more to run early on; a shared schema couples the services.

## Decision
One PostgreSQL database `growgift`. Each service owns one schema and one login role that can touch only that schema. Services never join across schemas; cross-service facts travel as events.

## Consequences
- Low hosting cost now, with a clear path to separate databases later.
- A compromised service credential cannot read other services' data.
- `init.sql` creates roles, schemas and the append-only audit trigger; production needs a migration process (`DEPLOYMENT-AND-RELEASE.md` section 5).
- No cross-service transactions; use the outbox (ADR-0003).
