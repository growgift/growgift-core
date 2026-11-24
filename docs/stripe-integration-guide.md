# Stripe Integration Guide

*Written from the `growgift-core` code. Test mode only until the checklist in section 10 is complete.*

GrowGift uses Stripe for two separate things: one-off payments for gifts (Gifting service) and a recurring subscription for parents and recipients (Identity service). They use different webhook endpoints and different signing secrets.

## 1. Gift payments (Gifting)

| Item | Detail |
|---|---|
| Checkout | `POST /api/v1/orders/{orderId}/checkout-session` (one order) or `POST /api/v1/orders/checkout-session` (several cart items in one session) |
| Confirmation | Stripe webhook `POST /api/v1/webhooks/stripe`, plus `POST /api/v1/orders/sync-payment`, which asks Stripe for the session when the webhook has not arrived |
| Event used | `payment_intent.succeeded` (and the checkout session lookup) |
| Secret | `STRIPE_SECRET_KEY` (API calls), `STRIPE_WEBHOOK_SECRET` (signature) |

Card details are entered on Stripe's hosted page, which keeps GrowGift in the simplest PCI scope (SAQ-A). The order moves to Paid once. A repeat webhook or a repeat `sync-payment` does nothing further.

## 2. Subscriptions (Identity)

### 2.1 Rules
- Applies to **parents and recipients**. Buyers and child logins never need a subscription.
- A card is added as part of registration (first step after the first sign-in) and must be valid for **at least 6 months**.
- Nothing is charged at that point. The Stripe trial ends **3 months after that user's registration time**, so each user's first payment is on their own day. Then the charge repeats monthly. The price is the Stripe Price in `STRIPE_PRICE_ID` (planned AUD 3.00 per month; change it in Stripe, not in code).
- One free trial per account. A rejected card does not use the trial.
- On lapse, only **new gift purchases and new family features** return HTTP 402 `subscription.required`. Signing in, redeeming, viewing, selling and approving existing chores are never blocked.
- `past_due` keeps access while Stripe retries the payment.

### 2.2 Endpoints (`/api/v1/billing`)
| Method and path | Purpose |
|---|---|
| `GET /subscription` | Status for the signed-in user (entitlement, trial end, card requirement, price) |
| `POST /checkout` | Creates a Stripe Checkout Session in subscription mode with the trial end date |
| `POST /portal` | Opens the Stripe customer portal (change card, cancel) |
| `POST /sync` | Re-reads the subscription from Stripe (fallback if the webhook is late) |
| `POST /webhook` | Stripe webhook, signature-verified, replay-protected |

### 2.3 Webhook events
`checkout.session.completed`, `customer.subscription.created`, `customer.subscription.updated`, `customer.subscription.deleted`. The handler never trusts the event body: it re-reads the subscription from Stripe, then applies the rules above. Event ids are stored in `billing_events` (primary key) so a replay is ignored.

### 2.4 How the block works
Identity decides. It puts `billing_ok` and `subscription` claims into the access token. Gifting and Family only obey `billing_ok=false` through the shared `BillingGate`. A token without the claim is never blocked. Tokens last 15 minutes, so a change can take up to that long to reach an open session unless the page refreshes the token.

## 3. Environment variables

| Variable | Service | Purpose |
|---|---|---|
| `STRIPE_SECRET_KEY` | Gifting, Identity | Stripe API key. A live key (`sk_live_`) is refused outside Production |
| `STRIPE_WEBHOOK_SECRET` | Gifting | Signing secret for the payments webhook |
| `STRIPE_BILLING_WEBHOOK_SECRET` | Identity | Signing secret for the billing webhook (a different endpoint, so a different secret) |
| `STRIPE_PRICE_ID` | Identity | The monthly Price |
| `BILLING_ENFORCE_FROM_UTC` | Identity | When set, billing is enforced from this time. Unset means billing is off |
| `BILLING_TRIAL_MONTHS` | Identity | Default 3 |
| `BILLING_MIN_CARD_MONTHS` | Identity | Default 6 |

Never commit these values or put them in the frontend.

