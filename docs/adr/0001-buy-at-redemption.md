# ADR-0001: Buy shares at redemption, not at purchase

- Status: Accepted
- Date: 2026-11-25 (recorded)

## Context
A gift could buy shares when the buyer pays, and hold them for the recipient (custody), or buy only when the recipient redeems.

## Decision
No shares are bought when a gift is purchased. The gift holds a dollar amount of a chosen symbol. At redemption, Brokerage places a dollar (Notional) BUY directly into the recipient's own ViewTrade account.

## Consequences
- GrowGift never holds securities or client assets for anyone, which removes pre-redemption custody.
- The recipient gets whatever quantity the dollars buy that day; the gift is a fixed amount, not a fixed number of shares. Marketing and terms must say so.
- Redemption needs the recipient's identity check and a ViewTrade account first.
- The omnibus acquisition path (`OmnibusLot`, `AcquireForOmnibusAsync`) stays in code but unused. Remove it or reinstate it by a new ADR.
- The buy uses client reference `gg-gift-<giftId>` for idempotency and reconciliation.

## Alternatives considered
Buy at purchase into an omnibus account, then transfer. Rejected for custody and regulatory weight.
