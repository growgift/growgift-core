# GrowGift — Platform Architecture

.NET 9 microservices rebuild of the GrowGift platform: fractional stock shares, gifted as physical boxes, redeemed with single-use codes, growing inside a family tree until ownership transfers at 18.

## System shape

Next.js web and Expo mobile clients talk to a YARP gateway, which routes to seven services. Services never share tables; every cross-context fact travels as a versioned Kafka event through a transactional outbox.

| Service | Schema | Owns |
|---|---|---|
| Identity | `identity` | Accounts, JWT RS256 issuance, TOTP MFA, Persona KYC state, role claims (`buyer`/`parent`/`child`), `kyc_status` and `viewtrade_account` claims |
| Catalog | `catalog` | Curated collections and instruments (seeded) |
| Gifting | `gifting` | Gift orders (state machine), redemption codes, redemption; Stripe webhooks |
| Brokerage | `brokerage` | `IBrokerageProvider` port (ViewTrade adapter), omnibus lots, acquisition and redemption-time transfer |
| Family | `family` | Children, three-jar wallets, chore tasks, reward splitting, transfer-at-18 |
| Fulfillment | `fulfillment` | Gift box orders, goodies, Shipbob (scaffold) |
| Notifications | — | SendGrid/Twilio fan-out (scaffold) |

## The money path (buy-at-redemption model)

No shares are purchased when a gift is bought. The gift locks in a **dollar amount of a chosen symbol**; the actual market purchase happens at redemption, directly into the recipient's fully disclosed account. This removes pre-redemption custody entirely — GrowGift never holds securities on anyone's behalf.

1. **Order** — `POST /api/v1/orders` (requires `Idempotency-Key`). `GiftOrder` state machine: `Draft → AwaitingPayment → Paid → CodeIssued → Shipped → Redeemed`, with `Expired → CodeIssued` reissue, plus `Cancelled`/`Refunded`.
2. **Payment** — Stripe webhook, signature-verified, replay-safe. Emits `GiftOrderPaidV1` via the outbox.
3. **Code issue** — Gifting consumes `GiftOrderPaidV1` and mints a CSPRNG code (`GG-XXXX-XXXX-XXXX`, Crockford-style alphabet without `0/1/O/I` confusables); only a salted SHA-256 hash is stored, plaintext is handed to Fulfillment exactly once for printing. The code is an internal claim on the order's symbol + amount — nothing exists at the broker yet.
4. **Redemption** — Recipient registers, completes KYC (Persona), gets a disclosed ViewTrade account, then enters the code (normalization folds O→0, I/L→1). Requires `kyc_status=approved` and a `viewtrade_account` claim. Unknown code and wrong code return identical errors (no enumeration); 10 failed attempts lock the gift; optimistic concurrency (`xmin`) makes double-redeem impossible. Emits `GiftRedeemedV1` carrying the symbol, amount and currency.
5. **Purchase** — Brokerage consumes `GiftRedeemedV1` (Redis idempotency claim per `EventId`) and calls `IBrokerageProvider.BuyForAccountAsync` — a market buy of the gifted notional directly into the recipient's account, with `clientRef = gg-gift-{GiftId}` as the broker-side idempotency key. Records a `RedemptionPurchase` row (the reconciliation ledger) and emits `OwnershipTransferredV1`.

Every hop is effectively-once: outbox on the write side, consumer idempotency claims on the read side, broker-side client refs at the ViewTrade boundary, and `redemption_purchases` as the reconciliation source of truth.

**Price note:** because the buy executes at redemption time, the recipient receives whatever quantity the gifted dollars purchase *that day* — the gift is a fixed notional, not a fixed share count. The omnibus acquisition path (`AcquireForOmnibusAsync`/`TransferFromOmnibusAsync`, `OmnibusLot`) remains implemented behind the provider port if a buy-at-purchase model is ever reinstated.

## Buyer shipping address

Buyers need a physical address on file before checkout — the gift box has to go somewhere. `User` (Identity) carries an optional `ShippingAddress` (owned entity: name, lines, city, region, postal code, ISO country code, phone), collected at signup for the buyer persona or added later via `PUT /api/v1/auth/me/shipping-address`. Rather than a synchronous cross-service call, `TokenService` embeds a `has_shipping_address` claim in the JWT at login — the same pattern already used for `kyc_status`/`viewtrade_account` — so Gifting's `POST /orders` can check it directly from the token and reject with an actionable error before any payment starts.

## Roles and KYC by persona

