# ADR-0007: Send sells to ViewTrade as dollar (Notional, DAY) orders

- Status: Accepted
- Date: 2026-11-25 (recorded)

## Context
Sell orders sent by share quantity were rejected by ViewTrade. Fractional holdings can only be sold as dollar-value orders.

## Decision
Sells are re-checked against live holdings, then sent as a dollar order of type Notional with time in force DAY and an expected value. "Sell all" uses the exact position. If a price is not available the API answers 503 `price_unavailable` and nothing is sent.

## Consequences
- The user sees an estimate; the final amount follows the market.
- Behaviour of "sell all" at ViewTrade should be confirmed in the sandbox before go-live.
- See `TRADING.md` and the "Sell orders" section of `CHANGES-latest.md`.
