# Third-Party Integrations

One page for every outside service GrowGift depends on: what it does, which service talks to it, what happens when it fails, and where the detail lives. All calls are made by the server.

| Integration | Purpose | Service | Environment now | Key variables | Detail |
|---|---|---|---|---|---|
| **Stripe** (Checkout, Billing, portal) | Gift payments; parent and recipient subscription | Gifting, Identity | Test mode | `STRIPE_SECRET_KEY`, `STRIPE_WEBHOOK_SECRET`, `STRIPE_BILLING_WEBHOOK_SECRET`, `STRIPE_PRICE_ID` | `stripe-integration-guide.md` |
| **ViewTrade (Orbis)** | Brokerage account opening, existing-account match, buy and sell orders, portfolio | Brokerage | Sandbox / staging | `VIEWTRADE_*` | `TRADING.md`, `PORTFOLIO.md`, `REDEMPTION-BUY.md`, `REDEEM-EXISTING-VIEWTRADE.md`, `ViewTrade_Config_Driven_Question_Mapping.md` |
| **Shippit** | Shipping labels, tracking, delivery updates for gift boxes | Fulfillment | Staging only (production refused by code) | `SHIPPIT_API_SECRET`/`SHIPPIT_API_KEY`, `SHIPPIT_BASE_URL`, `SHIPPIT_WEBHOOK_TOKEN` | `SHIPPIT.md` |
| **SendGrid** | Verification, receipt and shipping e-mails | Notifications | Development key | `SENDGRID_API_KEY`, `SendGrid__FromAddress` | `AmazonSESvsSendGrid.docx` |
| **Kafka, Redis, PostgreSQL** | Events, idempotency, data | All | Local containers | connection settings | `growgift-dotnet-architecture.md` |

## Failure behaviour

| If this is down | What users see | What the system does |
|---|---|---|
| Stripe | Cannot pay or manage a card | Orders stay unpaid; `sync-payment` and webhook retries catch up when Stripe returns |
| ViewTrade | Cannot onboard, trade or see a portfolio; redemption purchase may be `unconfirmed` | Money-path calls are never blindly retried; reconcile by client reference (`OPERATIONS-RUNBOOK.md` section 4) |
| Shippit | Gift codes still work; shipment waits | Shipment retries with back-off; an unknown create outcome becomes `CreateUnconfirmed` and needs a person |
| SendGrid | E-mails delayed or missing | Check the Notifications log; confirm the failure and retry behaviour before launch |
| Kafka | Events delayed | Outbox keeps events until they can be published |
| Redis | Idempotency checks fail | Write endpoints should fail closed rather than risk a duplicate |

## Rules that apply to every integration

1. Server-side only; no vendor key, token or session id reaches a browser, app, log or Git.
2. Test mode, sandbox or staging until a recorded go-live decision.
3. Idempotency key on every money-moving call, and a stored client reference for reconciliation.
4. Verify and de-duplicate every inbound webhook.
5. Do not retry a timed-out order, shipment or payment; check the outcome first.
6. Share the minimum personal data the vendor needs (`DATA-PROTECTION.md` section 2).
7. Record any new vendor in this table with its data, environment and failure behaviour before launch.

## Pending decisions

- E-mail provider: stay on SendGrid or move to Amazon SES when hosting on AWS (comparison in `AmazonSESvsSendGrid.docx`).
- Whether to add an alternative shipping or payment provider.
- Vendor contracts, data-processing terms and data location for each vendor (see `SECURITY-AND-COMPLIANCE.md` section 4).
