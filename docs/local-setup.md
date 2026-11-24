# GrowGift — Local Development Runbook

Step-by-step guide to bring the full platform up on a development machine (written for macOS; Linux notes inline). Includes fixes for every issue encountered during first-time setup.

---

## 1. Prerequisites

| Tool | Version | Install (macOS) | Verify |
|---|---|---|---|
| Docker Desktop | any recent | https://docker.com | `docker --version` |
| .NET SDK | 9.x or 10.x | `brew install --cask dotnet-sdk` | `dotnet --version` |
| Node.js | **≥ 18.18** (22 LTS recommended) | `brew install node@22 && brew link --overwrite node@22` | `node --version` |

**Node PATH gotcha:** `node@22` is a versioned Homebrew keg. If `node`/`npm` aren't found after install:
```bash
echo 'export PATH="/usr/local/opt/node@22/bin:$PATH"' >> ~/.zshrc
source ~/.zshrc
```
If `brew link` fails with "not writable", an old .pkg-installed Node owns the paths:
```bash
sudo chown -R $(whoami) /usr/local/include/node /usr/local/lib/node_modules 2>/dev/null
brew link --overwrite node@22
```

**.NET 10 note:** the projects target `net9.0`. `backend/Directory.Build.props` sets `<RollForward>Major</RollForward>` so they run on a .NET 10 runtime without changes. Don't delete that file.

---

## 2. Clear the runway (one-time)

Port conflicts are the #1 cause of mysterious failures. The stack needs **5432** (Postgres), **6379** (Redis), **9092** (Kafka), **5001–5006** (services), **8080** (gateway), **3000** (web).

```bash
# Anything already on the infra ports?
lsof -i :5432 -i :6379 -i :9092
```

Deal with what it shows:

- **Old Docker containers** (previous stacks, other projects): `docker ps -a`, then `docker rm -f <name>` for anything mapped to these ports.
- **Native macOS Postgres** (Homebrew or Postgres.app): `brew services stop postgresql` or quit Postgres.app from the menu bar. ⚠️ A native Postgres on 127.0.0.1 can coexist invisibly with Docker's binding — the app will silently talk to the wrong server and fail with confusing role/auth errors. Make sure `lsof -i :5432` shows **only** Docker before proceeding.

---

## 3. Infrastructure (Postgres + Redis + Kafka)

From the repo root:

```bash
docker-compose -p growgift -f infra/docker/docker-compose.yml up -d
```

Always use `-p growgift` (on every compose command, including `down`) — it namespaces containers/networks/volumes as `growgift_*` and prevents collisions with other projects.

Wait ~20 seconds, then verify:

```bash
docker ps                       # 3 containers, all (healthy)
docker exec growgift_postgres_1 psql -U postgres -d growgift -c "\du"
```

The role list **must** include `identity_svc`, `catalog_svc`, `gifting_svc`, `brokerage_svc`, `family_svc`, `fulfillment_svc`. These are created by `infra/docker/init.sql` on the **first boot of a fresh volume only**.

**If the roles are missing** (volume predates the script, or init failed):

```bash
# Option A — clean reset (destroys local data):
docker-compose -p growgift -f infra/docker/docker-compose.yml down -v
docker-compose -p growgift -f infra/docker/docker-compose.yml up -d

# Option B — run the script into the live container:
docker exec -i growgift_postgres_1 psql -U postgres -d growgift < infra/docker/init.sql
```

**General DB reset** (fixes any broken database state): Option A above.

---

## 4. Backend (7 processes)

### 4.1 One-time

```bash
cd backend
dotnet restore GrowGift.sln
dotnet build GrowGift.sln        # must be green before running anything
```

### 4.2 Configuration

Dev database passwords follow the pattern `dev_<schema>` (e.g. `identity_svc` → `dev_identity`) and live in each service's `appsettings.Development.json`. They're picked up automatically when `ASPNETCORE_ENVIRONMENT=Development`. Connection strings use `Host=127.0.0.1` (not `localhost`) to avoid IPv6 resolving to a stray native Postgres.

Required environment variables:

```bash
export GG_CODE_PEPPER=dev-pepper-change-me
export STRIPE_WEBHOOK_SECRET=whsec_dev
export ASPNETCORE_ENVIRONMENT=Development
```

### 4.3 Run

Each service holds its terminal, so either use one tab per service, or the script below. Ports are fixed — the gateway routes to them:

| Service | Port | | Service | Port |
|---|---|---|---|---|
| Identity | 5001 | | Family | 5005 |
| Catalog | 5002 | | Fulfillment | 5006 |
| Gifting | 5003 | | **Gateway** | **8080** |
| Brokerage | 5004 | | | |

