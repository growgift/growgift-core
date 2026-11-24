# GrowGift Local Setup Guide

This guide runs the full GrowGift website and backend on your own computer from the `growgift-core` repository, in about 30 minutes. It is written for macOS; Linux is similar.

## 1. Before you start

- If the repository is private, add each partner under Settings, Collaborators.
- Without ViewTrade, Stripe or Shippit keys, browsing, sign-up and the gift builder work. Live payment, brokerage redemption and shipping do not.
- Never share or commit real API keys. Use your own test or sandbox keys for optional features.

## 2. Prerequisites

| Tool | Version | macOS install |
|---|---|---|
| Docker Desktop | any recent | docker.com or `brew install --cask docker` |
| .NET SDK | 9 or 10 | `brew install --cask dotnet-sdk` |
| Node.js | 22 LTS (18.18 or newer works) | `brew install node@22 && brew link --overwrite node@22` |
| Git | any recent | `brew install git` |

These ports must be free: 5432 (Postgres), 6379 (Redis), 9092 (Kafka), 5001 to 5007 (services), 8080 (gateway) and 3000 (website). Check with `lsof -i :5432 -i :6379 -i :9092`. If a native Postgres is running on 5432, stop it with `brew services stop postgresql`, otherwise the app silently talks to the wrong database.

## 3. Setup steps

### 3.1 Clone
```bash
git clone https://github.com/growgift/growgift-core.git
cd growgift-core
git checkout develop
```

### 3.2 Start Postgres, Redis and Kafka
```bash
docker-compose -p growgift -f infra/docker/docker-compose.yml up -d
docker ps
docker exec growgift_postgres_1 psql -U postgres -d growgift -c "\du"
```
Wait about 20 seconds. `docker ps` must show 3 healthy containers, and the role list must include identity_svc, catalog_svc, gifting_svc, brokerage_svc, family_svc and fulfillment_svc. If roles are missing, wipe and restart: `docker-compose -p growgift -f infra/docker/docker-compose.yml down -v`, then run `up -d` again.

### 3.3 Build the backend
```bash
cd backend
dotnet restore GrowGift.sln
dotnet build GrowGift.sln
```
The build must finish with 0 errors.

### 3.4 Create the start script
Save as `backend/run-all.sh`, then `chmod +x run-all.sh`.
```bash
#!/bin/bash
export ASPNETCORE_ENVIRONMENT=Development
export GG_CODE_PEPPER=dev-pepper-change-me
export STRIPE_WEBHOOK_SECRET=whsec_dev
mkdir -p logs
dotnet run --project src/Services/Identity/GrowGift.Identity       --urls http://localhost:5001 > logs/identity.log    2>&1 &
dotnet run --project src/Services/Catalog/GrowGift.Catalog         --urls http://localhost:5002 > logs/catalog.log     2>&1 &
dotnet run --project src/Services/Gifting/GrowGift.Gifting         --urls http://localhost:5003 > logs/gifting.log     2>&1 &
dotnet run --project src/Services/Brokerage/GrowGift.Brokerage     --urls http://localhost:5004 > logs/brokerage.log   2>&1 &
dotnet run --project src/Services/Family/GrowGift.Family           --urls http://localhost:5005 > logs/family.log      2>&1 &
dotnet run --project src/Services/Fulfillment/GrowGift.Fulfillment --urls http://localhost:5006 > logs/fulfillment.log 2>&1 &
dotnet run --project src/Gateway/GrowGift.Gateway                  --urls http://localhost:8080 > logs/gateway.log     2>&1 &
wait
```
(Notifications on port 5007 needs `SENDGRID_API_KEY`; add it only if you want e-mails.)

### 3.5 Run the backend (terminal 1)
```bash
cd backend
./run-all.sh
```
Check with `curl http://localhost:8080/healthz` and `curl http://localhost:5001/healthz`. Kafka retry warnings for the first 30 seconds are normal. If a service stops, read `backend/logs/<service>.log`.

### 3.6 Run the website (terminal 2)
```bash
cd frontend
npm install
npm run dev
```
Open http://localhost:3000. The website forwards `/api/*` to the gateway on port 8080.

### 3.7 Check it works
1. The landing page and Collections load.
2. Sign-up and sign-in work.
3. "Give a share" reaches the pay step (payment needs a Stripe test key).

## 4. Optional features

Add `export` lines to `run-all.sh` only if needed, using test or sandbox keys.

| Feature | Variables |
|---|---|
| Card payments | `STRIPE_SECRET_KEY` (test key) |
| Subscriptions | `STRIPE_PRICE_ID`, `STRIPE_BILLING_WEBHOOK_SECRET`; leave `BILLING_ENFORCE_FROM_UTC` unset to keep billing off |
| Brokerage | `VIEWTRADE_BASE_URL`, `VIEWTRADE_API_KEY`, `VIEWTRADE_API_SECRET` (sandbox only) |
| Shipping | `SHIPPIT_API_SECRET` (staging only) |
| E-mail | `SENDGRID_API_KEY` |

If the Identity log shows billing table errors, run `infra/sql/identity_billing.sql`.

## 5. Stop and reset

Stop: Ctrl+C in each terminal, `pkill -f "dotnet run"`, then `docker-compose -p growgift -f infra/docker/docker-compose.yml stop`. Use `down -v` instead of `stop` to wipe all local data.

## 6. Troubleshooting

| Symptom | Likely cause | Fix |
|---|---|---|
| A service exits immediately | Build error or missing `logs` folder | Run `dotnet build GrowGift.sln`; `mkdir -p logs`; read the log |
| `port is already allocated` | Another app owns the port | `lsof -i :<port>`, stop that app |
| `role "identity_svc" does not exist` | Roles not created | Reset with `down -v` and `up -d` |
| `password authentication failed` | Wrong environment | Set `ASPNETCORE_ENVIRONMENT=Development` |
| Website shows API errors | Backend not running | `curl http://localhost:8080/healthz` |
| Next.js asks for a newer Node | Node older than 18.18 | Install Node 22 |
