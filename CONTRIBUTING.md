# Contributing to GrowGift

## Branches and pull requests

`local` to `develop` to `staging` to `main`. Work on a short-lived branch from `develop`, open a pull request into `develop`, and let CI pass. `staging` and `main` change by pull request only. Details: `docs/GIT_WORKFLOW.md`.

## Before you open a pull request

1. Build and test: `dotnet test backend/GrowGift.sln`, and in `frontend`: `npm test`, `npm run lint`, `npm run build`.
2. Add or update a test for the change. Every bug fix gets a regression test.
3. If you touch a shared rule in `frontend/lib`, tell the mobile team: the `growgift-mobile` repository must run `npm run sync:pull`.
4. Update the documents in `docs/` that the change affects, and `docs/CHANGES-latest.md`.
5. If the change is a hard-to-reverse decision, add an ADR (`docs/adr`).

## Non-negotiable rules

- No secrets, tokens, customer addresses, identity data or redemption codes in code, logs, tests, screenshots, commits or error messages. Use environment variables.
- All third-party calls from the server only.
- Stripe test mode, ViewTrade sandbox and Shippit staging only. No real or live-market orders during testing.
- Every money-moving call has an idempotency key and is never blindly retried. An uncertain outcome is recorded as unconfirmed and reconciled.
- Payment confirmed server-side before any shipment. Digital-only gifts never ship. Redemption is not coupled to shipping.
- Webhooks verify a signature or token and ignore replays.
- Do not claim a change works unless you ran the tests; say what you did not test.

## Commits

Short imperative subject, a body that says why. Do not commit `.env` files, keys or generated secrets.

## Reviews

One approving review for `develop`; the founder or lead approves `staging` to `main`. Reviewers check the rules above first, then correctness.

## Reporting a security issue

See `SECURITY.md`. Do not open a public issue.
