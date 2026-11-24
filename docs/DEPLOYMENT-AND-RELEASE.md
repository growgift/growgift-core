# Deployment and Release

How code gets from a developer's machine to production, what exists today, and what must be added before real customers use GrowGift. Facts come from `.github/workflows/ci.yml`, `infra/` and the service start-up code. Hosting options are in "Hosting GrowGift on AWS".

## 1. Environments

| Environment | Branch | Purpose | Third parties | State |
|---|---|---|---|---|
| Local | any | Development on a laptop (`GrowGift-Local-Setup-Guide.md`) | Stripe test, ViewTrade sandbox, Shippit staging | Working |
| Development / shared dev | `develop` | Integration of day-to-day work | Same as local | Not provisioned |
| Staging | `staging` | Release candidate, partner demos, full test pass | Stripe test, ViewTrade staging, Shippit staging | Not provisioned |
| Production | `main` | Real customers | Live keys, deliberately enabled | Not provisioned |

Third-party rule: until a deliberate go-live decision is recorded, no environment uses Stripe live keys, ViewTrade production or Shippit production. The code enforces part of this (a live Stripe key is refused outside Production; Shippit refuses any host except staging).

## 2. Branches and promotion

`local` to `develop` to `staging` to `main`, promoted one way. Staging and main change by pull request only. Full rules: `GIT_WORKFLOW.md`.

## 3. Continuous integration today

`.github/workflows/ci.yml`

| Job | Runs on | What it does |
|---|---|---|
| backend | push to `main`, any pull request | Restore, build the solution in Release, run `GrowGift.Domain.Tests` only |
| frontend | same | `npm ci`, lint, `npm run build` |
| images | push to `main` only | Builds one Docker image per service (gateway plus seven services) and pushes `ghcr.io/growgift/<service>:<git sha>` |

Gaps to close (in priority order):

1. Run every test project: `GrowGift.Brokerage.Tests`, `GrowGift.Fulfillment.Tests`, `GrowGift.Identity.Tests` and the front-end `npm test`. These cover the sell, trading, billing, shipping and redemption rules.
2. Trigger CI on pushes to `develop` and `staging` as well as `main`.
3. Add dependency and secret scanning (see `SECURITY-AND-COMPLIANCE.md`).
4. Scan built images for vulnerabilities before publishing.
5. Add a deploy job per environment (section 4).

## 4. Deployment

What exists: Dockerfiles for every service; Kubernetes base manifests (`infra/k8s/base`: deployments with 2 replicas, probes, resource limits, non-root and read-only containers, ingress with cert-manager, HPAs for the gateway and Gifting); a Terraform skeleton for a network, EKS, RDS, MSK and Redis. There are no per-environment overlays and no deploy workflow.

What is missing before a real environment:

| Item | Needed |
|---|---|
| Hosting decision | Choose from "Hosting GrowGift on AWS" (a lower-cost container option is described there). The k8s and Terraform files assume EKS, RDS and MSK and cost more |
| Per-environment config | Kustomize overlays (or equivalent) for staging and production: hostnames, replica counts, non-secret settings |
| Secrets | A managed secrets store, injected as environment variables (list in section 7) |
| Deploy job | Pull the image tagged with the commit sha and roll it out; require approval for production |
| Website hosting | The Next.js site is not in the Kubernetes manifests. Decide where it runs |
| DNS and TLS | Domain, certificates, gateway and website hostnames |

## 5. Database changes (blocker)

Services create tables automatically only in Development (`CreateTables()` when `ASPNETCORE_ENVIRONMENT=Development`). The start-up comments say production applies migrations in the deploy pipeline, but **the repository contains no EF Core migrations and no pipeline step**. Manual SQL exists for a few changes (`infra/sql/identity_billing.sql`, `infra/sql/fulfillment_shipping.sql`).

Before staging or production:

1. Generate an initial EF Core migration for each service's context (`dotnet ef migrations add Initial`).
2. Run migrations as a separate deploy step, with a role allowed to change the schema. The service roles (`identity_svc` and the others) should not own DDL rights in production.
3. Make changes backward compatible (expand, then contract) so the previous version keeps working during a rollout and can be rolled back.
4. Take a backup first. Test the migration on a copy of staging data.
5. Keep `init.sql` for roles, schemas and the audit trigger.

