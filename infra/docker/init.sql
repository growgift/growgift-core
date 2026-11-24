-- GrowGift — PostgreSQL bootstrap (development).
-- Runs once, as the `postgres` superuser, via /docker-entrypoint-initdb.d.
-- Production equivalents live in Terraform + a migrations pipeline; this file
-- exists so `docker compose up` yields a working local environment.
--
-- Design:
--   * One database (growgift), one schema per bounded context.
--   * One least-privilege role per service; each role owns ONLY its schema.
--     No service can read another service's tables — cross-context data flows
--     over Kafka events, never via SQL joins.
--   * Append-only audit_log per schema, enforced in the database itself with
--     a trigger that rejects UPDATE and DELETE regardless of role.

\connect growgift

-- ---------------------------------------------------------------------------
-- Roles (dev passwords only; production uses IAM auth / secrets manager)
-- ---------------------------------------------------------------------------
CREATE ROLE identity_svc    LOGIN PASSWORD 'dev_identity';
CREATE ROLE catalog_svc     LOGIN PASSWORD 'dev_catalog';
CREATE ROLE gifting_svc     LOGIN PASSWORD 'dev_gifting';
CREATE ROLE brokerage_svc   LOGIN PASSWORD 'dev_brokerage';
CREATE ROLE family_svc      LOGIN PASSWORD 'dev_family';
CREATE ROLE fulfillment_svc LOGIN PASSWORD 'dev_fulfillment';

-- ---------------------------------------------------------------------------
-- Schemas — each owned by its service role (ownership grants DDL for EF Core)
-- ---------------------------------------------------------------------------
CREATE SCHEMA "identity"  AUTHORIZATION identity_svc;
CREATE SCHEMA catalog     AUTHORIZATION catalog_svc;
CREATE SCHEMA gifting     AUTHORIZATION gifting_svc;
CREATE SCHEMA brokerage   AUTHORIZATION brokerage_svc;
CREATE SCHEMA "family"    AUTHORIZATION family_svc;
CREATE SCHEMA fulfillment AUTHORIZATION fulfillment_svc;

-- Nobody gets to lean on public.
REVOKE CREATE ON SCHEMA public FROM PUBLIC;

-- ---------------------------------------------------------------------------
-- Immutable audit log: shared trigger function + per-schema tables
-- ---------------------------------------------------------------------------
CREATE OR REPLACE FUNCTION public.reject_audit_mutation() RETURNS trigger AS $$
BEGIN
    RAISE EXCEPTION 'audit_log is append-only: % is not permitted', TG_OP
        USING ERRCODE = 'insufficient_privilege';
END;
$$ LANGUAGE plpgsql;

-- The trigger function must not be replaceable by service roles.
REVOKE ALL ON FUNCTION public.reject_audit_mutation() FROM PUBLIC;

DO $$
DECLARE s text;
BEGIN
    FOREACH s IN ARRAY ARRAY['identity','catalog','gifting','brokerage','family','fulfillment']
    LOOP
        EXECUTE format($ddl$
            CREATE TABLE %I.audit_log (
                sequence          bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                id                uuid        NOT NULL UNIQUE,
                actor             text        NOT NULL,
                action            text        NOT NULL,
                subject_type      text        NOT NULL,
                subject_id        text        NOT NULL,
                metadata_json     jsonb       NOT NULL DEFAULT '{}'::jsonb,
                ip_address        text        NULL,
                at_utc            timestamptz NOT NULL DEFAULT now(),
                previous_hash_hex text        NULL,
                hash_hex          text        NOT NULL
            )$ddl$, s);

        EXECUTE format(
            'CREATE TRIGGER audit_append_only
                 BEFORE UPDATE OR DELETE ON %I.audit_log
                 FOR EACH ROW EXECUTE FUNCTION public.reject_audit_mutation()', s);

        -- Service role may INSERT and SELECT — never UPDATE/DELETE/TRUNCATE.
        EXECUTE format('REVOKE ALL ON %I.audit_log FROM %I', s, s || '_svc');
        EXECUTE format('GRANT SELECT, INSERT ON %I.audit_log TO %I', s, s || '_svc');
        EXECUTE format('ALTER TABLE %I.audit_log OWNER TO postgres', s);
    END LOOP;
END $$;
