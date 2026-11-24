# Security and Compliance

What GrowGift does today to protect money, identities and data, where the gaps are, and which compliance questions need a qualified adviser. Written from the code on `develop`. It is not legal advice and makes no claim of certification.

## 1. Security principles (standing rules)

1. All calls to Stripe, ViewTrade, Shippit and SendGrid are made by the server. The browser and the mobile app never hold a third-party key or token.
2. Secrets come only from environment variables or a secrets manager. They are never committed, logged, returned in an error, or put in front-end code.
3. Customer addresses, identity data, redemption codes and tokens never appear in logs, Git, analytics or error messages.
4. Anything that moves money or goods is safe to repeat: no duplicate order, shipment or purchase on a retry, refresh or timeout. An uncertain outcome is recorded as unconfirmed and reconciled, never blindly resent.
5. Shippit is staging-only and ViewTrade is sandbox-only until a deliberate go-live decision. No real market orders during testing.
6. Gift and brokerage redemption is not coupled to shipping. Digital-only gifts never ship.

## 2. Controls in place

| Area | Control | Where |
|---|---|---|
| Authentication | RS256 JWT, 15-minute access tokens, signing key from `GG_JWT_PRIVATE_KEY_PEM` | Identity |
| Passwords | Hashed (never stored in clear); minimum length 12 | Identity |
| Brute force | Failed-login counter and temporary lock (`FailedLoginCount`, `LockedUntilUtc`) | Identity |
| Email | Verification token stored as a hash | Identity |
| Authorisation | Policies: Buyer, Parent, ChildReadOnly, KycApproved, Trader, ShippingAdmin. Child tokens are rejected on every mutating endpoint | Each service |
| Redemption codes | CSPRNG codes; only a peppered, salted hash is stored (`GG_CODE_PEPPER`, rotation with `GG_CODE_PEPPER_PREVIOUS`); unknown and wrong codes look identical; 10 failures lock the gift; optimistic concurrency stops double redemption | Gifting |
| Money-path idempotency | `Idempotency-Key` on order and trading endpoints; Redis claims; consumer claims per event id; broker client reference `gg-gift-<giftId>` | Gifting, Brokerage |
| Webhooks | Stripe signature verification; Shippit header token; replay and duplicate protection | Gifting, Identity, Fulfillment |
| Stripe key safety | A live key (`sk_live_`) is refused outside Production | Identity billing |
| Shippit safety | Code refuses any host other than staging; secret never in a file, log or response | Fulfillment |
| Label and manifest access | Restricted to the `ShippingAdmin` policy | Fulfillment |
| Broker tokens | ViewTrade tokens and session ids stay inside Brokerage, never returned to clients or logged | Brokerage |
| Rate limiting | 100 requests per minute per user or IP, sliding window | Gateway |
| Internal endpoints | Every `/internal/` path is blocked at the gateway; service-to-service calls use `INTERNAL_SERVICE_KEY` | Gateway, Identity, Fulfillment |
| Response headers | `X-Content-Type-Options: nosniff` on gateway and services | Gateway, services |
| Database isolation | One least-privilege role per service, owning only its schema | `infra/docker/init.sql` |
| Audit | Append-only, hash-chained `audit_log` per schema; a trigger rejects UPDATE and DELETE | `init.sql` |
| Containers | Non-root user; read-only root filesystem; all capabilities dropped | Dockerfiles, k8s base |
| Transport | TLS at the ingress through cert-manager (k8s base) | `infra/k8s/base/ingress.yaml` |
| Card data | Stripe-hosted Checkout and portal; card numbers never reach GrowGift | Gifting, Identity |
| Mobile | Token only in iOS Keychain / Android Keystore; optional Face ID / fingerprint lock; screenshots blocked while a code is shown; crash reports scrubbed of tokens, e-mails and codes; HTTPS-only in release builds | `growgift-mobile` |
| Supply chain (mobile) | `npm audit` gate, CodeQL, Dependabot | `growgift-mobile` |

