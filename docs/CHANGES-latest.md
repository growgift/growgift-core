# Latest changes

## Sell shares from My Profile (full or partial)
- **Sell** button on every holding → dialog: *Sell all* / *Sell some* (typed amount or 25/50/75%), estimate, review, confirm, result. See docs/TRADING.md.
- Backend re-checks holdings with ViewTrade (never oversells; "sell all" uses the exact position), supports fractional sell quantities (≤ 6 dp), keeps idempotency/no-blind-retry rules.
- Open orders explainer now covers sells; Buy/Sell page accepts fractional sells.

## Shippit shipping (staging only) — see docs/SHIPPIT.md
- Paid physical orders now create a durable shipment job in Fulfillment; a background worker creates the Shippit order, fetches the label (with retries), books it, and follows tracking via webhooks (polling only as a fallback).
- Staging-only is enforced in code (`ShippitGuard`): production host/URL/env makes the service refuse to start. API secret is env-only (`SHIPPIT_API_SECRET`).
- No duplicate shipments: unique order index + persist-before-send + an uncertain create is never resent (reconciled via webhook or an operator).
- Customer endpoints (owner-only), admin-only label/recovery endpoints, token-protected webhook with replay protection, once-only shipment emails.
- Gateway now refuses every `/internal/` path from outside (this also closes direct public access to Identity's existing internal child-account / link-as-parent endpoints).
- New tables: run `infra/sql/fulfillment_shipping.sql` (Development applies it automatically).

- Emails now link to `App:PublicBaseUrl` (default https://growgift.com.au) instead of a hardcoded example domain. Override with env `App__PublicBaseUrl` on Notifications.
- Account verification email is sent via Notifications/SendGrid at signup. Identity uses `App:WebBaseUrl` (localhost:3000 in Development, https://growgift.com.au otherwise) and `Services:NotificationsBaseUrl`. The "dev link" fallback only appears if sending fails.
- Payment confirmation no longer depends only on the Stripe webhook: the confirmation page calls `POST /api/v1/orders/sync-payment`, which asks Stripe directly. If Stripe says unpaid, the page says so; unpaid orders show "Payment not completed" in My profile.
- Signup no longer signs the user in; they sign in, then continue (e.g. to /cart).
- Header menu refreshes on login/logout (fixes missing My family / My profile). Child logins see only "My profile & portfolio" and are redirected to /kid from any other page.

## Redeem / auth / child-login update
- Root cause of "login/sign-up lands on the broker application": the login page's "Create an account" link dropped the return path, and signup then sent every recipient to `/kyc` (the broker application). Nothing remembered the gift being redeemed.
- New `lib/redirects.ts` (validated internal return paths + one precedence policy), `lib/redemption.ts` (pending gift code in sessionStorage, 30-min expiry, cleared on sign-out/redeem), `lib/navigation.ts` (menus per role).
- Redeem now resumes at `/redeem?resume=1` after login/sign-up/identity check and goes straight to the family step; the gift code is no longer kept in the URL.
- Child login: link on home page and in the menu (mobile menu added); signed-in children are sent to `/kid`; the child menu reads "My Portfolio".
- Server-side: child tokens can no longer call `/me/become-parent` or Gifting buyer/redeem endpoints; `POST /children` returns the existing child for the same name + date of birth instead of creating a duplicate.
- Tests: `npm test` (unit, plain Node) and `scripts/e2e-redeem.mjs` (browser journeys against a mocked API; needs `npm i --no-save playwright-core`).

## Gift status, purchase confirmation, Sell eligibility
- My Profile → "Gifts you've given": friendly status, next step, date, box, order reference, delivery progress for physical gifts; the redemption code is hidden until "Show redemption code" is clicked (`frontend/lib/giftStatus.ts`).
- Confirmation page wording follows the server-reported order status; adds "View in My Profile".
- Sell button: after an existing ViewTrade account is linked, My Profile now waits for the refreshed token and re-checks trading eligibility (previously it kept the stale "finish identity verification" answer).

## ViewTrade form 175 and two-step background check
- `api/applications/create` now sends `form_id` (default 175; override with `VIEWTRADE_FORM_ID`). The key name is taken from the `form_id` field in ViewTrade's responses and is not yet confirmed as the request field name.
- After the application is submitted, Brokerage calls `POST api/applications/background-check` (`{application_id}`). If the reply carries `global_id_status: DOCUMENTATION_REQUIRED`, it then calls the verify-documents endpoint. A failed check never undoes the submission; `POST /api/v1/onboarding/verify` re-runs it.
- Both endpoints follow ViewTrade's documentation: background-check sends `application_id` and `ip_address`; verify-documents (`api/applications/verify-documents`) sends `application_id`, `applicant_type: "primary"` and `ip_address`. The IP is the applicant's (first `X-Forwarded-For` entry, else the socket address) and is never logged. The check outcome is read from the latest `background_checks` entry of the reply, falling back to a top-level `status`.
- **Unconfirmed:** the documentation says to "request with applicants.background_checks" to get the check status in the reply; how that is requested (query parameter or body field) is not stated, so it is not sent yet. If the reply lacks the check status, the stage shows as "unknown" and nothing is assumed.
- UAT: set the document-verification result by hand in the Veriff dashboard, then call `POST /api/v1/onboarding/verify` or just let the application list reflect the new status.

## Subscription (3 months free, then monthly)
**What it does.** For parents and recipients, adding a card is part of REGISTRATION: after they sign in for the first time they are taken to `/subscribe` (no skip) and must add a card that stays valid for at least 6 months. Nothing is charged then; Stripe's trial ends exactly 3 months after that person's registration time, so each person's first payment is on their own day 90, then monthly at the price in Stripe. Buyers and child logins never need a subscription. A new parent/recipient with no live subscription (no card yet, card rejected, or subscription ended) is blocked from **new gift purchases** and **new family features** (add child, add partner, create chore; HTTP 402 `subscription.required`). Signing in, redeeming, viewing, selling and approving existing chores are never blocked. (The card can only be taken after the account exists, because Stripe needs a customer; so it is the first step after the first sign-in, not before the account is created.)

**Accounts that registered before billing is switched on** have no card. They get 3 months from the switch-on date, are invited to add a card (skippable, once per browser session), and are blocked only after that period if they still have no live subscription.

**Where it lives.** Identity service, `Billing/` (rules in `SubscriptionPolicy`/`BillingSync`, Stripe in `StripeBilling`, API in `BillingEndpoints`). Identity alone decides and writes `billing_ok` and `subscription` claims into the token; Gifting and Family only obey `billing_ok=false` (`SharedKernel/Billing/BillingGate`). A token without the claim is never blocked. Gateway routes `/api/v1/billing/*` to Identity.

**Rules (all tested).**
- One free trial per account, ever. Cancelling and re-subscribing, or a rejected card, does not earn another.
- Card must stay valid for at least 6 months (`BILLING_MIN_CARD_MONTHS`). Checked on the server after Checkout from the card's expiry month/year only; a shorter-lived card cancels the new subscription and the user is told to use another. Stripe Checkout cannot enforce this itself, so a bad card is accepted by Stripe and then cancelled by us before any charge. If the expiry cannot be read, the subscription is kept and flagged for a re-check, not silently passed.
- `past_due` keeps access while Stripe retries payment; `canceled`, `unpaid`, `incomplete*`, `paused`, `card_rejected` do not.
- Price is never hard-coded: it is the Stripe Price in `STRIPE_PRICE_ID`. To change the price, create a new Price in Stripe, update the variable, restart Identity (existing subscribers stay on their old price until you move them in Stripe).
- Webhook is signature-verified, replay-safe (`billing_events` primary key = Stripe event id), and rebuilds state by re-reading the subscription from Stripe, so out-of-order events cannot regress it.

**Switches (environment variables on Identity).**
| Variable | Meaning |
|---|---|
| `STRIPE_SECRET_KEY` | Same key Gifting uses. A `sk_live_` key is refused unless the host environment is Production. |
| `STRIPE_PRICE_ID` | The recurring AUD monthly Price (e.g. A$3.00). |
| `STRIPE_BILLING_WEBHOOK_SECRET` | Signing secret of the billing webhook endpoint (separate from Gifting's). |
| `BILLING_ENFORCE_FROM_UTC` | **Unset = nobody is ever blocked and no prompts appear.** Set e.g. `2026-12-01T00:00:00Z` to turn billing on. Accounts registered from that moment must add a card at registration; accounts registered before it get 3 months from this date instead, so nobody is locked out on day one. A typo leaves enforcement off. |
| `BILLING_TRIAL_MONTHS` | Months from registration to the first payment. Default 3. |
| `BILLING_MIN_CARD_MONTHS` | Default 6. |

**Stripe setup (test mode first).**
1. Product "GrowGift subscription" with a recurring monthly price in AUD; put its id in `STRIPE_PRICE_ID`.
2. Webhook endpoint `https://<your-host>/api/v1/billing/webhook` for events `checkout.session.completed`, `customer.subscription.created`, `customer.subscription.updated`, `customer.subscription.deleted`; copy its signing secret into `STRIPE_BILLING_WEBHOOK_SECRET`. Locally: `stripe listen --forward-to localhost:8080/api/v1/billing/webhook`.
3. Enable the Customer Portal (Settings → Billing → Customer portal) so "Manage subscription" works.
4. Run `infra/sql/identity_billing.sql` (new tables `identity.subscriptions`, `identity.billing_events`; the Development auto-create step skips new tables once the schema exists).

**Website.** `/subscribe` (plan, card rule, start trial, "Not now — continue"), `/subscribe/success` (asks the server to re-read Stripe, never trusts the URL), a Subscription card on My Profile (Manage subscription via Stripe's portal). Parents/recipients whose token says `billing_ok=false` are sent to `/subscribe` after sign-in, then on to where they were going.

**Not verified.** `StripeBilling.cs` and `BillingEndpoints.cs` were only syntax-checked: Stripe.net is not available in the offline build, so they have not been compiled against the SDK or run against Stripe. Rules, token gate and options are tested (29 backend tests via the offline harness; real xunit project `backend/tests/GrowGift.Identity.Tests`). Front end: 90 unit tests pass and the TypeScript check is clean. Before relying on it, build the solution, then do one full run in Stripe test mode: subscribe with a good card, with a card expiring in under 6 months (expect it to be cancelled), let a test clock pass the trial, and fail a renewal.

**Open decisions.** Price is assumed A$3.00 per month. GST treatment (price inclusive or exclusive) is a Stripe Price/Tax setting I have not configured. For new accounts, ending the subscription (for example immediate cancel in the Stripe portal) blocks the gated actions at once, because no card is on file. If someone adds a card with under 3 days of free time left, Stripe's trial is extended to 3 days (a trial can't end sooner), so they aren't charged immediately. A token is valid 15 minutes, so a just-lapsed person can still buy for up to that long.

## Sell orders now sent as dollar orders (Notional / DAY)

- Cause (hypothesis, not proven from a log): sells were sent as `MARKET`/`GTC` with a share `quantity`, often fractional. No saved Postman request shows a SELL working that way. The saved `PlaceEquitySell` (RBLX, `Notional`, `DAY`, `expectedValue` 10) returned `{"orderRef":"CC31981365","market":"U"}`.
- Change: `PlaceOrderRequest` has an optional `Notional`; when set, `ViewTradeOrderService.PlaceAsync` sends `orderType:"Notional"`, `validity:"DAY"`, `expectedValue` (no `quantity`). `SellPlanner` computes it from ViewTrade's own holdings: sell-all = market value floored to the cent; partial = quantity × value per share (falls back to last price). Under 1 cent → `trading.sell_too_small`. No price available → 503 `trading.price_unavailable`, nothing sent, idempotency key not spent.
- Tests: 187 pass in the offline harness (6 new). Not run against ViewTrade.
- Unverified: whether ViewTrade accepts a dollar value equal to the whole position (sell-all may leave sub-cent dust or be rejected if its valuation moves); whether the original error was the quantity form. If a sell is still rejected, send the Brokerage log line `ViewTrade order reply → Rejected: …`.
