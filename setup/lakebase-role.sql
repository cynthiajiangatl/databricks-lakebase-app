-- Grants the Container Apps managed identity least-privilege access to the synced table.
--
-- Run in the Lakebase SQL Editor (or psql) against `databricks_postgres`
-- as a DATABRICKS_SUPERUSER, AFTER deploying infra/main.bicep.
--
-- Replace <client_id> with the `identityClientId` output from the deployment.
-- The role name for a service principal is its client ID, not a display name,
-- and it is case-sensitive.
--
-- Prerequisite: the managed identity must exist as a service principal in the
-- Databricks workspace with the `workspace-access` entitlement, and hold
-- CAN_USE on the Lakebase project. Workspace-admin rights are sufficient.

CREATE EXTENSION IF NOT EXISTS databricks_auth;

-- Creates an OAuth-authenticated Postgres role. No password is set: the project
-- has native Postgres login disabled, so the role only accepts Entra-derived tokens.
SELECT databricks_create_role('<client_id>', 'SERVICE_PRINCIPAL');

GRANT CONNECT ON DATABASE databricks_postgres TO "<client_id>";
GRANT USAGE ON SCHEMA dbdemos_aibi_customer_support TO "<client_id>";

-- Column-level, not table-level. `GRANT SELECT ON <table>` would expose every
-- column, including call_transcript, compliance_data_leak and lat/long, none of
-- which the dashboard reads. COUNT(*) still works: Postgres permits it with
-- SELECT on any one column.
GRANT SELECT (
    created_time,
    continental_region,
    priority,
    operational_cost,
    csat_score,
    call_sentiment_score,
    first_time_resolution
) ON dbdemos_aibi_customer_support.lb_tickets_clean TO "<client_id>";

-- If a table-wide grant was applied previously it must be removed explicitly.
-- Column grants do not override it, so it would silently keep every column readable.
REVOKE SELECT ON dbdemos_aibi_customer_support.lb_tickets_clean FROM "<client_id>";

-- Verify: only the seven dashboard columns should come back readable.
SELECT column_name,
       has_column_privilege('<client_id>',
           'dbdemos_aibi_customer_support.lb_tickets_clean', column_name, 'SELECT') AS readable
FROM information_schema.columns
WHERE table_schema = 'dbdemos_aibi_customer_support'
  AND table_name = 'lb_tickets_clean'
ORDER BY readable DESC, column_name;
