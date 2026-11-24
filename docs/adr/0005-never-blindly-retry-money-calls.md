# ADR-0005: Never blindly retry money or goods calls

- Status: Accepted
- Date: 2026-11-25 (recorded)

## Context
A timeout does not say whether ViewTrade placed an order or Shippit created a shipment. Resending can duplicate real money or real parcels.

## Decision
Calls that create an order or shipment carry an idempotency key or client reference. When the outcome is unknown (timeout, unreadable reply, crash) the record becomes `unconfirmed` and is never resent automatically. A person or a lookup by reference resolves it. Clients show "unconfirmed" as "being checked", never as "failed".

## Consequences
- Some orders and shipments need manual attention (`OPERATIONS-RUNBOOK.md` sections 4 and 5).
- Regression tests protect this rule (`TESTING-STRATEGY.md` section 5).
- Retries are allowed only after confirming nothing was created.
