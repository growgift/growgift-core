# GrowGift Local Setup Guide

Run the full GrowGift website and backend on your own computer from the `growgift-core` repository, in about 30 minutes. Written for macOS; Linux is similar. It includes the fixes for every issue met during first-time setup.

---

## 1. Before you start

- If the repository is private, add each partner under Settings, Collaborators.
- Without ViewTrade, Stripe, Shippit or SendGrid keys, browsing, sign-up and the gift builder work. Live payment, brokerage redemption, shipping and e-mail do not.
- **Never share or commit real keys.** Use your own **test or sandbox** keys, and keep them only in the git-ignored file described in section 6.

---

## 2. Prerequisites

| Tool | Version | Install (macOS) | Verify |
|---|---|---|---|
| Docker Desktop | any recent | docker.com or `brew install --cask docker` | `docker --version` |
| .NET SDK | 9.x or 10.x | `brew install --cask dotnet-sdk` | `dotnet --version` |
| Node.js | 22 LTS (18.18 or newer works) | `brew install node@22 && brew link --overwrite node@22` | `node --version` |
| Git | any recent | `brew install git` | `git --version` |

**Node PATH gotcha:** `node@22` is a versioned Homebrew keg. If `node` or `npm` is not found after install:

```bash
echo 'export PATH="/usr/local/opt/node@22/bin:$PATH"' >> ~/.zshrc
source ~/.zshrc
```

If `brew link` fails with "not writable", an old .pkg-installed Node owns the paths:

```bash
sudo chown -R $(whoami) /usr/local/include/node /usr/local/lib/node_modules 2>/dev/null
brew link --overwrite node@22
```

**.NET 10 note:** the projects target `net9.0`. `backend/Directory.Build.props` sets `<RollForward>Major</RollForward>` so they run on a .NET 10 runtime unchanged. Do not delete that file.

---

## 3. Clear the runway (one-time)

Port conflicts are the most common cause of confusing failures. The stack needs:

| Port | Used by |
|---|---|
| 5432 | Postgres |
| 6379 | Redis |
| 9092 | Kafka |
| 5001 to 5007 | Backend services |
| 8080 | Gateway |
| 3000 | Website |

```bash
lsof -i :5432 -i :6379 -i :9092
```

- **Old Docker containers:** `docker ps -a`, then `docker rm -f <name>` for anything on these ports.
- **Native macOS Postgres** (Homebrew or Postgres.app): `brew services stop postgresql`, or quit Postgres.app. A native Postgres on 127.0.0.1 can coexist invisibly with Docker's binding, so the app silently talks to the wrong server and fails with confusing role or password errors. Make sure `lsof -i :5432` shows only Docker before you continue.

---

## 4. Setup steps

### 4.1 Clone

```bash
git clone https://github.com/growgift/growgift-core.git
cd growgift-core
git checkout develop
```

### 4.2 Start Postgres, Redis and Kafka

```bash
docker-compose -p growgift -f infra/docker/docker-compose.yml up -d
```

Always pass `-p growgift` on every compose command, including `down`. It names containers, networks and volumes `growgift_*` and avoids collisions with other projects.

Wait about 20 seconds, then check:

```bash
docker ps                       # 3 containers, all (healthy)
docker exec growgift_postgres_1 psql -U postgres -d growgift -c "\du"
```

The role list must include `identity_svc`, `catalog_svc`, `gifting_svc`, `brokerage_svc`, `family_svc` and `fulfillment_svc`. They are created by `infra/docker/init.sql` on the first boot of a fresh volume only.

If roles are missing:

```bash
# Option A: clean reset (destroys local data)
docker-compose -p growgift -f infra/docker/docker-compose.yml down -v
docker-compose -p growgift -f infra/docker/docker-compose.yml up -d

# Option B: run the script into the live container
docker exec -i growgift_postgres_1 psql -U postgres -d growgift < infra/docker/init.sql
```

### 4.3 Build the backend

```bash
cd backend
dotnet restore GrowGift.sln
dotnet build GrowGift.sln        # must finish with 0 errors
```

---

## 5. Backend configuration

Dev database passwords follow the pattern `dev_<schema>` (for example `identity_svc` uses `dev_identity`) and live in each service's `appsettings.Development.json`. They are used automatically when `ASPNETCORE_ENVIRONMENT=Development`. Connection strings use `Host=127.0.0.1`, not `localhost`, to avoid IPv6 resolving to a stray native Postgres.

### 5.1 Environment variables

Replace every `<...>` and `xxxx` with **your own test or sandbox value**. Never paste real keys into this document, a commit or a chat.

