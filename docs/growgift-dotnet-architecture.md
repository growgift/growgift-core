# GrowGift .NET Platform Architecture

*Generated from the `growgift-core` code on the `develop` branch (all branches were identical when written). This is the single architecture document; the older `architecture.md` has been merged into it and removed.*

## 1. What the platform does

GrowGift sells fractional stock shares as gifts. A buyer pays for a gift (digital, or a physical box with a single-use redemption code). The recipient redeems the code, completes identity checks and a brokerage account with ViewTrade, and the shares are bought into their account. Parents can also manage savings for children through a family tree with three jars, chores and rewards.

No shares are bought when a gift is purchased. The gift holds a dollar amount of a chosen symbol. The market purchase happens at redemption, directly into the redeemer's own ViewTrade account, so GrowGift never holds securities for anyone.

## 2. System shape

Next.js 15 (web) and an Expo skeleton (mobile) call a YARP gateway on port 8080. The gateway routes by path to services. Services never share tables; cross-service facts travel as Kafka events written through a transactional outbox.

| Service | Port | Schema | Owns |
|---|---|---|---|
| Gateway (YARP) | 8080 | none | Path routing; blocks every `/internal/` path from outside |
| Identity | 5001 | `identity` | Accounts, RS256 JWTs, e-mail verification, KYC state, shipping address, subscriptions and billing (Stripe) |
| Catalog | 5002 | `catalog` | Collections and instruments |
| Gifting | 5003 | `gifting` | Gift orders, redemption codes, redemption, Stripe payment webhook |
| Brokerage | 5004 | `brokerage` | ViewTrade onboarding, trading (buy/sell), portfolio, redemption purchases |
| Family | 5005 | `family` | Children, partners, wallets and jars, chores |
| Fulfillment | 5006 | `fulfillment` | Shipments (Shippit staging), labels, tracking webhooks |
| Notifications | 5007 | none | E-mail through SendGrid (verification, receipts, shipping) |

Supporting infrastructure: PostgreSQL 16 (one database `growgift`, one schema and one least-privilege role per service), Redis (idempotency keys, session caches), Kafka (events).

## 3. Gateway routes

| Path prefix | Service |
|---|---|
| `/api/v1/auth/*` | Identity |
| `/api/v1/billing/*` | Identity |
| `/api/v1/collections/*` | Catalog |
| `/api/v1/orders/*`, `/api/v1/gifts/*`, `/api/v1/webhooks/*` | Gifting |
| `/api/v1/children/*`, `/tasks/*`, `/partners/*`, `/family`, `/me/*` | Family |
| `/api/v1/shipments/*`, `/api/v1/shipping/*` | Fulfillment |
| `/api/v1/onboarding/*`, `/portfolio`, `/trading/*` | Brokerage |

## 3a. Clients

| Client | Repository | State |
|---|---|---|
| Website | `growgift-core/frontend` (Next.js 15, React 19, Tailwind 3) | Implemented |
| Mobile app (iOS and Android) | `growgift/growgift-mobile` (Expo, React Native) | Implemented v1, not yet tested on devices. The `mobile/` folder in this repository is an older skeleton and is not used. |