`UserRole` has four person-facing values: `Buyer`, `Recipient`, `Parent`, `Child` (plus `Admin`). Buyer and Recipient are deliberately separate — an earlier version of this system had recipients silently registering as `Buyer` too, which made "is this a buyer" unanswerable from the role alone.

| Persona | Signup collects | KYC required | Notes |
|---|---|---|---|
| Buyer | name + shipping address, email, password | **No** | Never opens a brokerage account; `Gifting`'s `Buyer` policy is `RequireAuthenticatedUser()` only |
| Recipient | email, password | **Yes** | Must complete KYC + have a disclosed broker account before `POST /gifts/redeem` (`KycApproved` policy) |
| Parent (family) | email, password | **Yes** | Manages a minor's holdings, so `Family`'s `Parent` policy now requires `kyc_status=approved` in addition to the `parent` role claim |

The web signup flow (`/signup`) reflects this split structurally: an entry screen chooses "buying a gift" (→ address form, no KYC mention anywhere) vs. "receiving a gift or investing for family" (→ a sub-choice between recipient/parent, then an email/password-only form that ends at `/kyc`). `useRequireAuth(role, { requireKyc: true })` redirects a signed-in-but-unverified parent straight to `/kyc` if they navigate to `/family` directly, rather than surfacing a raw 403 from the backend policy.

## Family module

`Child` (DOB-validated, read-only login, ownership transfer at 18) owns a `ChildWallet` with three jars — Short-Term (1 yr lock), Medium-Term (3 yrs), Long-Term (10 yrs) — backed by an immutable `JarEntry` ledger with derived balances. `ChoreTask` supports one-time and recurring chores with photo proof; only the creating parent can approve. Approval deposits the reward and spawns the next occurrence in one transaction: cents split three ways with the deterministic remainder going short → medium → long, so the ledger always sums exactly.

## Security and compliance mapping

- **Codes**: hash-only storage with a peppered salt (`GG_CODE_PEPPER`, dual-read rotation via `GG_CODE_PEPPER_PREVIOUS`); constant response shape against enumeration; attempt lockout.
- **Auth**: JWT RS256 (`aud=growgift.api`); endpoint policies (`Buyer`, `Parent`, `ChildReadOnly`, `KycApproved`). Child role is rejected by every mutating endpoint server-side.
- **Audit**: hash-chained append-only `audit_log` per schema; a Postgres trigger owned by the superuser rejects UPDATE/DELETE for every role, so immutability holds even against a compromised service credential (AUSTRAC/SOC 2 posture).
- **Isolation**: one least-privilege DB role per service, each owning only its schema; `public` schema locked down.
- **Transport/infra**: TLS at the ingress (cert-manager), non-root read-only containers, dropped capabilities, secrets via External Secrets Operator — never in git.
- **Payments**: card data never touches GrowGift (Stripe-hosted), keeping PCI DSS scope at SAQ-A.
- **Resilience**: Polly decorrelated-jitter retries + circuit breaker on the ViewTrade client; OpenTelemetry traces across gateway → service → Kafka.

## Repository layout

```
backend/   GrowGift.sln, BuildingBlocks (SharedKernel, Contracts, Infrastructure.Common),
           Gateway (YARP), 7 services, domain tests (xUnit)
frontend/  Next.js 15 App Router — landing, collections, gift builder, redeem, family, kid view
mobile/    Expo skeleton (QR scan → redeem deep link)
infra/     docker (compose + init.sql), k8s (kustomize base), terraform (module skeleton)
.github/   CI: build + domain tests + per-service image publish
```

## Implementation status — honest ledger

**Implemented in depth**: SharedKernel and contracts; outbox/relay, Redis idempotency, hash-chained audit writer; Gifting (full domain, commands, endpoints, Stripe verification); Brokerage (provider port, ViewTrade adapter, three consumers, omnibus ledger); Family (wallet/jar/chore domain with tests, approval flow, endpoints); Identity core (users, password hashing, RS256 token issuance); Catalog with seed data; docker-compose + init.sql; k8s base; CI; web frontend pages.

**Scaffolded (structure + contracts, internals pending)**: Fulfillment (Shipbob calls), Notifications (SendGrid/Twilio senders), Persona webhook handling in Identity, passkeys/WebAuthn, Terraform module internals, mobile screens, OpenSearch, fraud scoring, transfer-at-18 scheduler job.

**Not yet run**: the solution has not been compiled in this environment (no network for `dotnet restore`); expect minor fix-ups on first build.