| Variable | Example value | Needed for | Secret? |
|---|---|---|---|
| `ASPNETCORE_ENVIRONMENT` | `Development` | Always | No |
| `GG_CODE_PEPPER` | `dev-pepper-change-me` (dev only; use a long random value outside dev) | Gift codes | Yes outside dev |
| `INTERNAL_SERVICE_KEY` | `<48+ random hex characters>` | Service-to-service calls (Identity, Fulfillment) | **Yes** |
| `STRIPE_SECRET_KEY` | `sk_test_xxxx` | Card payments | **Yes** |
| `STRIPE_WEBHOOK_SECRET` | `whsec_xxxx` | Gift payment webhooks | **Yes** |
| `STRIPE_PRICE_ID` | `price_xxxx` (your subscription price) | Subscriptions | No |
| `STRIPE_BILLING_WEBHOOK_SECRET` | `whsec_xxxx` (printed by the Stripe CLI listener) | Subscription webhooks | **Yes** |
| `VIEWTRADE_UMA_BASE_URL` | `https://user-auth-gateway-staging.orbisfn.io/uma` | Brokerage (sandbox) | No |
| `VIEWTRADE_AMO_BASE_URL` | `https://user-auth-gateway-staging.orbisfn.io/amo` | Brokerage (sandbox) | No |
| `VIEWTRADE_SSO_BASE_URL` | `https://wrh.orbisfn.net` | Brokerage (sandbox) | No |
| `VIEWTRADE_TRADING_BASE_URL` | `https://wrh.orbisfn.net` | Brokerage (sandbox) | No |
| `VIEWTRADE_API_KEY` | `<your sandbox API key>` | Brokerage | **Yes** |
| `VIEWTRADE_API_SECRET` | `<your sandbox API secret>` | Brokerage | **Yes** |
| `VIEWTRADE_FIRM_ID` | `<your firm id>` | Brokerage | Account-specific |
| `VIEWTRADE_BRANCH_ID` | `<your branch id>` | Brokerage | Account-specific |
| `SENDGRID_API_KEY` | `SG.xxxx` | E-mail (Notifications service, port 5007) | **Yes** |
| `SendGrid__FromAddress` | `no-reply@your-domain.example` | E-mail sender address | No |
| `SHIPPIT_API_KEY` | `<your staging key>` | Shipping (**staging only**). `SHIPPIT_API_SECRET` is also accepted. | **Yes** |
| `SHIPPIT_BASE_URL` | `https://app.staging.shippit.com/api/3` | Shipping (**staging only**) | No |

Notes:

- Use Shippit **staging** and ViewTrade **sandbox** only. Never use production endpoints or credentials locally.
- Leave `BILLING_ENFORCE_FROM_UTC` unset to keep billing enforcement off.
- If the Identity log shows billing table errors, run `infra/sql/identity_billing.sql`.

### 5.2 Keep secrets in one git-ignored file

Create `backend/env.local.sh`. It is listed in `.gitignore` and must never be committed.

```bash
# backend/env.local.sh  (git-ignored: your own test/sandbox values only)
export ASPNETCORE_ENVIRONMENT=Development
export GG_CODE_PEPPER=dev-pepper-change-me
export INTERNAL_SERVICE_KEY="<48+ random hex characters>"

# Stripe (test mode)
export STRIPE_SECRET_KEY="sk_test_xxxx"
export STRIPE_WEBHOOK_SECRET="whsec_xxxx"
export STRIPE_PRICE_ID="price_xxxx"
export STRIPE_BILLING_WEBHOOK_SECRET="whsec_xxxx"   # from the Stripe CLI listener

# ViewTrade (sandbox)
export VIEWTRADE_UMA_BASE_URL="https://user-auth-gateway-staging.orbisfn.io/uma"
export VIEWTRADE_AMO_BASE_URL="https://user-auth-gateway-staging.orbisfn.io/amo"
export VIEWTRADE_SSO_BASE_URL="https://wrh.orbisfn.net"
export VIEWTRADE_TRADING_BASE_URL="https://wrh.orbisfn.net"
export VIEWTRADE_API_KEY="<your sandbox API key>"
export VIEWTRADE_API_SECRET="<your sandbox API secret>"
export VIEWTRADE_FIRM_ID="<your firm id>"
export VIEWTRADE_BRANCH_ID="<your branch id>"

# E-mail
export SENDGRID_API_KEY="SG.xxxx"
export SendGrid__FromAddress="no-reply@your-domain.example"

# Shippit (staging only)
export SHIPPIT_API_KEY="<your staging key>"
export SHIPPIT_BASE_URL="https://app.staging.shippit.com/api/3"
```

Generate an internal key with `openssl rand -hex 24`.

Minimum to just browse the site: only the first three lines (`ASPNETCORE_ENVIRONMENT`, `GG_CODE_PEPPER`, `INTERNAL_SERVICE_KEY`). Add the other blocks only for the features you want to test.

---

## 6. Run the backend

Ports are fixed, because the gateway routes to them:

| Service | Port | | Service | Port |
|---|---|---|---|---|
| Identity | 5001 | | Fulfillment | 5006 |
| Catalog | 5002 | | Notifications | 5007 |
| Gifting | 5003 | | **Gateway** | **8080** |
| Brokerage | 5004 | | Family | 5005 |

For the first run, start one service and watch its log:

```bash
cd backend
source ./env.local.sh
dotnet run --project src/Services/Identity/GrowGift.Identity --urls http://localhost:5001
```

