# Data Protection and Privacy

What personal data GrowGift holds, where it lives, who else receives it, and what is still undecided. Built from the entity definitions on `develop`. It describes the system, not legal compliance; see `SECURITY-AND-COMPLIANCE.md` section 4 for the questions to put to an adviser.

## 1. Data inventory

| Data | Where stored | Service / table | Sensitivity | Notes |
|---|---|---|---|---|
| E-mail, password hash, role | PostgreSQL `identity` | `users` | High | Password stored only as a hash |
| E-mail verification token | `identity` | `users` | Medium | Stored as a hash |
| Login lock counters | `identity` | `users` | Low | `FailedLoginCount`, `LockedUntilUtc` |
| Identity-check status and references | `identity` | `users` | High | `Kyc`, `PersonaInquiryRef`, `ViewTradeAccountRef`. References and status only |
| MFA secret (if enabled) | `identity` | `users` | High | `TotpSecretEncrypted` (encrypted) |
| Buyer shipping address (name, lines, city, region, postcode, country, phone) | `identity` | `users` | High | Collected for physical gifts |
| Subscription and billing state | `identity` | `subscriptions`, `billing_events` | Medium | Stripe customer and subscription ids; no card numbers |
| Gift order (buyer id and e-mail, recipient name and e-mail, greeting, symbol, amounts, fees, Stripe payment intent id) | `gifting` | `gift_orders`, `gifts` | High | Greeting text is free text from the buyer |
| Redemption code | `gifting` | `gifts` | High | Hash only; plaintext is shown once and sent once to print and e-mail |
| ViewTrade onboarding application: the applicant's form answers, multi-select answers, uploaded-document references and upload tracking, status, ViewTrade reference ids, rejection reason | `brokerage` | `onboarding_applications` (`answers_json`, `multi_answers_json`, `document_refs_json`, `viewtrade_uploaded_documents_json`) | **Very high** | Stored as JSON in the database. The code found no step that clears or encrypts these after the application is submitted or approved. See section 4 |
| Redemption purchase ledger | `brokerage` | `redemption_purchases` | High | Gift id, recipient user id, account reference, symbol, dollar amount, FX rate, broker order reference and state |
| Children (display name, date of birth, login code) | `family` | `children` | High (minors) | Parent-created; child login uses a code and PIN |
| Wallet and jar ledger, chores, partners | `family` | `child_wallets`, `jar_entries`, `chore_tasks`, `partners` | Medium | Chore photo proof may be image data |
| Shipment (recipient name, address, phone, courier, tracking, label and manifest ids) | `fulfillment` | `shipments`, `shipment_events` | High | Recipient address is personal data |
| Domain events | Kafka and `outbox_messages` | each service | Medium | Events carry ids and amounts; see `EVENT-CATALOG.md` for the few that carry an e-mail |
| Audit log | each schema | `audit_log` | Medium | Append-only; must not contain secrets or addresses |
| Application logs | Standard output, log store (not yet defined) | all | Low if rules followed | See section 5 |

## 2. Where data goes outside GrowGift

| Recipient | Data shared | Purpose | Environment today |
|---|---|---|---|
| Stripe | Buyer e-mail, amount, order metadata; card data is entered on Stripe's pages only | Payments and subscriptions | Test mode |
| ViewTrade (Orbis) | Identity and application answers, e-mail, account and order details | Brokerage account opening, buying and selling | Sandbox / staging |
| Shippit | Recipient name, address, phone, parcel details | Delivery | Staging |
| SendGrid | Recipient e-mail address and message content | Verification, receipts, shipping e-mails | Development key |
| Cloud host (to be chosen; AWS Sydney planned) | All of the above at rest | Hosting | Not yet live |

Each of these may process data outside Australia. Record the location and the contract terms for each before go-live.

## 3. Principles applied in code

- Collect only what a feature needs. Identity answers are held only so an application can be completed and retried; they should be purged once ViewTrade has them (open item, section 4).
- Send identity and address data to the party that needs it, server to server, never through the browser or mobile app beyond the user's own session.
- Mask or omit personal data in logs, errors and analytics. The mobile crash reporter scrubs tokens, e-mails and gift codes before sending.
- Children's accounts are created and controlled by a parent and are read-only.

## 4. Decisions still needed

**Priority finding: identity answers stay in the database.** The Brokerage service saves everything the applicant enters (the form answers and document references) as JSON in `brokerage.onboarding_applications` and the code found does not clear or encrypt it afterwards. This is the most sensitive data GrowGift holds. Recommended: (1) purge or redact the answers and document references as soon as ViewTrade confirms submission, keeping only status and reference ids; (2) until then encrypt the JSON columns at the application level with a managed key; (3) restrict who can read the table; (4) define the retention period; (5) keep it out of logs, backups used by developers, and non-production copies.

| Topic | Question | Suggested default to discuss |
|---|---|---|
| Retention | How long to keep orders, shipments, audit log and e-mails after completion or account closure | Financial records as required by law (commonly 7 years in Australia, confirm), other personal data deleted or anonymised on a schedule |
| Deletion and access requests | Who handles them, how fast, and how to erase data that sits in an append-only audit log | Define a process; audit entries should hold ids only so they can stay |
| Minors | Parental consent wording; what happens to child data at 18 or on parent account closure | Legal advice needed |
| Breach response | Who decides, who notifies the regulator (OAIC) and customers | See `OPERATIONS-RUNBOOK.md` section 9 |
| Region | Where production data is stored | AWS Sydney (ap-southeast-2) |
| Backups | Encryption, retention and who can restore | See `BACKUP-AND-DISASTER-RECOVERY.md` |
| Analytics and tracking | Whether any is used on the website or apps | None today; get consent if added |
| Privacy policy and collection notice | Text and where it is linked | Needed before any real customer signs up and before app-store submission |

## 5. Logging rules

Never log: passwords, tokens, API keys or secrets, redemption codes, full addresses, phone numbers, identity answers, card data, ViewTrade tokens or session ids, request bodies on money endpoints.

Log: ids (order, gift, shipment, user), states, error categories, timings. Use the shipment and order ids to find the rest in the database.

## 6. Test data

Use fake names, `example.com` e-mail addresses and Stripe test cards. Never copy production data into development or staging.
