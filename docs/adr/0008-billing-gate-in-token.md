# ADR-0008: Subscription status travels as a token claim

- Status: Accepted
- Date: 2026-11-25 (recorded)

## Context
Gifting and Family must block some actions when a subscription lapses, without calling Identity on every request.

## Decision
Identity owns subscription state and puts `billing_ok` and a subscription summary into the 15-minute access token. Other services obey `billing_ok=false` through a shared `BillingGate`. A token without the claim is never blocked. Only new gift purchases and new family features are blocked (HTTP 402); sign-in, redeeming, viewing and selling are never blocked.

## Consequences
- No cross-service call on the hot path.
- A change reaches an open session within 15 minutes unless the client refreshes its token.
- Users are never locked out of their own money.
