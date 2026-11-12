# ADR-0005-persistence-strategy

Decision recorded for the foundation milestone; refined in the consolidated implementation.

The relational store follows EaaS v3 (SQL Server 2022 / Azure SQL, with SQLite as a local extension) and is created
only by explicit, confirmed operations (ADR-0007). Workspace, customer-profile and credential-metadata data use
per-user files (INI/JSON) until relational persistence of those aggregates (M2). Secrets never enter either store
(ADR-0008).
