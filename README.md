# GrowGift 🌱🎁

Give a share, grow a future. Fractional stock shares as physical gifts: a box arrives, a code gets scanned, real ownership lands — and grows inside a family tree until it transfers at 18.

## Quickstart (local)

```bash
# 1. Infrastructure (Postgres + Redis + Kafka)
docker compose -p growgift -f infra/docker/docker-compose.yml up -d

# 2. Backend (any service; Gifting shown)
export GG_CODE_PEPPER=dev-pepper-change-me
export STRIPE_WEBHOOK_SECRET=whsec_dev
dotnet run --project backend/src/Services/Gifting/GrowGift.Gifting

# 3. Web
cd frontend && npm install && npm run dev   # http://localhost:3000
```

The compose file boots Postgres with `infra/docker/init.sql`, which creates one least-privilege role and schema per service plus append-only audit tables. To reset a broken local DB: `docker compose -f infra/docker/docker-compose.yml down -v && docker compose -f infra/docker/docker-compose.yml up -d`.

Full step-by-step instructions, including every environment variable: `docs/GrowGift-Local-Setup-Guide.md`.

## Layout

- `backend/` — .NET 9 solution: 7 services + YARP gateway, Clean Architecture folders per service, CQRS via MediatR, transactional outbox → Kafka, xUnit domain tests
- `frontend/` — Next.js 15, Tailwind, "Evergreen & Ribbon" design system
- `mobile/` — older Expo skeleton, unused. The iOS and Android app is the `growgift-mobile` repository
- `infra/` — docker compose, kustomize base, Terraform skeleton
- `docs/` — documentation index in `docs/README.md`; architecture in `docs/growgift-dotnet-architecture.md`

## Environment secrets (never committed)

| Variable | Used by |
|---|---|
| `GG_CODE_PEPPER` (+ `GG_CODE_PEPPER_PREVIOUS` during rotation) | Gifting — redemption code hashing |
| `STRIPE_WEBHOOK_SECRET` | Gifting — webhook signature verification |
| `STRIPE_SECRET_KEY` | Gifting — creates Stripe Checkout Sessions |
| `GG_JWT_PRIVATE_KEY_PEM` | Identity — RS256 signing key; set once so tokens survive service restarts |
| `VIEWTRADE_API_KEY` | Brokerage |
| `VIEWTRADE_API_SECRET` | Brokerage — only used when `VIEWTRADE_AUTH_SCHEME=BasicKeySecret` |
| `VIEWTRADE_BASE_URL` | Brokerage — overrides `ViewTrade:BaseUrl`; set to your real sandbox URL |
| `VIEWTRADE_AUTH_SCHEME` | Brokerage — `ApiKeyHeader` (default), `Bearer`, or `BasicKeySecret`; match your ViewTrade sandbox docs |
| `PERSONA_API_KEY` | Identity |
| `SENDGRID_API_KEY`, `TWILIO_*` | Notifications |
| `SHIPPIT_API_SECRET` (or `SHIPPIT_API_KEY`) | Fulfillment — your Shippit **staging** API key, used as the bearer secret (never commit; see docs/SHIPPIT.md) |
| `SHIPPIT_BASE_URL` | Fulfillment — optional; must be `https://app.staging.shippit.com/api/3` (anything else makes the service refuse to start) |
| `SHIPPIT_WEBHOOK_TOKEN`, `SHIPPIT_WEBHOOK_URL` | Fulfillment — shared header token + public https URL for Shippit tracking webhooks |
| `INTERNAL_SERVICE_KEY` | Identity + Fulfillment — shared key for the internal shipping-address lookup |
