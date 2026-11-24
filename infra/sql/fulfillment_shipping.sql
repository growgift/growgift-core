-- Shippit shipping tables for the Fulfillment service. Idempotent; safe to re-run.
-- Generated from ShippingSchema.Sql (a unit test keeps the C# entity and this SQL in step).
-- Run as a role that may create tables in the fulfillment schema, then grant fulfillment_svc access:
--   GRANT SELECT, INSERT, UPDATE ON fulfillment.shipments, fulfillment.shipment_events TO fulfillment_svc;

-- (The fulfillment schema already exists from infra/docker/init.sql.)

CREATE TABLE IF NOT EXISTS fulfillment.shipments (
    "Id"                  uuid        NOT NULL PRIMARY KEY,
    "OrderId"             uuid        NOT NULL,
    "GiftId"              uuid        NOT NULL,
    "BuyerId"             uuid        NOT NULL,
    "BuyerEmail"          text        NOT NULL,
    "RecipientName"       text        NULL,
    "BoxSku"              text        NULL,
    "Reference"           text        NOT NULL,
    "Status"              integer     NOT NULL,
    "ToName"              text        NULL,
    "ToLine1"             text        NULL,
    "ToLine2"             text        NULL,
    "ToCity"              text        NULL,
    "ToState"             text        NULL,
    "ToPostcode"          text        NULL,
    "ToCountry"           text        NULL,
    "ToPhone"             text        NULL,
    "TrackingNumber"      text        NULL,
    "CourierName"         text        NULL,
    "TrackingUrl"         text        NULL,
    "LabelUrl"            text        NULL,
    "LabelFetchedAtUtc"   timestamptz NULL,
    "ManifestNumber"      text        NULL,
    "ManifestId"          text        NULL,
    "ShippitState"        text        NULL,
    "Attempts"            integer     NOT NULL DEFAULT 0,
    "NextAttemptAtUtc"    timestamptz NOT NULL,
    "LeaseUntilUtc"       timestamptz NULL,
    "LastError"           text        NULL,
    "Alert"               text        NULL,
    "LastApiOkAtUtc"      timestamptz NULL,
    "LastEventAtUtc"      timestamptz NULL,
    "LastReconciledAtUtc" timestamptz NULL,
    "ReachedMask"         integer     NOT NULL DEFAULT 0,
    "NotifiedMask"        integer     NOT NULL DEFAULT 0,
    "RowVersion"          integer     NOT NULL DEFAULT 0,
    "CreatedAtUtc"        timestamptz NOT NULL,
    "UpdatedAtUtc"        timestamptz NOT NULL
);

-- One shipment per order, one per reference: the durable duplicate guard.
CREATE UNIQUE INDEX IF NOT EXISTS ux_shipments_order     ON fulfillment.shipments ("OrderId");
CREATE UNIQUE INDEX IF NOT EXISTS ux_shipments_reference ON fulfillment.shipments ("Reference");
CREATE UNIQUE INDEX IF NOT EXISTS ux_shipments_tracking  ON fulfillment.shipments ("TrackingNumber") WHERE "TrackingNumber" IS NOT NULL;
CREATE INDEX IF NOT EXISTS ix_shipments_buyer ON fulfillment.shipments ("BuyerId");
CREATE INDEX IF NOT EXISTS ix_shipments_due   ON fulfillment.shipments ("NextAttemptAtUtc") WHERE "Status" NOT IN (8, 9, 11);

CREATE TABLE IF NOT EXISTS fulfillment.shipment_events (
    "Id"            uuid        NOT NULL PRIMARY KEY,
    "ShipmentId"    uuid        NOT NULL REFERENCES fulfillment.shipments ("Id"),
    "EventKey"      text        NOT NULL,
    "Source"        text        NOT NULL,
    "ShippitState"  text        NOT NULL,
    "OccurredAtUtc" timestamptz NOT NULL,
    "ReceivedAtUtc" timestamptz NOT NULL,
    "Outcome"       text        NOT NULL
);

-- Replay / duplicate webhook protection.
CREATE UNIQUE INDEX IF NOT EXISTS ux_shipment_events_key ON fulfillment.shipment_events ("ShipmentId", "EventKey");
