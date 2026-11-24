# Operations Runbook

What to check, and what to do, when something is wrong. Applies to staging and production once they exist; the same steps work locally. Start-up order and health checks: `Application_StartUp_process.docx`. Money-path rules: never retry blindly, never edit money tables by hand.

## 1. Health and where to look

| Check | How |
|---|---|
| Gateway and each service | `GET /healthz` on the service port (gateway 8080, Identity 5001, Catalog 5002, Gifting 5003, Brokerage 5004, Family 5005, Fulfillment 5006, Notifications 5007) |
| Logs | `backend/logs/<service>.log` locally; the platform log store in hosted environments |
| Database | `psql` as an admin role. One schema per service |
| Events | Kafka topics `growgift.<domain>.<event>.v1`; outbox backlog in each service's `outbox_messages` |

Useful queries (read-only):

```sql
-- Events written but not yet published (outbox backlog); column names may differ, check the table
select count(*) from gifting.outbox_messages;

-- Redemption purchases that are not settled
select gift_id, order_state, purchased_at_utc from brokerage.redemption_purchases
where order_state not in ('filled') order by purchased_at_utc desc;

-- Shipments that need a person
select id, status, alert, attempts from fulfillment.shipments
where status in ('NeedsAttention','CreateUnconfirmed');
```

## 2. Severity guide

| Level | Examples | Response |
|---|---|---|
| SEV1 | Payments taken but gifts not issued; wrong orders placed at the broker; data exposure; site down | Act at once; stop the cause; tell the founder |
| SEV2 | One flow broken (redeem, shipping, sign-up); webhook backlog | Same day |
| SEV3 | Cosmetic or single-user issue | Next working day |

## 3. Payments and webhooks (Stripe)

| Symptom | Likely cause | Action |
|---|---|---|
| Customer paid, order still unpaid | Webhook not delivered or signature failing | The confirmation page calls `POST /orders/sync-payment`, which asks Stripe directly; ask the customer to reopen it. In Stripe, check the endpoint's recent deliveries and resend the event. Never mark an order paid by hand |
| Webhook returns 400 signature error | Secret mismatch: payments and billing use different endpoints and secrets | Compare the endpoint secret with `STRIPE_WEBHOOK_SECRET` / `STRIPE_BILLING_WEBHOOK_SECRET`; restart the service |
| Duplicate event | Stripe retries | Safe: events are stored by id and ignored on replay |
| Billing "not available" | Missing `STRIPE_SECRET_KEY` or `STRIPE_PRICE_ID` | Set and restart Identity |
| Subscriber blocked (HTTP 402) | Subscription lapsed | Expected for new purchases and new family features only. Sign-in, redeem, view and sell still work |

## 4. Brokerage (ViewTrade)

| Symptom | Likely cause | Action |
|---|---|---|
| Redemption purchase `unconfirmed` | The order request timed out or the reply was unreadable. An order may exist at ViewTrade | **Do not resend.** Look the order up at ViewTrade by the client reference `gg-gift-<giftId>`. If it exists, record its state. If it does not, a retry is allowed only after this check. In production the app does not offer a retry for `unconfirmed` |
| Trading order returns 202 / unconfirmed | Same reason | Same approach: confirm at ViewTrade first |
| Sell fails with a price error | No live price for the symbol (HTTP 503 `price_unavailable`) | Retry later; check ViewTrade market data access |
| User cannot redeem | Missing `kyc_status=approved` or `viewtrade_account` claim | Check onboarding status; a new token is issued at next sign-in (tokens last 15 minutes) |
| Onboarding stuck | ViewTrade application pending or rejected | Check `brokerage.onboarding_applications` status and `RejectionReason` |
| Existing ViewTrade customer sent through onboarding | E-mail not matched | Use the existing-account match flow (`POST /api/v1/onboarding/resolve-existing`); see `REDEEM-EXISTING-VIEWTRADE.md` |

Guard rails: no real or live-market orders during testing; sandbox credentials only.

## 5. Shipping (Shippit, staging only)

