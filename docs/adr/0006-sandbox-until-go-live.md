# ADR-0006: Sandbox and staging third parties until go-live

- Status: Accepted
- Date: 2026-11-25 (recorded)

## Context
The platform touches real money, real securities and real parcels.

## Decision
Until a recorded go-live decision, use Stripe test mode, ViewTrade sandbox and Shippit staging only. The code refuses a live Stripe key outside Production and any Shippit host other than staging. No real or live-market orders during testing. Credentials are never shared in chat, tickets or Git.

## Consequences
- Local and staging are safe by default.
- Going live is a deliberate change with its own checklist (`DEPLOYMENT-AND-RELEASE.md` section 8, `SECURITY-AND-COMPLIANCE.md` section 4).
- Production Shippit support needs a code change and a new ADR.
