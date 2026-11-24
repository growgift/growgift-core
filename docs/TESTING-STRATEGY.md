# Testing Strategy

What is tested today, how to run it, what CI actually enforces, and what to add. Counts come from the repository on `develop` (attributes and `test(` blocks) and were not re-run when this document was written, so run the commands to confirm.

## 1. What exists

| Suite | Location | Approx. tests | Covers |
|---|---|---|---|
| Domain | `backend/tests/GrowGift.Domain.Tests` | 13 | Gift orders, redemption codes, child wallets, chore tasks, money rules |
| Brokerage | `backend/tests/GrowGift.Brokerage.Tests` | 138 | ViewTrade client and mapping, trading, sells (dollar orders), portfolio, open orders, redemption onboarding, FX, background checks |
| Fulfillment | `backend/tests/GrowGift.Fulfillment.Tests` | 92 | Shippit client, shipment lifecycle, webhooks, guards and mapping, views and contracts |
| Identity | `backend/tests/GrowGift.Identity.Tests` | 29 | Billing and subscription rules |
| Website logic | `frontend/tests` | about 94 | Gift status wording, trading rules, portfolio, billing, brokerage gate, redemption, journeys, redirects, navigation |
| Mobile logic | `growgift-mobile/tests` | 72 | Same shared rules, QR parsing, log scrubbing |

## 2. How to run

```bash
# backend (all four projects)
cd backend
dotnet test GrowGift.sln -c Release

# website
cd frontend && npm test && npm run lint && npm run build

# mobile
cd growgift-mobile && npm run typecheck && npm test
```

## 3. What CI enforces today

`.github/workflows/ci.yml` runs only `GrowGift.Domain.Tests` for the backend (13 of roughly 272 backend tests) and does not run `npm test` for the website. The Brokerage, Fulfillment and Identity suites, which protect the sell, shipping, billing and redemption rules, are not enforced. **Fix this first**: change the backend step to `dotnet test backend/GrowGift.sln` and add `npm test` to the frontend job.

## 4. Test pyramid and gaps

| Level | Today | Add |
|---|---|---|
| Unit (rules, mapping, state machines) | Good coverage of money, shipping and billing rules | Keep adding a test with every bug fix |
| Integration (service plus real PostgreSQL, Redis, Kafka) | None | Containerised tests per service: outbox publish, consumer idempotency, schema roles |
| Contract (third parties) | Client tests with fakes | Recorded or sandbox contract checks for ViewTrade, Stripe and Shippit responses; update when vendors change |
| API contract between clients and gateway | None | Generate an OpenAPI description and check web and mobile against it |
| End to end | Manual walk-through | Automated browser journey on staging (sign-up, gift, redeem) and Maestro or Detox flows for the mobile app |
| Performance | None | Load test the gateway and the gift and trading endpoints before launch |
| Security | None | Dependency and secret scans in CI; authorisation tests (child and buyer tokens rejected where required); a third-party penetration test before real money |
| Accessibility | None | Automated checks plus a manual screen-reader pass on web and mobile |

## 5. Must-have regression tests (money and safety)

Keep a test for each of these and never delete one without replacing it:

- Retrying or refreshing an order, payment confirmation, shipment create or redemption does not create a second one.
- A timed-out or unreadable create is recorded as unconfirmed and is never resent automatically.
- Payment is confirmed server-side before a shipment is created.
- Digital-only gifts never create a shipment.
- Redemption does not depend on shipping.
- A replayed Stripe or Shippit webhook is ignored; a bad signature or token is rejected.
- Child tokens cannot call mutating endpoints.
- Wrong and unknown redemption codes give the same answer; 10 failures lock the gift; double redemption is impossible.
- A sell never exceeds live holdings; sells are sent as dollar (Notional, DAY) orders.
- Stripe live keys are refused outside Production; Shippit refuses any non-staging host.
- No secret, token, code or address appears in a log line or error body.

## 6. Test data and environments

- Stripe test cards (`4242 4242 4242 4242` success, `4000 0000 0000 0341` failed renewal), ViewTrade sandbox, Shippit staging.
- No real or live-market orders in any test.
- Fake people and `example.com` e-mail addresses; never production data.

## 7. Release gate

A release to `staging` needs all suites green. A release to `main` also needs the staging walk-through in `DEPLOYMENT-AND-RELEASE.md` section 8 signed off.
