# ADR-0004: Stripe-hosted Checkout and portal

- Status: Accepted
- Date: 2026-11-25 (recorded)

## Context
Taking cards directly would bring GrowGift into a heavy card-data scope.

## Decision
Gifts and subscriptions are paid on Stripe-hosted pages (Checkout and customer portal), opened in the browser or an in-app browser. The server creates the session and confirms payment by signed webhook, with a `sync-payment` check as a backstop.

## Consequences
- Card numbers never touch GrowGift servers, the website code or the mobile app.
- The mobile app depends on Stripe's web pages; Apple or Google may require their own billing for some digital goods and need review before store submission.
- Two webhook endpoints with separate secrets (payments, billing).