To run everything, save this as `backend/run-all.sh`, then `chmod +x run-all.sh`:

```bash
#!/bin/bash
cd "$(dirname "$0")"
if [ ! -f ./env.local.sh ]; then echo "Missing backend/env.local.sh (see the setup guide, section 5.2)"; exit 1; fi
source ./env.local.sh
mkdir -p logs
dotnet run --project src/Services/Identity/GrowGift.Identity         --urls http://localhost:5001 > logs/identity.log      2>&1 &
dotnet run --project src/Services/Catalog/GrowGift.Catalog           --urls http://localhost:5002 > logs/catalog.log       2>&1 &
dotnet run --project src/Services/Gifting/GrowGift.Gifting           --urls http://localhost:5003 > logs/gifting.log       2>&1 &
dotnet run --project src/Services/Brokerage/GrowGift.Brokerage       --urls http://localhost:5004 > logs/brokerage.log     2>&1 &
dotnet run --project src/Services/Family/GrowGift.Family             --urls http://localhost:5005 > logs/family.log        2>&1 &
dotnet run --project src/Services/Fulfillment/GrowGift.Fulfillment   --urls http://localhost:5006 > logs/fulfillment.log   2>&1 &
dotnet run --project src/Services/Notifications/GrowGift.Notifications --urls http://localhost:5007 > logs/notifications.log 2>&1 &
dotnet run --project src/Gateway/GrowGift.Gateway                    --urls http://localhost:8080 > logs/gateway.log       2>&1 &
echo "Starting; logs in backend/logs/. Ctrl+C stops everything."
wait
```

Run it (terminal 1):

```bash
cd backend
./run-all.sh
```

Verify:

```bash
curl http://localhost:8080/healthz   # gateway
curl http://localhost:5001/healthz   # each service should say Healthy
```

Kafka connection-retry warnings in the Brokerage and Gifting logs for the first 30 seconds are normal. If a service stops, read `backend/logs/<service>.log`.

---

## 7. Run the website

```bash
cd frontend
npm install
npm run dev          # terminal 2
```

Open **http://localhost:3000**. The dev server forwards `/api/*` to the gateway on port 8080, so the gateway and the services a page needs must be running. Do not run `npm audit fix --force`; it installs breaking major versions.

---

## 8. Check it works

1. The landing page renders and Collections show cards.
2. Sign-up and sign-in work.
3. "Give a share" walks the 4 steps and "Pay securely" creates an order (watch `logs/gifting.log`). Payment needs a Stripe test key.
4. "Redeem a gift" with a code asks you to sign in or complete verification when no one is signed in. That is correct.
5. Row count check:
   `docker exec growgift_postgres_1 psql -U postgres -d growgift -c "select count(*) from gifting.gift_orders;"`

---

## 9. Daily start and stop

```bash
# Start
docker-compose -p growgift -f infra/docker/docker-compose.yml up -d
cd backend && ./run-all.sh          # terminal 1
cd frontend && npm run dev          # terminal 2

# Stop
# Ctrl+C in both terminals. If any backend process is left running:
pkill -f "dotnet run"
# then:
docker-compose -p growgift -f infra/docker/docker-compose.yml stop   # keeps data
# `down` removes containers; add -v to also wipe data
```

---

## 10. Troubleshooting

| Symptom | Likely cause | Fix |
|---|---|---|
| A service exits immediately (`exit 1`) | Build error or missing `logs` folder | `dotnet build GrowGift.sln`; `mkdir -p logs`; read the log |
| `Missing backend/env.local.sh` | Config file not created | Section 5.2 |
| `(root) Additional property name is not allowed` | Compose v1 with newer file syntax | Fixed in the repo; upgrade to Compose v2 when convenient |
| `Bind for 0.0.0.0:XXXX failed: port is already allocated` | Another container or native app owns the port | `docker ps --filter "publish=XXXX"` or `lsof -i :XXXX`, then stop it |
| `network ... has active endpoints` | Foreign containers on the network | `docker network inspect <net>`, then `docker rm -f` them |
| `role "identity_svc" does not exist` | `init.sql` never ran, or the app is talking to a different Postgres | Section 4.2 role check; check `lsof -i :5432` for a second Postgres |
| `password authentication failed` | Wrong environment | Set `ASPNETCORE_ENVIRONMENT=Development` |
| `You must install or update .NET ... 9.0.0` | Only .NET 10 runtime installed | Keep `Directory.Build.props` (roll-forward) |
| CS1061 `AddDbContextCheck` or `AddAspNetCoreInstrumentation` | Missing package or using | Fixed in the repo; if it recurs, delete `bin` and `obj`, then `dotnet restore --force` |
| Stripe or ViewTrade calls fail | Key missing or wrong | Check `env.local.sh`; use test or sandbox values only |
| Website shows API errors | Backend not running | `curl http://localhost:8080/healthz` |
| Next.js asks for a newer Node | Node older than 18.18 | Install Node 22 |
| Stale or odd build behaviour | Old build output | `find . -name bin -o -name obj \| xargs rm -rf`, then restore and build |