## 6. Release process (proposed)

1. Feature branch from `develop`; pull request into `develop`; CI green and one reviewer.
2. When a set of changes is ready, pull request `develop` to `staging`. Deploy to staging and run the checks in section 8.
3. Record release notes in `CHANGES-latest.md` (or a `CHANGELOG.md`).
4. Pull request `staging` to `main` with approval from the founder or lead. Tag the release (`vYYYY.MM.DD` or semantic version).
5. Deploy `main` images to production in a quiet period. Watch health checks, error rate, payment and order flow for 30 minutes.
6. The mobile app follows its own store release, see the `growgift-mobile` README. Backend changes that break installed apps need the compatibility rules in that repository's `WEB_MOBILE_SYNC.md`.

### Rollback

- Application: redeploy the previous image tag. Services are stateless apart from the database.
- Database: because changes are backward compatible, roll forward when possible. Restore from backup only for data damage (see `BACKUP-AND-DISASTER-RECOVERY.md`).
- Never undo a payment, order or shipment by editing the database. Use the reconciliation steps in `OPERATIONS-RUNBOOK.md`.

## 7. Configuration and secrets matrix

All values come from the environment. None are committed.

| Variable | Service | Secret | Notes |
|---|---|---|---|
| `ASPNETCORE_ENVIRONMENT` | all | No | `Production` in prod |
| `GG_JWT_PRIVATE_KEY_PEM` | Identity | **Yes** | Must be set so tokens survive restarts; rotate with a planned key overlap |
| `GG_CODE_PEPPER`, `GG_CODE_PEPPER_PREVIOUS` | Gifting | **Yes** | Long random value in production; the previous value is used only during rotation |
| `INTERNAL_SERVICE_KEY` | Identity, Fulfillment, callers | **Yes** | Service-to-service calls |
| `STRIPE_SECRET_KEY` | Gifting, Identity | **Yes** | `sk_live_` only in Production |
| `STRIPE_WEBHOOK_SECRET` | Gifting | **Yes** | Payments endpoint |
| `STRIPE_BILLING_WEBHOOK_SECRET` | Identity | **Yes** | Billing endpoint (different secret) |
| `STRIPE_PRICE_ID`, `BILLING_ENFORCE_FROM_UTC`, `BILLING_TRIAL_MONTHS`, `BILLING_MIN_CARD_MONTHS` | Identity | No | Billing settings |
| `VIEWTRADE_*` (base URLs, `API_KEY`, `API_SECRET`, firm and branch ids, `AUTH_SCHEME`, `FORM_ID`) | Brokerage | Key and secret: **Yes** | Sandbox until go-live |
| `SHIPPIT_API_SECRET` (or `SHIPPIT_API_KEY`), `SHIPPIT_BASE_URL`, `SHIPPIT_WEBHOOK_TOKEN`, `SHIPPIT_WEBHOOK_URL` | Fulfillment | Secret and token: **Yes** | Staging only |
| `SENDGRID_API_KEY`, `SendGrid__FromAddress` | Notifications | Key: **Yes** | Service will not start without the key |
| Database connection strings and passwords | each service | **Yes** | One role per service; dev passwords (`dev_<schema>`) must never reach staging or production |

## 8. Pre-release checklist

- [ ] All CI jobs green, including every test project and front-end tests
- [ ] Migrations tested on staging data; backup taken
- [ ] No development-only features enabled (the "simulate verification" shortcut and development auto-create are Development-only)
- [ ] Staging walk-through: sign-up, gift purchase (Stripe test), code issue, redemption (ViewTrade sandbox), shipment (Shippit staging), subscription, sell order, family flows
- [ ] Webhooks reach staging and signatures verify; a replayed event is ignored
- [ ] Secrets present and correct; no test keys in production, no live keys in staging
- [ ] Health checks green: gateway and each service `/healthz`
- [ ] Release notes written; rollback image tags noted
- [ ] For go-live only: legal, licensing and privacy items in `SECURITY-AND-COMPLIANCE.md` section 4 closed, and a recorded decision to switch each third party from test to live