## 3. Gaps and recommended work

Ordered by importance. "Owner" is a suggestion.

| # | Gap | Why it matters | Suggested action | Owner |
|---|---|---|---|---|
| 0 | Applicants' identity-form answers and document references are stored as plain JSON (no application-level encryption found; disk encryption depends on the host) in `brokerage.onboarding_applications` and are not purged after submission | The most sensitive data in the system would be exposed by any database leak or over-broad access | Purge or redact after ViewTrade confirms; encrypt until then; restrict access; set retention (`DATA-PROTECTION.md` section 4) | Engineering |
| 1 | CI runs only `GrowGift.Domain.Tests` (13 tests). The Brokerage, Fulfillment and Identity projects (259 more tests) and the front-end tests are not run in CI | Money-path regressions can merge unnoticed | Run all test projects and `npm test` in CI (see `TESTING-STRATEGY.md`) | Engineering |
| 2 | No dependency or secret scanning on this repository (CodeQL, Dependabot, secret scanning, `dotnet list package --vulnerable`) | Known-vulnerable packages or a committed key go unseen | Turn on GitHub secret scanning and push protection, Dependabot and CodeQL | Engineering |
| 3 | No production secrets store defined (no External Secrets or equivalent in `infra/k8s`) | Production keys need a managed home | Choose AWS Secrets Manager or Parameter Store and wire it in | Engineering |
| 4 | No production database migration process (no EF Migrations in the repo) | Schema changes cannot be applied safely | See `DEPLOYMENT-AND-RELEASE.md` section 5 | Engineering |
| 5 | Branch protection and required reviews are not documented as enabled | Direct pushes can bypass checks | Require pull requests and passing CI on `staging` and `main` | Founder |
| 6 | MFA fields exist (`TotpSecretEncrypted`, `MfaEnabled`) but there is no documented user-facing MFA | Account takeover risk on accounts that hold investments | Confirm status; require MFA for parents and admins before go-live | Product |
| 7 | No documented penetration test or threat model review | Unknown exploitable weaknesses | Commission a test before real money; run a lightweight threat-model workshop now | Founder |
| 8 | Logging and alerting standards are not defined | Incidents are found late | See `OPERATIONS-RUNBOOK.md` section 8 | Engineering |
| 9 | The `mobile/` skeleton in this repo is unused | Dead code and old API calls confuse reviewers | Remove it once `growgift-mobile` is confirmed | Engineering |
| 10 | Postman collections may hold literal tokens | Leaked credentials | Move tokens to collection variables and rotate any that were shared | Founder |

## 4. Compliance questions for qualified advisers

GrowGift gifts shares, sends dollar amounts to a brokerage, and holds identity data. These need written advice. This list is a prompt, not an answer.

- **Financial services licensing:** whether any part of gifting, referring or onboarding to a broker needs an Australian financial services licence or an authorised-representative arrangement, and how the partnership with the broker is documented.
- **AML/CTF (AUSTRAC):** whether GrowGift has obligations, or relies entirely on the broker's KYC. Record who performs which check.
- **Privacy Act 1988 and the Australian Privacy Principles:** collection notices, consent, overseas disclosure (Stripe, SendGrid, Shippit, ViewTrade and cloud hosting may process data outside Australia), access and correction requests, and the notifiable data breach scheme. See `DATA-PROTECTION.md`.
- **Children's data:** the family feature stores data about minors. Obtain advice on parental consent and handling.
- **Consumer law:** refund, cancellation, gift expiry and subscription terms, including GST on the subscription (not configured today).
- **Payments:** confirm the card-data scope (hosted checkout normally means the simplest self-assessment) with Stripe or a QSA.
- **App stores:** Apple and Google may require their own billing for some digital goods; review before submission.
- **Terms of service, privacy policy and disclosure documents** for the website and apps.

## 5. Reporting a vulnerability

See `SECURITY.md` in the repository root.
