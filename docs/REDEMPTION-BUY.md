# Redeemed gifts → real ViewTrade order

## What was wrong
After a redeem, Brokerage's `GiftRedeemedConsumer` called `ViewTradeProvider.BuyForAccountAsync`, which posted an invented body to a placeholder `api/v1/orders`. It never reached ViewTrade's trading API, so no shares appeared there (the gift still showed in My Profile because that comes from Gifting). Separately, the consumer claimed the gift in Redis *before* buying, so a failed buy was never retried.

## Now
For each `gift-redeemed.v1` the consumer:
1. Returns if a `redemption_purchases` row for the gift already exists (durable duplicate guard).
2. Finds the recipient's stored ViewTrade user id (`onboarding_applications.ViewTradeUmaUserId`, set at onboarding or when an existing account was linked). None → row `needs_attention`, error logged.
3. Runs the same ViewTrade sign-in chain as Buy/Sell (by that user id, no e-mail search), then
   `POST api/orders/v2/place/equity` with `orderType: "Notional"`, `validity: "DAY"`, `expectedValue: <gift dollars>`, `account.accountNumber: <viewtrade_account>`.
4. Stable idempotency key `gg-gift-{giftId}` → a redelivery never places a second order.

| Outcome | Result |
|---|---|
| order reference returned | row saved with `OrderState` accepted/pending/partiallyfilled/filled (reply has only `OrderRef`, so anything unrecognised = accepted), **no** `ownership-transferred.v1` (no fill quantity yet) |
| session/auth failure (nothing placed) | exception → consumer retries every 5 s |
| ViewTrade 4xx | `rejected` row, logged, not retried |
| timeout / 5xx / unreadable | `unconfirmed` row, logged, **never resent** — check ViewTrade by hand |

## One database change you must run (no EF migrations in this repo)
```sql
ALTER TABLE brokerage.redemption_purchases
  ADD COLUMN "OrderState" text NOT NULL DEFAULT 'accepted',
  ADD COLUMN "UsdAmount"  numeric(18,4) NULL,
  ADD COLUMN "FxRate"     numeric(18,8) NULL;
```

## Open points
- **Currency (done):** gifts are AUD, ViewTrade trades USD. At redemption the amount is converted (`FxRateProvider`): `expectedValue = floor_to_cent(AUD × rate)`, so we never spend more than the gift is worth. The rate (Frankfurter `GET /v2/rate/aud/usd`, only the `rate` field is read; cached 10 min) and the USD amount are stored per purchase (`FxRate`, `UsdAmount`) next to the original AUD amount. No rate / unreadable / outside the AUD sanity band 0.30–1.20 → nothing is sent and the consumer retries. Config: `Fx:BaseUrl` / `FX_BASE_URL` (swap the source), `FX_AUD_USD_RATE` or `Fx:FixedRates:AUD` (pin a rate for testing).
- **Rate vs. what the customer saw:** the AUD→USD rate is taken at redemption, not at purchase of the gift box, so the USD value can differ from the day the gift was bought. Decide if that is acceptable for your terms.
- **Fills:** no order-status endpoint is known, so fill quantity/price aren't recorded. Notional orders with `DAY` validity placed outside market hours may not execute until the next session.
- **Portfolio on My Profile** still uses a placeholder holdings path, so it won't show ViewTrade positions yet (needs the real holdings/account-balance response sample).
- **Dev-simulated KYC** gives a fake `VT-DEV-…` account with no ViewTrade user → `needs_attention`. Use a real (or linked existing) ViewTrade account.
- **Gifts redeemed before this fix** aren't retried automatically (their message was already consumed). Redeem a fresh code to test, or reset the `gift-redeemed` consumer-group offset to replay.