Both clients call the same gateway and the same `/api/v1` endpoints. Shared business rules live in `frontend/lib` and are copied into the mobile repository under CI drift protection (see that repository's `docs/WEB_MOBILE_SYNC.md`).

## 4. Main flows

### 4.1 Buy a gift
1. The buyer signs in (a shipping address is required for physical boxes) and creates an order: `POST /api/v1/orders` with an `Idempotency-Key`.
2. A Stripe Checkout Session is created server-side. The browser pays on Stripe's hosted page, so card data never touches GrowGift.
3. Payment is confirmed two ways: the Stripe webhook, and `POST /api/v1/orders/sync-payment` called by the confirmation page, which asks Stripe directly. Either path marks the order paid once.
4. Gifting issues a redemption code (`GG-XXXX-XXXX-XXXX`). Only a salted hash is stored. The plaintext is handed once, synchronously, to Notifications (receipt e-mail) and Fulfillment (printing/shipping), and never written to a message log.
5. For a physical box, Fulfillment creates a Shippit shipment (staging only) and follows tracking by webhook.

### 4.2 Redeem a gift
1. The recipient signs up or signs in. Redemption needs `kyc_status=approved` and a `viewtrade_account` claim in the token.
2. A user who already has a ViewTrade account is matched by verified e-mail (`POST /api/v1/onboarding/resolve-existing`) and skips onboarding. Otherwise the onboarding wizard submits the ViewTrade application.
3. `POST /api/v1/gifts/redeem` validates the code (wrong and unknown codes look identical; 10 failures lock the gift; optimistic concurrency prevents double redemption) and publishes `GiftRedeemedV1`.
4. Brokerage consumes the event and places a dollar ("Notional") BUY in the redeemer's ViewTrade account with the idempotency key `gg-gift-<giftId>`, recording a row in `redemption_purchases`. An uncertain result is stored as `unconfirmed` and is never re-sent automatically.

### 4.3 Buy and sell shares
`POST /api/v1/trading/orders` with `{side, symbol, quantity}` and an `Idempotency-Key`. Brokerage maps the user to their ViewTrade user, opens a session (access token, impersonation, Watchman token, SSO) and places the order on the Trading host. Sells are re-checked against live holdings and sent as dollar (Notional, DAY) orders. See `docs/TRADING.md`.

**Price note.** Because the purchase happens at redemption, the recipient receives whatever quantity the gifted dollars buy that day. The gift is a fixed dollar amount, not a fixed number of shares. An omnibus acquisition path (`AcquireForOmnibusAsync`, `OmnibusLot`) still exists behind the provider port but is not used in this model (see ADR-0001).

### 4.4 Family
A verified parent creates children (read-only child logins), adds a partner and creates chores. Approving a chore pays the reward into the child's wallet, split across the Short-Term (1 year), Medium-Term (3 years) and Long-Term (10 years) jars; remainders are allocated deterministically so the ledger always sums exactly.

### 4.5 Subscription
Parents and recipients add a card at registration (valid for at least 6 months). Nothing is charged for 3 months from that user's registration; then a monthly charge applies (price lives in the Stripe Price). If a subscription lapses, only new gift purchases and new family features are blocked (HTTP 402); sign-in, redeeming, viewing and selling are never blocked. See the Stripe Integration Guide.

### 4.6 Personas and identity checks

| Persona | Role value | Signup collects | Identity check (KYC) | Notes |
|---|---|---|---|---|
| Buyer | `Buyer` | Name, shipping address, e-mail, password | Not required | Never opens a brokerage account |
| Recipient | `Recipient` | E-mail, password | Required before redeeming (`kyc_status=approved` and a `viewtrade_account` claim) | Kept separate from Buyer so the role alone says who someone is |
| Parent | `Parent` | E-mail, password | Handled by the same onboarding as recipients | The `Parent` policy checks the `parent` role claim |
| Child | `Child` | Created by a parent (login code and PIN) | None | Read-only: every mutating endpoint rejects child tokens |
| Admin | `Admin` | Operator accounts | n/a | Required for shipping admin endpoints (`ShippingAdmin` policy) |

The shipping address is saved with `PUT /api/v1/auth/me/shipping-address`. The token carries a `has_shipping_address` claim, so Gifting can reject a physical-box order before payment starts without calling Identity.

## 5. Events and reliability

- **Outbox:** each of Identity, Gifting, Brokerage and Family has an `outbox_messages` table; a relay publishes to Kafka. Topics are chosen by an explicit event-domain attribute.
- **Idempotency:** Redis claims on write endpoints and on every consumed event; broker-side client references (`gg-gift-<giftId>`).
- **No blind retries on money paths:** a timed-out or unreadable order or shipment create is marked uncertain and reconciled, never resent.
- **Event names:** topics follow `growgift.<domain>.<event-name>.v<N>` and the domain comes from an explicit `[EventDomain]` attribute. See `EVENT-CATALOG.md`.
- **Audit:** an append-only, hash-chained `audit_log` per schema, protected by a trigger that rejects UPDATE and DELETE.

## 6. Security model

- JWT RS256 (15-minute access tokens); claims include role, `kyc_status`, `viewtrade_account`, `has_shipping_address`, `billing_ok`.
- Policies: Buyer, Parent, ChildReadOnly, KycApproved, Trader, ShippingAdmin. Child tokens are rejected by every mutating endpoint.
- Secrets only from environment variables (never committed or logged): `GG_JWT_PRIVATE_KEY_PEM`, `GG_CODE_PEPPER`, `STRIPE_*`, `VIEWTRADE_*`, `SHIPPIT_*`, `SENDGRID_API_KEY`, `INTERNAL_SERVICE_KEY`.
- ViewTrade tokens and session ids stay inside Brokerage. Shippit is restricted to staging by code.
- Webhooks are verified (Stripe signature; Shippit header token) and protected against replay.
- Redemption codes are stored only as peppered hashes (`GG_CODE_PEPPER`, with `GG_CODE_PEPPER_PREVIOUS` for rotation).
- Containers run as non-root with a read-only root filesystem and all Linux capabilities dropped (Kubernetes base manifests).
- Card data never touches GrowGift (Stripe-hosted pages), which keeps the card-data scope small. Confirm the exact PCI DSS scope with your payment adviser.
- See `SECURITY-AND-COMPLIANCE.md` and `DATA-PROTECTION.md` for the full picture.

## 7. Repository layout

```
backend/   GrowGift.sln, BuildingBlocks, Gateway, 7 services, tests
frontend/  Next.js 15, Tailwind 3, React 19
mobile/    Expo skeleton
infra/     docker (compose, init.sql), k8s (kustomize base), terraform (AWS skeleton), sql (manual scripts)
docs/      this documentation
.github/   CI (build, tests, image publish)
```

## 8. Implementation status

| Area | State |
|---|---|
| Gifting, Family, Identity core, Catalog, gateway | Implemented |
| ViewTrade onboarding, existing-account detection, trading, portfolio, redemption buy | Implemented against ViewTrade staging; behaviour confirmed through the Postman collection |
| Shippit shipping | Implemented, staging only; not yet exercised end to end |
| Subscription (Stripe Billing) | Implemented; Stripe adapter was written without being run against Stripe |
| Notifications | Verification and receipt e-mails implemented; other messages are scaffolds |
| Terraform, Kubernetes | Skeleton |
| Mobile | Implemented in `growgift-mobile` (v1); the `mobile/` folder here is an unused skeleton |

Open items: choose how child holdings are modelled with ViewTrade (parent account versus separate child account); confirm the sell and sell-all request shape with ViewTrade; run Stripe Billing in test mode before launch.
