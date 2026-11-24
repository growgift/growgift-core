-- Subscription tables for the Identity service. Idempotent; safe to re-run.
-- Needed because the Development auto-create step skips new tables once the schema already exists.
-- Run as a role that may create tables in the identity schema (e.g. postgres), then grant access:
--   GRANT SELECT, INSERT, UPDATE ON "identity".subscriptions, "identity".billing_events TO identity_svc;

CREATE TABLE IF NOT EXISTS "identity".subscriptions (
    "Id"                   uuid        NOT NULL PRIMARY KEY,
    "UserId"               uuid        NOT NULL,
    "StripeCustomerId"     text        NULL,
    "StripeSubscriptionId" text        NULL,
    "Status"               varchar(32) NOT NULL,
    "TrialEndsAtUtc"       timestamptz NULL,
    "CancelAtPeriodEnd"    boolean     NOT NULL DEFAULT false,
    "TrialUsed"            boolean     NOT NULL DEFAULT false,
    "CardCheckedAtUtc"     timestamptz NULL,
    "CreatedAtUtc"         timestamptz NOT NULL,
    "UpdatedAtUtc"         timestamptz NOT NULL
);

-- One subscription row per user.
CREATE UNIQUE INDEX IF NOT EXISTS "IX_subscriptions_UserId"               ON "identity".subscriptions ("UserId");
CREATE INDEX        IF NOT EXISTS "IX_subscriptions_StripeCustomerId"     ON "identity".subscriptions ("StripeCustomerId");
CREATE INDEX        IF NOT EXISTS "IX_subscriptions_StripeSubscriptionId" ON "identity".subscriptions ("StripeSubscriptionId");

-- Webhook replay / duplicate protection: the primary key is the Stripe event id.
CREATE TABLE IF NOT EXISTS "identity".billing_events (
    "EventId"       text        NOT NULL PRIMARY KEY,
    "Type"          text        NOT NULL,
    "ReceivedAtUtc" timestamptz NOT NULL
);