Shipment states that need a person: `NeedsAttention` and `CreateUnconfirmed`. Admin endpoints (role `ShippingAdmin`) under `/api/v1/shipping/admin`:

| Situation | Endpoint | Rule |
|---|---|---|
| A step failed and the cause is fixed | `POST /shipments/{id}/retry` | Only from `NeedsAttention` |
| Create outcome unknown (timeout or 5xx) | `POST /shipments/{id}/adopt` with the tracking number | Use when the order exists in the Shippit dashboard under our reference |
| Create outcome unknown, and Shippit has no such order | `POST /shipments/{id}/confirm-not-created` with `confirmNoOrderExistsInShippit: true` | Only after you have checked Shippit. It makes creating again safe |
| Need the label or manifest | `GET /shipments/{id}/label` | Admin only; logs who fetched it |

An unconfirmed shipment is never resent automatically; an alert is raised after 2 hours. Full detail: `SHIPPIT.md`.

## 6. Events and background work

| Symptom | Action |
|---|---|
| Code not issued after payment | Check Gifting's `GiftOrderPaidConsumer` log and Kafka reachability. The consumer is idempotent, so redelivery is safe |
| Purchase not placed after redeem | Check Brokerage's `GiftRedeemedConsumer` and `redemption_purchases` for the gift id |
| Kafka retry warnings at start-up | Normal for about 30 seconds |
| Outbox growing | The relay cannot publish. Check Kafka health and credentials |

## 7. Accounts and codes

| Situation | Action |
|---|---|
| User locked out (repeated failures) | The lock expires on its own (`LockedUntilUtc`). Do not remove the lock without identity verification |
| Gift locked after 10 wrong codes | Verify the buyer or recipient, then follow the support process; never reveal or store a plaintext code |
| Customer asks for their data or deletion | See `DATA-PROTECTION.md` section 4 |

## 8. Monitoring to put in place

Not yet configured in the repository; add before production.

| Signal | Alert when |
|---|---|
| `/healthz` per service | Down for 2 minutes |
| HTTP 5xx rate at the gateway | Above 2% for 5 minutes |
| Payment webhook failures | Any 400 or 5xx |
| `redemption_purchases` not `filled` | Older than 15 minutes |
| `shipments` in `NeedsAttention` or `CreateUnconfirmed` | Any |
| Outbox backlog | Growing for 10 minutes |
| Database | Connection use, disk, replication lag |
| Kafka | Consumer lag per group |
| Certificates | Expiry within 14 days |

Logs must follow `DATA-PROTECTION.md` section 5. OpenTelemetry is wired into Gifting; extend it to the other services.

## 9. Incident process

1. **Contain:** stop the harmful action (disable the endpoint at the gateway, pause the consumer, rotate a key).
2. **Assess:** what data or money is affected, and for how long. Use ids, not personal data, in notes.
3. **Fix and verify** in staging where possible.
4. **Notify:** the founder immediately. If personal data may have been exposed, assess whether it is an eligible data breach under the Privacy Act and notify the OAIC and affected people as required (take legal advice).
5. **Record** a short post-incident note: timeline, cause, impact, fix, follow-ups.

## 10. Rotating secrets

| Secret | Steps |
|---|---|
| Stripe keys and webhook secrets | Create the new value in Stripe, update the secrets store, restart the service, then revoke the old value |
| `GG_CODE_PEPPER` | Set the new value as `GG_CODE_PEPPER` and the old as `GG_CODE_PEPPER_PREVIOUS`; keep both until old codes are redeemed or expired |
| `GG_JWT_PRIVATE_KEY_PEM` | Plan an overlap: tokens last 15 minutes, so a change signs everyone out once. Do it in a quiet period |
| `INTERNAL_SERVICE_KEY` | Update every service that sends or checks it at the same time |
| ViewTrade, Shippit, SendGrid | Request new credentials, update the store, restart, revoke old |
| Any key seen in chat, a ticket, a screenshot or Git | Treat as leaked: rotate immediately |
