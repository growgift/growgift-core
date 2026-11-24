# Backup and Disaster Recovery

A plan with the decisions still needed. Nothing here is implemented yet because no hosted environment exists. Fill in the targets before go-live.

## 1. What must survive

| Asset | Why | Where |
|---|---|---|
| PostgreSQL `growgift` (all schemas) | Accounts, orders, gift codes (hashed), purchase ledger, shipments, audit log | Database server |
| Secrets (signing key, code pepper, vendor keys) | Without the code pepper, issued codes can no longer be verified; without the JWT key, tokens reset | Secrets store |
| Kafka topics and outbox rows | Events in flight | Kafka, each `outbox_messages` |
| Container images and configuration | Rebuild service | Registry (`ghcr.io/growgift/*`), Git |
| Third-party records | Stripe, ViewTrade and Shippit hold their own copies of payments, accounts and shipments | Vendors |

## 2. Targets to decide

| Measure | Question | Suggested starting point |
|---|---|---|
| RPO (data loss tolerated) | How much recent data may be lost | 5 minutes for the database (point-in-time recovery) |
| RTO (time to restore) | How long the service may be down | 4 hours at launch; tighten as revenue grows |
| Backup retention | How long to keep backups | 35 days of point-in-time recovery plus monthly copies, aligned with the retention decision in `DATA-PROTECTION.md` |
| Region | Where backups live | Same country as production, in a separate account or region |

## 3. Backup design (to implement)

- Database: managed automatic backups with point-in-time recovery, encrypted, plus a periodic logical dump kept in a separate location. Restrict who can restore or read backups; they contain identity data.
- Secrets: stored and versioned in the secrets manager; keep an offline, access-controlled copy of the code pepper and JWT key in the founder's secure vault.
- Infrastructure: everything in Git (Terraform, Kubernetes manifests) so an environment can be rebuilt.
- Kafka: topics can be rebuilt from the database outbox and source tables; do not treat Kafka as the system of record.

## 4. Recovery scenarios

| Scenario | Steps |
|---|---|
| Bad deploy | Roll back to the previous image tag (`DEPLOYMENT-AND-RELEASE.md` section 6) |
| Corrupted or deleted data | Restore to a new database from point-in-time backup, compare, then switch; do not restore over production without a copy |
| Region or host outage | Rebuild from Terraform in the recovery region, restore the latest backup, update DNS |
| Lost secret | Rotate (`OPERATIONS-RUNBOOK.md` section 10); a lost code pepper needs a planned re-issue of unredeemed codes |
| Compromised credential | Contain, rotate, review the audit log, follow the incident process |

## 5. After any restore: reconcile

Recovered data can be older than what the outside world saw. Before reopening:

1. Compare paid Stripe payments with `gifting.gift_orders`.
2. Compare ViewTrade orders (by client reference `gg-gift-<giftId>`) with `brokerage.redemption_purchases`.
3. Compare Shippit orders (by our reference) with `fulfillment.shipments`.
4. Resolve differences through the normal procedures; never create duplicates.

## 6. Test it

Run a restore drill on staging at least twice a year and after any major schema change. Record the time taken against the RTO.
