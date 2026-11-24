# Event Catalog

Integration events carried over Kafka through a transactional outbox. Source of truth: `backend/src/BuildingBlocks/GrowGift.Contracts/IntegrationEvents`.

## Conventions

- Topic name: `growgift.<domain>.<event-name-in-kebab-case>.v<N>`, for example `GiftOrderPaidV1` is `growgift.gifting.gift-order-paid.v1`. The domain comes from the `[EventDomain("…")]` attribute on the event, never from the namespace.
- Delivery is at least once. Producers write the event in the same database transaction as the change (outbox); a relay publishes it.
- Consumers must be idempotent. They claim each `EventId` in Redis before acting.
- Changing an event: add optional fields only. For a breaking change create `…V2` with its own topic and run both versions until every consumer has moved.
- Events never carry secrets, redemption codes, addresses or tokens.

## Events

| Event | Domain | Payload (summary) | Published by | Consumed by |
|---|---|---|---|---|
| `GiftOrderPaidV1` | gifting | Order id, buyer id, symbol, amount, currency, whether a physical box is included | Gifting (`ConfirmPayment`, after Stripe confirmation) | Gifting (`GiftOrderPaidConsumer`: issues the redemption code) |
| `RedemptionCodeIssuedV1` | gifting | Order id, gift id, expiry | Gifting (`IssueRedemptionCode`) | None found in code |
| `GiftRedeemedV1` | gifting | Gift id, recipient user id, optional beneficiary child id, symbol, dollar amount, currency, ViewTrade account reference | Gifting (`RedeemGift`) | Brokerage (`GiftRedeemedConsumer`: places the dollar BUY and records `redemption_purchases`) |
| `SharesAcquiredV1` | gifting | Order id and acquisition details | Not published in the buy-at-redemption model | None |
| `OwnershipTransferredV1` | gifting | Gift id and transfer details | Not published today | None |
| `AccountOpenedV1` | brokerage | User id, account reference, status | Brokerage (onboarding endpoints, existing-account matching) | Identity (`AccountOpenedConsumer`: updates the user's KYC status and ViewTrade account reference) |
| `UserRegisteredV1` | identity | User id, e-mail, role | Not published today | None |
| `KycCompletedV1` | identity | User id, inquiry reference, outcome | Not published today | None |
| `TaskRewardApprovedV1` | family | Task id, child id, reward details | Family (`ApproveTask`) | None found in code |
| `ChildTurned18V1` | family | Child id, parent user id | Not published today (transfer-at-18 job is not built) | None |
| `FulfillmentOrderCreatedV1`, `ShipmentDispatchedV1`, `ShipmentDeliveredV1` | fulfillment | Fulfillment order id, carrier, tracking number, delivery time | Not published today (shipping state is tracked in the Fulfillment tables) | None |

"Not published today" and "None found in code" are from a search of the codebase at the time of writing. Treat unused events as candidates to remove or implement, and re-check before relying on them.

## Money-path chain

1. `GiftOrderPaidV1` (Gifting) leads to code issue and `RedemptionCodeIssuedV1`.
2. The recipient redeems: `GiftRedeemedV1` (Gifting) leads to a ViewTrade dollar BUY in Brokerage, recorded in `brokerage.redemption_purchases` with `OrderState` (accepted, pending, filled or unconfirmed).
3. An `unconfirmed` purchase is never re-sent automatically. See `OPERATIONS-RUNBOOK.md` section 4.

## Adding an event

1. Add the record to the right `*Events.cs` file with `[EventDomain("<domain>")]` and the `V1` suffix.
2. Publish through the outbox publisher inside the same transaction as the state change.
3. Write a consumer that claims `EventId` for idempotency.
4. Add a row to this table and a test.
