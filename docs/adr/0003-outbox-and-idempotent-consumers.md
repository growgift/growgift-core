# ADR-0003: Transactional outbox and idempotent consumers

- Status: Accepted
- Date: 2026-11-25 (recorded)

## Context
A state change and the event announcing it must not diverge, and consumers see duplicates.

## Decision
Producers write the event to an `outbox_messages` table in the same transaction as the change; a relay publishes to Kafka. Consumers claim each `EventId` in Redis before acting. Topics are named `growgift.<domain>.<event>.v<N>` using an explicit `[EventDomain]` attribute.

## Consequences
- Delivery is at least once and effectively once in effect.
- Redis must be available for consumers; fail closed.
- Breaking event changes need a new version and a transition period (`EVENT-CATALOG.md`).
- Outbox backlog is a key health signal.
