# ADR-0007: Generate the EaaS v3 schema from the v3 data dictionary

- Status: Accepted
- Date: 2026-11-06

## Context

EaaS v3 is the source of truth for the database. The Documents repository contains the v3 data dictionary (CSV) and
Visio design but no SQL scripts or migration mechanism. The foundation build created only a marker table on SQL
Server and a hand-written SQLite subset whose `audit.TenantConnection` key differed from v3.

## Decision

- Copy the dictionary verbatim into `schema/eaas-v3/` with its commit and SHA-256, and generate the baseline DDL for
  SQL Server/Azure SQL and SQLite with `build/scripts/eaas-schema/generate_eaas_v3_schema.py`. Generated scripts and
  a table/column manifest are committed and embedded in Infrastructure.
- Apply the literal v3 rules only (see `eaas-v3-database-analysis.md`); do not invent nullability, defaults or seed data.
- Version migrations (`V0001` baseline, `V0002` Secure Plus extensions), record them in `secureplus.SchemaVersion`,
  run them in one transaction with an application lock, and classify databases read-only before any change.
- Keep SQLite as a Secure Plus extension for offline development, with identical table and column names.

## Consequences

- The full 204-table v3 schema can be initialized; the one v3 type mismatch is reported, not silently "fixed".
- Databases from the pre-release foundation build (`__secureplus_schema`) are reported as incompatible and left
  untouched; engineers create a new database.
- A new dictionary version requires copying the CSV, regenerating and adding a new migration (`V0003+`), never editing
  an applied one (checksums detect edits).
