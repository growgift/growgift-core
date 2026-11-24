# Buy / Sell shares (ViewTrade) — how it works and what you must configure

## Flow (all server-side, Brokerage service)
Browser → `POST /api/v1/trading/orders` `{side, symbol, quantity}` + `Idempotency-Key` header. Nothing else.

Backend, per order (`ViewTradeSessionService` → `ViewTradeOrderService`):

| # | Postman request | Host (env) | Auth | Takes → gives |
|---|---|---|---|---|
| 1 | AccessTokenGenerate | UMA | api key/secret | → initial token (existing `ViewTradeAuthTokenProvider`) |
| 2 | GetUser (`POST api/v2/users/b2c/list`) | UMA | Bearer initial | caller's verified e-mail → `users_list[].id` (exact e-mail match, active, in GrowGift's branch) |
| 3 | ImpersonateLogin | UMA | Bearer initial | `{user_id}` → `tokens.access_token` |
| 4 | WatchmanAuthentication | UMA | Bearer impersonated | `{branch_id}` → `data.token` |
| 5 | PerformSSO | SSO | none | `{platform:"uma", token: watchman}` → `sessionId` (`stage` must be `ESTABLISHED`) |
| 6/7 | PlaceEquity / PlaceEquitySell | Trading | `Authorization: SessionId <sessionId>` | `POST api/orders/v2/place/equity`, `order.transaction` = BUY / SELL → `{OrderRef}` |

The trading account number is the `viewtrade_account` claim from the user's verified GrowGift JWT, never from the request body.

## Configuration you must supply
| Setting | Env var | Notes |
|---|---|---|
| UMA host | `VIEWTRADE_UMA_BASE_URL` | already used |
| API key/secret | `VIEWTRADE_API_KEY`, `VIEWTRADE_API_SECRET` | already used |
| Firm / branch | `VIEWTRADE_FIRM_ID` (1585), `VIEWTRADE_BRANCH_ID` (2641) | |
| **SSO host** | `VIEWTRADE_SSO_BASE_URL` | NEW, required (ViewTrade-Staging-SSO `baseUrl`) |
| **Trading host** | `VIEWTRADE_TRADING_BASE_URL` | NEW, required (ViewTrade-Staging-Trading `baseUrl`) |

Without the two new URLs, `GET /trading/status` returns `reason: "not_configured"` and the page says trading is unavailable.
Optional: `ViewTrade:SessionCacheMinutes` (default 5; 0 = never reuse), `ViewTrade:MaxOrderQuantity` (default 10000).

## Safety properties (each covered by a test)
- Tokens, Watchman token and session id never leave the Brokerage service; logs contain only stage names, HTTP status and ViewTrade's numeric `code`.
- A user can only ever be mapped to the ViewTrade user whose e-mail equals their own verified e-mail (substring search hits are ignored); sessions are cached per GrowGift user and never shared.
- Duplicate protection: the `Idempotency-Key` is claimed in Redis before anything is sent; a repeat returns the stored outcome and never reaches ViewTrade.
- Timeouts / 5xx / unreadable replies → state `unconfirmed`, never retried. The only automatic resend is after a 401/403 from Trading (rejected before processing), once, with a fresh session.
- "Accepted" is never presented as executed. Only a ViewTrade status that clearly means filled is shown as filled.
- Child accounts, unverified (KYC) users and users without an account get 403 with a reason.

## Known gaps (not guessed)
- The Postman collection has no sample for the order reply beyond `{OrderRef}`, so the status vocabulary is unknown: anything unrecognised is shown as "accepted", not "filled".
- No documented holdings endpoint is wired for sells: the page uses `/portfolio` as a hint only; ViewTrade's rejection is authoritative.
- Assumes `viewtrade_account` equals the Trading `accountNumber` (format like `ORBS-01-004099`). Confirm with one staging order.
- Session lifetime is unknown; sessions are reused only if the id is a JWT with a readable `exp`.
- Whole-share, MARKET, GTC orders only (as in the Postman bodies).

## Selling from My Profile (full or partial)

**Where:** My Profile → Portfolio → **Sell** on any holding → *Sell all* or *Sell some* (type a quantity or use 25/50/75%) → *Review sale* → *Confirm sell*. Buy/Sell page still works and now accepts fractional sell quantities too.

**What is sent to ViewTrade:** the existing `POST api/orders/v2/place/equity` with `transaction: SELL`, `orderType: MARKET`, `validity: GTC` (the saved `PlaceEquitySell` request) and `quantity`. The browser only sends `{side: SELL, symbol, quantity | sellAll}` + an `Idempotency-Key`; account, tokens and session stay server-side.

**Server-side safety (`POST /api/v1/trading/orders`, sells):**
* Holdings are re-read from ViewTrade before anything is sent: never sells more than held (`trading.insufficient_shares`), nothing held → `trading.not_held`, holdings unreadable → `trading.holdings_unavailable` (503, **no order placed**).
* `sellAll: true` → the server uses the exact quantity ViewTrade reports; a typed quantity equal to (or within 0.000001 of) the position is promoted to sell-all, so no dust is left.
* Sell quantities may be fractional, ≤ 6 decimal places (gifted holdings are fractional). Buying by quantity is still whole shares.
* Duplicate protection is unchanged: one Idempotency-Key per deliberate attempt, double-click = one order, a repeat of a key is answered from the recorded outcome (the holdings check is skipped for a repeat, otherwise it would wrongly reject it after the first sale). Timeouts/5xx are `unconfirmed` and are **never** auto-resent; the dialog says not to sell again until open orders are checked.
* Child logins, un-KYC'd users and users with no linked account cannot sell (button disabled with the reason; the server enforces it again).
* After a sell the profile reloads holdings and **Open orders** (a GTC market order placed when the US market is closed waits there until it executes).

**⚠️ Not verified against real ViewTrade:** the Postman sell example only shows a whole-share `quantity` (100). Whether ViewTrade accepts a **fractional `quantity`** on a MARKET sell is not documented in the collection. If it rejects, the dialog shows ViewTrade's rejection and nothing is sold. Please test with a very small sell in staging and tell me what comes back (an order reference, or the rejection reason); if fractional quantities are refused, the alternative is a dollar-amount ("Notional") sell like the buy path.