Single service (recommended for the first run — watch the log):
```bash
dotnet run --project src/Services/Identity/GrowGift.Identity --urls http://localhost:5001
```

All services — save as `backend/run-all.sh`, `chmod +x run-all.sh`, `./run-all.sh`:

```bash
#!/bin/bash
export GG_CODE_PEPPER=dev-pepper-change-me
export STRIPE_WEBHOOK_SECRET=whsec_dev
export ASPNETCORE_ENVIRONMENT=Development
mkdir -p logs
dotnet run --project src/Services/Identity/GrowGift.Identity       --urls http://localhost:5001 > logs/identity.log    2>&1 &
dotnet run --project src/Services/Catalog/GrowGift.Catalog         --urls http://localhost:5002 > logs/catalog.log     2>&1 &
dotnet run --project src/Services/Gifting/GrowGift.Gifting         --urls http://localhost:5003 > logs/gifting.log     2>&1 &
dotnet run --project src/Services/Brokerage/GrowGift.Brokerage     --urls http://localhost:5004 > logs/brokerage.log   2>&1 &
dotnet run --project src/Services/Family/GrowGift.Family           --urls http://localhost:5005 > logs/family.log      2>&1 &
dotnet run --project src/Services/Fulfillment/GrowGift.Fulfillment --urls http://localhost:5006 > logs/fulfillment.log 2>&1 &
dotnet run --project src/Gateway/GrowGift.Gateway                  --urls http://localhost:8080 > logs/gateway.log     2>&1 &
echo "Starting; logs in backend/logs/. Ctrl+C stops everything."
wait
```

Stop all: Ctrl+C in the script's terminal, or `pkill -f "dotnet run"`.

### 4.4 Verify

```bash
curl http://localhost:5001/healthz   # each service → Healthy
curl http://localhost:8080/healthz   # gateway
```

Kafka connection-retry warnings in Brokerage/Gifting logs during the first ~30s are normal.

---

## 5. Frontend

```bash
cd frontend
npm install
npm run dev
```

Open **http://localhost:3000**. The dev server proxies `/api/*` → gateway on 8080, so the backend stack (or at least the gateway + the service a page needs) must be running for data-driven pages (gift builder checkout, redeem).

Don't run `npm audit fix --force` — it installs breaking major versions to silence warnings.

---

## 6. Daily start / stop

```bash
# Start
docker-compose -p growgift -f infra/docker/docker-compose.yml up -d
cd backend && ./run-all.sh          # terminal 1
cd frontend && npm run dev          # terminal 2

# Stop
Ctrl+C in both terminals
docker-compose -p growgift -f infra/docker/docker-compose.yml stop   # keep data
# or `down` to remove containers; add -v to also wipe data
```

---

## 7. Troubleshooting quick reference

| Symptom | Cause | Fix |
|---|---|---|
| `(root) Additional property name is not allowed` | Compose v1 vs newer file syntax | Already fixed in repo; upgrade to Compose v2 when convenient |
| `Bind for 0.0.0.0:XXXX failed: port is already allocated` | Another container or native app owns the port | `docker ps --filter "publish=XXXX"` / `lsof -i :XXXX`, remove/stop it |
| `network ... has active endpoints` | Foreign containers attached to the network | `docker network inspect <net>` → `docker rm -f` those containers |
| `role "identity_svc" does not exist` | init.sql never ran, **or** app is talking to a different Postgres | §3 role check; if roles exist in container, check `lsof -i :5432` for a second Postgres |
| `password authentication failed` | Password mismatch vs `dev_<schema>` | Ensure `ASPNETCORE_ENVIRONMENT=Development` so `appsettings.Development.json` applies |
| `You must install or update .NET ... 9.0.0` | Only .NET 10 runtime installed | `Directory.Build.props` roll-forward handles it; keep the file |
| CS1061 `AddDbContextCheck` / `AddAspNetCoreInstrumentation` | Missing package / using | Fixed in repo; if it recurs: clean `bin`/`obj`, `dotnet restore --force` |
| Next.js: Node version required | Node < 18.18 | Install Node 22 (§1) |
| Stale/weird build behaviour | Old build artifacts | `find . -name bin -o -name obj \| xargs rm -rf` then restore + build |

---

## 8. Smoke test (end-to-end)

1. Open http://localhost:3000 → landing page renders with the ticket preview.
2. **Collections** → cards render.
3. **Give a share** → walk the 4 steps → "Pay securely" creates an order via `POST /api/v1/orders` (watch `logs/gifting.log`).
4. **Redeem a gift** → enter a code → expect the auth/KYC step (401 path) since no user is signed in — that's correct behaviour.
5. `docker exec growgift_postgres_1 psql -U postgres -d growgift -c "select count(*) from gifting.gift_orders;"` → row count reflects step 3.
