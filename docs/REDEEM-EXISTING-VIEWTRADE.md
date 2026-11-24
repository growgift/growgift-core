# Redeem: skip ViewTrade onboarding for existing, fully onboarded users

## Before
`/redeem` → `proceedToClaim()` sent **every** recipient whose token lacked `kyc_status=approved` to `/kyc` → `/onboarding`. Nothing asked ViewTrade whether they already existed (the only protection was a late 409 "user already exists" from registration).

## Now
`proceedToClaim()` (and `/kyc` itself) first calls `POST /api/v1/onboarding/resolve-existing` (Brokerage). No request body: identity comes only from the caller's JWT.

| Backend decision | Meaning | UI |
|---|---|---|
| `skip_onboarding` | exactly one ViewTrade user for the verified e-mail, active, in GrowGift's branch, with an **approved** application **and** an account number | link account locally, publish `account-opened.v1` (same event normal onboarding emits), refresh token, run the normal claim |
| `start_onboarding` | no ViewTrade user | existing onboarding (`/kyc`) |
| `continue_onboarding` | user exists, application in progress / none | existing onboarding; the ViewTrade user id is stored first so Submit reuses it instead of re-registering (which ViewTrade rejects, 4090) |
| `pending_review` | submitted, not approved (any status other than in_progress / approved / rejected) | stay on the gift: "being reviewed", retry |
| `not_eligible` | user inactive / out of branch / application rejected | stop, contact support |
| `ambiguous` | several ViewTrade users for the e-mail, or the ViewTrade user is already linked to another GrowGift user | stop, contact support; nothing chosen |
| `email_unverified` | GrowGift e-mail not verified | stop: verify e-mail first |
| HTTP 503 `onboarding.status_unavailable` | ViewTrade error / timeout / auth failure / unreadable reply | "couldn't check", retry. **Never** treated as "no account" |

The gift code stays saved (30-min sessionStorage) in every stop case. Claiming still runs the existing redeem logic, which already makes the claim idempotent per code.

## Identity changes (needed for the security rule)
- Login did **not** require a verified e-mail and the token had no such flag, so e-mail matching alone would let someone register `victim@x.com` and take over that person's ViewTrade account. Tokens now carry `email_verified`; Brokerage refuses to match without it.
- New `POST /api/v1/auth/me/refresh` re-issues the token from stored user state so newly linked account claims apply without logging in again.

## ViewTrade API facts used (all from existing code / Postman)
- Find user by e-mail: `POST api/v2/users/b2c/list` (GetUser), shared with trading via `IViewTradeUserDirectory`.
- Applications for a user: `POST api/applications/list` → `id`, `status`, `account_number` (existing `ViewTradeAnswerSubmitter`).
- **Confirmed statuses:** `in_progress`, `approved`. Anything else is treated as "submitted, not approved" — never complete. If ViewTrade documents other statuses (e.g. what "under review" is called) the mapping is one small function: `ExistingAccountEvaluator.Classify`.

## Known limits
- E-mail verification is on the GrowGift side only; ViewTrade's own e-mail verification of that user isn't visible to us.
- Only the first 10 e-mail search hits are read.
- Dev: when ViewTrade isn't reachable locally the redeem page shows a dev-only link to the existing "Simulate verification" button.
