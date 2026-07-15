-- Cluster-level local bootstrap only. PoolAI.Migrator remains the sole owner
-- of application schema and business migrations under docs/database/.
\set ON_ERROR_STOP on
\getenv poolai_api_password POOLAI_API_PASSWORD
\getenv poolai_worker_password POOLAI_WORKER_PASSWORD
\getenv poolai_migrator_password POOLAI_MIGRATOR_PASSWORD
\getenv poolai_database_name POOLAI_DATABASE_NAME

SELECT 'CREATE ROLE poolai_runtime_owner NOLOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS'
WHERE NOT EXISTS (
    SELECT 1 FROM pg_catalog.pg_roles WHERE rolname = 'poolai_runtime_owner'
) \gexec

ALTER ROLE poolai_runtime_owner
    NOLOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS;

SELECT pg_catalog.format(
    'CREATE ROLE poolai_api LOGIN PASSWORD %L NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS',
    :'poolai_api_password'
)
WHERE NOT EXISTS (
    SELECT 1 FROM pg_catalog.pg_roles WHERE rolname = 'poolai_api'
) \gexec

SELECT pg_catalog.format(
    'ALTER ROLE poolai_api PASSWORD %L',
    :'poolai_api_password'
) \gexec

ALTER ROLE poolai_api
    LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS;

SELECT pg_catalog.format(
    'CREATE ROLE poolai_worker LOGIN PASSWORD %L NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS',
    :'poolai_worker_password'
)
WHERE NOT EXISTS (
    SELECT 1 FROM pg_catalog.pg_roles WHERE rolname = 'poolai_worker'
) \gexec

SELECT pg_catalog.format(
    'ALTER ROLE poolai_worker PASSWORD %L',
    :'poolai_worker_password'
) \gexec

ALTER ROLE poolai_worker
    LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS;

SELECT pg_catalog.format(
    'CREATE ROLE poolai_migrator LOGIN PASSWORD %L NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS',
    :'poolai_migrator_password'
)
WHERE NOT EXISTS (
    SELECT 1 FROM pg_catalog.pg_roles WHERE rolname = 'poolai_migrator'
) \gexec

SELECT pg_catalog.format(
    'ALTER ROLE poolai_migrator PASSWORD %L',
    :'poolai_migrator_password'
) \gexec

ALTER ROLE poolai_migrator
    LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS;

REVOKE poolai_runtime_owner FROM poolai_api, poolai_worker;
GRANT poolai_runtime_owner TO poolai_migrator
    WITH INHERIT FALSE, SET TRUE;

ALTER DATABASE :"poolai_database_name" OWNER TO poolai_migrator;
REVOKE CREATE, TEMPORARY ON DATABASE :"poolai_database_name" FROM PUBLIC;
GRANT CONNECT ON DATABASE :"poolai_database_name"
    TO poolai_migrator, poolai_api, poolai_worker;
