-- Grant the wolverine user permissions to create and drop schemas (users in Oracle)
-- This script runs after the APP_USER is created by the container

ALTER SESSION SET CONTAINER = FREEPDB1;

-- Grant privileges to create and drop users (schemas)
GRANT CREATE USER TO wolverine;
GRANT DROP USER TO wolverine;
GRANT ALTER USER TO wolverine;

-- Grant ability to create sessions and manage objects in other schemas
GRANT CREATE SESSION TO wolverine WITH ADMIN OPTION;
GRANT CREATE TABLE TO wolverine WITH ADMIN OPTION;
GRANT CREATE SEQUENCE TO wolverine WITH ADMIN OPTION;
GRANT CREATE PROCEDURE TO wolverine WITH ADMIN OPTION;
GRANT CREATE VIEW TO wolverine WITH ADMIN OPTION;

-- Grant ability to create/drop/alter objects in ANY schema (needed for cross-schema testing)
GRANT CREATE ANY TABLE TO wolverine;
GRANT DROP ANY TABLE TO wolverine;
GRANT ALTER ANY TABLE TO wolverine;
GRANT SELECT ANY TABLE TO wolverine;
GRANT INSERT ANY TABLE TO wolverine;
GRANT UPDATE ANY TABLE TO wolverine;
GRANT DELETE ANY TABLE TO wolverine;
GRANT CREATE ANY INDEX TO wolverine;
GRANT DROP ANY INDEX TO wolverine;
GRANT CREATE ANY SEQUENCE TO wolverine;
GRANT DROP ANY SEQUENCE TO wolverine;
GRANT CREATE ANY PROCEDURE TO wolverine;
GRANT DROP ANY PROCEDURE TO wolverine;
GRANT EXECUTE ANY PROCEDURE TO wolverine;

-- Grant unlimited tablespace so wolverine can allocate space to schemas it creates
GRANT UNLIMITED TABLESPACE TO wolverine WITH ADMIN OPTION;

-- Grant ability to select from system views for schema introspection
GRANT SELECT ON sys.all_tables TO wolverine;
GRANT SELECT ON sys.all_tab_columns TO wolverine;
GRANT SELECT ON sys.all_constraints TO wolverine;
GRANT SELECT ON sys.all_cons_columns TO wolverine;
GRANT SELECT ON sys.all_indexes TO wolverine;
GRANT SELECT ON sys.all_ind_columns TO wolverine;
GRANT SELECT ON sys.all_sequences TO wolverine;
GRANT SELECT ON sys.all_users TO wolverine;
GRANT SELECT ON sys.all_objects TO wolverine;