## 4. Stripe dashboard setup (test mode)

1. Create a Product "GrowGift subscription" with a recurring monthly Price in AUD. Copy the Price id (`price_...`) into `STRIPE_PRICE_ID`.
2. Create a webhook endpoint `https://<your-domain>/api/v1/billing/webhook` for the four events in section 2.3. Copy its signing secret into `STRIPE_BILLING_WEBHOOK_SECRET`.
3. Keep the payments webhook (`/api/v1/webhooks/stripe`) and its own, different secret (`STRIPE_WEBHOOK_SECRET`).
4. Configure the customer portal: allow card update and cancellation.
5. Restart Gifting and Identity after changing any variable. Run `infra/sql/identity_billing.sql` once if the billing tables do not exist (section 9).

| Stripe object | Used for | Where the value goes |
|---|---|---|
| API secret key (`sk_test_...`) | Server calls to Stripe | `STRIPE_SECRET_KEY` (Gifting and Identity) |
| Payments webhook and its signing secret | Gift payments | `STRIPE_WEBHOOK_SECRET` (Gifting) |
| Billing webhook and its own signing secret | Subscriptions | `STRIPE_BILLING_WEBHOOK_SECRET` (Identity) |
| Monthly Price in AUD | The subscription | `STRIPE_PRICE_ID` (Identity) |
| Customer portal settings | Change card, cancel | Dashboard only |

## 5. Local testing with the Stripe CLI

1. Install: `brew install stripe/stripe-cli/stripe`, then `stripe login`.
2. Payments, in one terminal: `stripe listen --forward-to localhost:8080/api/v1/webhooks/stripe`. Use the `whsec_` it prints as `STRIPE_WEBHOOK_SECRET` for this session, then restart Gifting.
3. Billing, in a second terminal: `stripe listen --forward-to localhost:8080/api/v1/billing/webhook`. Use its `whsec_` as `STRIPE_BILLING_WEBHOOK_SECRET`, then restart Identity.
4. Pay with test card `4242 4242 4242 4242`, any future expiry, any CVC.
5. Failed renewal: card `4000 0000 0000 0341` (attaches, then fails on renewal). Use an expiry under 6 months away to check the card-expiry rule.

## 6. Test cases

| Case | Expected result |
|---|---|
| Gift paid with the success card | Order becomes Paid once; the confirmation page shows it; a code is issued |
| Webhook delayed | The confirmation page's `sync-payment` call still confirms the order |
| Parent adds a card valid for more than 6 months | Subscription created in trial; trial end equals registration plus 3 months |
| Card expiring within 6 months | Rejected; trial not used |
| Failed renewal | `past_due`, access kept; after Stripe gives up, new purchases return 402 |
| Replay the same billing event | Ignored (event id already stored) |
| Cancel in the customer portal | Access until the paid period ends; then new gift purchases and new family features return 402; sign-in, redeem and sell still work |

## 7. Troubleshooting

| Symptom | Cause | Fix |
|---|---|---|
| Webhook returns 400 "signature verification failed" | Wrong or old signing secret, or the secret belongs to the other endpoint | Copy the secret of this endpoint (or the one `stripe listen` printed) and restart the service |
| Order stays unpaid after paying | Webhook not delivered | Check the CLI is running; the confirmation page's sync should still confirm it |
| Billing returns "not available" | `STRIPE_SECRET_KEY` or `STRIPE_PRICE_ID` missing | Set them and restart Identity |
| Service refuses to start with a live key | An `sk_live_` key outside Production | Use a test key locally |

## 8. Known limits
- GST is not configured on the Price.
- The minimum lead time between registration and trial end (3 days) is an assumption about Stripe's limits and should be verified.
- Accounts registered before billing is switched on get 3 months from the switch-on date.

## 9. Run `infra/sql/identity_billing.sql`
New tables (`subscriptions`, `billing_events`) are not created by development auto-create on an existing database. Run the script once.

## 10. Before going live
Test mode run of every item in section 6; real webhook endpoint with its own secret; live key set only in Production; price and GST reviewed; terms and refund wording agreed.
