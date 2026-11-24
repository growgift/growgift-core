# GrowGift Documentation

Start here. Each document has one job. If two documents disagree, the code wins and the document should be fixed.

## Start and run

| Document | Use it to |
|---|---|
| `GrowGift-Local-Setup-Guide.md` | Run the website and backend on your computer |
| `Application_StartUp_process.docx` | See the start order and how to tell each part is healthy |
| `GIT_WORKFLOW.md` | Branching and promotion rules |
| `../CONTRIBUTING.md` | Rules for changes and pull requests |

## Product and design

| Document | Use it to |
|---|---|
| `GrowGift-PRD.docx` | What the product must do and its requirement status |
| `GrowGift-Creative-Brief.docx` | Brand, audiences and tone |
| `GrowGift-High-Level-Design.docx` | The system on one page: goals, logical view |
| `growgift-dotnet-architecture.md` | Services, flows, events, security model and implementation status (the single architecture document) |
| `GrowGift API Reference.docx` | Every endpoint |
| `GrowGift Database Architecture.docx` | Schemas, tables and roles |
| `EVENT-CATALOG.md` | Kafka events, topics, producers and consumers |
| `adr/` | Why key decisions were made |

## Features and integrations

| Document | Use it to |
|---|---|
| `INTEGRATIONS.md` | All third parties in one table, with failure behaviour |
| `stripe-integration-guide.md` | Gift payments, subscriptions, set-up, test cases, go-live |
| `TRADING.md`, `PORTFOLIO.md` | Buying, selling and the portfolio at ViewTrade |
| `REDEMPTION-BUY.md`, `REDEEM-EXISTING-VIEWTRADE.md` | Redemption purchase and the existing-account shortcut |
| `ViewTrade_Config_Driven_Question_Mapping.md` | How onboarding questions map to ViewTrade |
| `SHIPPIT.md` | Shipping (staging only) |
| `AmazonSESvsSendGrid.docx` | E-mail provider comparison |

## Run it in production

| Document | Use it to |
|---|---|
| `DEPLOYMENT-AND-RELEASE.md` | Environments, CI, deployment gaps, release and rollback, secrets matrix |
| `OPERATIONS-RUNBOOK.md` | Health checks, incidents, reconciliation, secret rotation |
| `BACKUP-AND-DISASTER-RECOVERY.md` | Backup plan, recovery targets, restore drills |
| `Hosting GrowGift on AWS.docx` | Hosting options and cost |
| `TESTING-STRATEGY.md` | What is tested, what CI enforces, what to add |

## Security, privacy and compliance

| Document | Use it to |
|---|---|
| `SECURITY-AND-COMPLIANCE.md` | Controls, gaps and questions for advisers |
| `DATA-PROTECTION.md` | Personal data inventory, sharing, retention decisions, logging rules |
| `../SECURITY.md` | How to report a vulnerability |

## Change history

`CHANGES-latest.md` lists recent changes. Add an entry with every pull request that changes behaviour.

## Mobile app

The iOS and Android app lives in the `growgift/growgift-mobile` repository (its own README, `docs/LOCAL_SETUP.md` and `docs/WEB_MOBILE_SYNC.md`). The `mobile/` folder in this repository is an older skeleton and is not used.

## Still missing (needs a decision or a person)

| Item | Owner |
|---|---|
| Privacy policy, terms of service, refund and gift-expiry terms | Founder with legal adviser |
| Licensing and AML/CTF position | Founder with legal adviser |
| Penetration test report | Founder |
| Customer support playbook and escalation contacts | Founder |
| Business continuity and vendor risk register | Founder |
| Branding assets for the mobile app (icon, splash) | Design |
| An OpenAPI description generated from the gateway | Engineering |

## Keeping documents current

- Change the document in the same pull request as the code.
- Mark generated documents with the branch and date they were generated from.
- Word (`.docx`) documents are snapshots; prefer Markdown for anything that changes often so changes show up in Git history.
