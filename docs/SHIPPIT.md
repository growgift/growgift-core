# Shippit shipping (staging only)

Physical gift boxes are shipped through the **Shippit staging** API (`https://app.staging.shippit.com/api/3`).
Production is deliberately not wired up: the code refuses to start against any other host, and the shipped config
contains no secret.

> **Status of this work.** Written and unit-tested offline. **No call has been made to the real Shippit staging
> API** (this build environment can't reach it), and the Postgres/EF parts (`EfShipmentStore`, `Program.cs`) have **not
> been compiled or run** here. Sections marked ⚠️ say exactly what to check first.

## Flow

```
Stripe paid ─► Gifting GiftOrderPaidConsumer ──(Physical only, paymentConfirmed)──► Fulfillment /internal/v1/fulfillment-orders
                                                                                         │ creates ONE shipment row (unique OrderId)
                                                                                         ▼
                                                                          ShipmentWorker (DB-leased, ≤25 steps/min)
 AwaitingAddress ─► ReadyToCreate ─► CreateInFlight ─► Created ─► Labelled ─► Booked ─► InTransit ─► Delivered
   (Identity addr)   (validated)     (persisted         (POST     (GET        (POST      (webhooks;     (terminal)
                                      BEFORE send)       /orders)  …/label)    /book)     poll if silent)
                                           │ timeout/5xx/crash
                                           ▼
                                    CreateUnconfirmed ── never resent ──► webhook with our reference adopts it,
                                                                         or an operator adopts / releases it
```

* **Digital gifts never ship** – Fulfillment rejects anything but `Physical`, and Gifting only calls it from the order-paid consumer.
* **Payment first** – the shipment job exists only after the server-side paid event; `paymentConfirmed` is re-checked.
* **Redemption is decoupled** – Gift redemption and brokerage never call Shippit. A Shippit outage only delays the parcel.
* **Address** – the purchaser's saved address is read server-to-server from Identity (needs `INTERNAL_SERVICE_KEY`), snapshotted onto the shipment, validated locally (AU state/postcode), never logged, never sent to a browser.

## Duplicate prevention
| Risk | Guard |
|---|---|
| "Order paid" delivered twice / parallel | unique index on `OrderId` (`ux_shipments_order`); `CreateIfAbsent` |
| Two workers / restarts | `FOR UPDATE SKIP LOCKED` lease + optimistic `RowVersion` |
| Create timed out / 5xx / process died after send | status `CreateInFlight` is **saved before** the HTTP call; recovery → `CreateUnconfirmed`; **never auto-resent** |
| Resolving an unconfirmed create | webhook carrying our `retailer_reference` (`GG-…`) adopts the tracking number; or admin `adopt` / `confirm-not-created` |
| Shippit idempotency | **not assumed.** Only 429 (definitely not processed) is retried for creates |
| Book timed out | verified with a label read (shows `manifest_number`) before any second `book` |

## Retries & limits
Label: polls only the documented "still processing/generating/not processed" 422 codes, exponential backoff (30 s → 30 min), 12 tries then `NeedsAttention`. Book: 5 tries. Shippit's limit is 60 req/60 s per key; the worker caps itself at 25 steps/min. Errors are stored as short codes only (`rejected_422`, `transport.timeout` …) – never a response body.

## Webhooks (primary tracking path)
`POST /api/v1/shipping/webhooks/shippit` (public route, **header-token auth**: `X-GrowGift-Webhook-Token`, constant-time compare; refuses everything if no token configured). Shippit documents custom headers but no request signing, so the token is the authentication. Replays are harmless (unique `(ShipmentId, EventKey)`), out-of-order events never move a shipment backwards, terminal states are final, unknown shipments are acknowledged (200) but ignored, bodies > 64 KB rejected. Registered automatically on startup (staging) when `SHIPPIT_WEBHOOK_URL` (public https) and the token are set; delivery address/receiver details are hidden in the payload. Fallback polling (`GET …/tracking`) only runs for shipments with no event for 6 h.

## Access control
* Customer: `GET /api/v1/shipments`, `GET /api/v1/shipments/{orderId}` – JWT, **owner only** (someone else's order = 404). Shows status, courier, tracking link, suburb/state/postcode – no street address, label or internal errors.
* Admin (`role=admin`): `GET /api/v1/shipping/admin/shipments/{id}`, `…/label` (fresh pre-signed URL, access is logged), `POST …/retry`, `…/adopt`, `…/confirm-not-created`. **Labels and manifests are never customer endpoints.**
* The old unauthenticated `GET /api/v1/shipments/{orderId}` was removed.
* The Gateway now returns 404 for any path containing `/internal/`; the new Identity address endpoint additionally needs `X-Internal-Key`.

## Configuration
| Setting | Where | Notes |
|---|---|---|
| `SHIPPIT_API_SECRET` (alias `SHIPPIT_API_KEY`) | env | your single Shippit staging API key; never in a file |
| `SHIPPIT_BASE_URL` | env (optional) | defaults to the staging URL; any non-staging value is refused at startup |
| `SHIPPIT_WEBHOOK_TOKEN` | env | random ≥32 chars; also sent by Shippit in the header |
| `SHIPPIT_WEBHOOK_URL` | env | e.g. `https://<public-host>/api/v1/shipping/webhooks/shippit` |
| `INTERNAL_SERVICE_KEY` | env on Identity **and** Fulfillment | same value |
| `Shippit:CourierType` | appsettings | ⚠️ `standard` is a placeholder – must be a service enabled on your staging merchant |
| `Shippit:Parcels:<BoxSku>` / `DefaultParcel` | appsettings | ⚠️ weight (kg) / L×W×D (m) not set yet |
| `Shippit:AutoBook` | appsettings | `true` = book automatically after the label |

Tables: run `infra/sql/fulfillment_shipping.sql` (Development applies it at startup).

## Tests
`backend/tests/GrowGift.Fulfillment.Tests` – 118 tests: staging guard, address validation, state mapping/ordering, intake (digital/unpaid/duplicate/parallel), full create→label→book, label retries & exhaustion, create timeout/5xx/crash never resent, webhook adoption, 429 retry, webhook token/replay/out-of-order/mismatch/oversize, once-only emails, reconciliation, HTTP client (request shape, status classification, secret never in results), customer/admin views leak nothing, SQL ⇄ entity, static contract checks. They use a **fake Shippit client and in-memory store** – they prove our logic, **not** Shippit's real behaviour or the Postgres code.

## ⚠️ Unverified until you run it against staging
1. Create-order response envelope (parsed tolerantly: `response.tracking_number` or flat). 2. Whether your staging merchant accepts `courier_type: standard`. 3. Label response shape (`qualified_url`) and that `manifest_number` appears after booking. 4. Tracking response shape. 5. Webhook payload field names (taken from the docs). 6. `EfShipmentStore` SQL and EF mapping on Postgres. 7. Sandbox shipments are **simulated** – a "delivered" state in staging does not mean a courier moved anything.
