-- One database per sample, on the shared server published by ../docker-compose.yml.
-- Marten creates schemas and tables on demand; it does not create databases.
-- Names must match the connection strings in each samples/<Project>/appsettings.json.
CREATE DATABASE bank_account;
CREATE DATABASE booking;
CREATE DATABASE clean_architecture_todos;
CREATE DATABASE cqrs_minimal_api;
CREATE DATABASE ecommerce;
CREATE DATABASE inflow;
CREATE DATABASE meeting_groups;
CREATE DATABASE more_speakers;
CREATE DATABASE outbox_demo;
