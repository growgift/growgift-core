# Architecture Decision Records

Short records of decisions that shape GrowGift, so nobody has to rediscover why. They were written on 2026-11-25 from the existing code and design documents; the date is when they were recorded, not when each decision was made.

Add a record when a decision is hard to reverse or affects several services. Copy `0000-template.md`, number it, and never edit an accepted record: write a new one that supersedes it.

| ADR | Decision | Status |
|---|---|---|
| [0001](0001-buy-at-redemption.md) | Buy shares at redemption, not at purchase | Accepted |
| [0002](0002-schema-per-service.md) | One database, one schema and role per service | Accepted |
| [0003](0003-outbox-and-idempotent-consumers.md) | Transactional outbox and idempotent consumers | Accepted |
| [0004](0004-stripe-hosted-checkout.md) | Stripe-hosted Checkout and portal | Accepted |
| [0005](0005-never-blindly-retry-money-calls.md) | Never blindly retry money or goods calls | Accepted |
| [0006](0006-sandbox-until-go-live.md) | Sandbox and staging third parties until go-live | Accepted |
| [0007](0007-sell-as-dollar-orders.md) | Send sells to ViewTrade as dollar (Notional, DAY) orders | Accepted |
| [0008](0008-billing-gate-in-token.md) | Subscription status travels as a token claim | Accepted |
| [0009](0009-mobile-expo-shared-logic.md) | Mobile app in Expo with shared logic copied under CI guard | Accepted |
