#!/usr/bin/env python3
"""Generates the V0006 Secure Plus extension migration for SQL Server/Azure SQL and SQLite from one table spec.

Usage: python3 build/scripts/schema/generate_extension_migration.py
Writes src/EngineeringWorkspace.Infrastructure/Persistence/Schema/{SqlServer,Sqlite}/V0006__secureplus_v2.sql and
prints the manifest entries (secureplus tables and columns) used by SchemaMigrations.ExpectedTables.
"""
import json
import os

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", "..", ".."))
NAME = "V0006__secureplus_v2.sql"

# Column: (name, type, nullable). Types: id (INT IDENTITY), bigid (BIGINT IDENTITY), int, bit, dt, dec, key<n>, text<n>, max.
ALTERS = {
    "AssessmentStatus": [("Source", "text20", True), ("ScheduleId", "int", True), ("StartedAtUtc", "dt", True), ("ResultsSaved", "int", True),
                         ("ErrorMessage", "text2000", True)],
    "AssessmentResult": [("RecordedAtUtc", "dt", True)],
    "AssessmentSchedule": [("CustomerKey", "key64", True), ("DaysOfWeek", "int", True), ("Months", "int", True), ("IntervalHours", "int", True),
                           ("IntervalWeeks", "int", True), ("WindowStart", "text5", True), ("WindowEnd", "text5", True), ("MonthlyMode", "text20", True),
                           ("WeekOfMonth", "int", True), ("TimeZoneId", "text100", True), ("AnchorDate", "text10", True), ("LastStatus", "text20", True),
                           ("LastMessage", "text1000", True), ("ModifiedAtUtc", "dt", True)],
}

TABLES = [
    ("SeedVersion", "Seed datasets applied to this database (versioned, idempotent reference data).",
     [("Dataset", "key64", False), ("Version", "text50", False), ("ContentHash", "text64", False), ("RowsApplied", "int", False), ("AppliedAtUtc", "dt", False),
      ("AppliedBy", "text256", False), ("Summary", "text2000", True)], ["Dataset"], [], []),
    ("ReferenceValue", "Solution areas and experience areas.",
     [("Category", "key50", False), ("Code", "key64", False), ("Name", "text200", False), ("Description", "text1000", True), ("SortOrder", "int", False)],
     ["Category", "Code"], [], []),
    ("ControlDetail", "Remediation content and implementation details of each catalog control (EaaS v3 has no columns for them).",
     [("ControlId", "key150", False), ("SeedVersion", "text50", False), ("SolutionArea", "text64", True), ("ExperienceArea", "text64", True),
      ("VariantOf", "text150", True), ("VariantLabel", "text400", True), ("ComplianceStandards", "max", True), ("EstimatedMinutes", "int", True),
      ("Difficulty", "text20", True), ("Effort", "text20", True), ("RiskLevel", "text20", True), ("Benefits", "text1000", True), ("ExperienceImpact", "text2000", True),
      ("GraphRequest", "max", True), ("GraphCommand", "max", True), ("PowerShell", "max", True), ("JsonBody", "max", True), ("SettingPath", "text2000", True),
      ("GpoPath", "text2000", True), ("Registry", "text2000", True), ("Generated", "text1000", True), ("UpdatedAtUtc", "dt", False)],
     ["ControlId"], [("IX_ControlDetail_SolutionArea", ["SolutionArea"])], []),
    ("ControlAlias", "Old control IDs of merged duplicates; results stored with an alias resolve to ControlId.",
     [("Alias", "key150", False), ("ControlId", "text150", False), ("Reason", "text1000", True)], ["Alias"], [], []),
    ("ScheduleRun", "Run history of recurring assessment schedules.",
     [("ScheduleRunId", "id", False), ("ScheduleId", "int", False), ("StartedAtUtc", "dt", False), ("CompletedAtUtc", "dt", True), ("StatusCode", "text20", False),
      ("Message", "text2000", True), ("Host", "text50", True), ("AssessmentRunIds", "text1000", True)],
     ["ScheduleRunId"], [("IX_ScheduleRun_Schedule", ["ScheduleId", "StartedAtUtc"])], []),
    ("Specification", "Specifications: standard (unassigned) or tailored. Linked to spec.Specification when assigned to an engagement.",
     [("SpecificationKey", "key64", False), ("Name", "text200", False), ("Description", "text2000", True), ("IsStandard", "bit", False), ("IsSystem", "bit", False),
      ("IsModified", "bit", False), ("BasedOnKey", "text64", True), ("StatusCode", "text20", False), ("SeedVersion", "text50", True), ("CreatedBy", "text256", False),
      ("CreatedAtUtc", "dt", False), ("ModifiedBy", "text256", True), ("ModifiedAtUtc", "dt", True)], ["SpecificationKey"], [], ["Name"]),
    ("SpecificationItem", "Members of a specification: Experience, Framework, ComplianceStandard, Requirement, PolicySet, Control or Policy.",
     [("SpecificationKey", "key64", False), ("ItemType", "key30", False), ("ItemKey", "key200", False)], ["SpecificationKey", "ItemType", "ItemKey"], [],
     []),
    ("SpecificationAssignment", "The specification each customer uses (one per customer).",
     [("CustomerKey", "key64", False), ("SpecificationKey", "text64", False), ("AssignedBy", "text256", False), ("AssignedAtUtc", "dt", False)],
     ["CustomerKey"], [("IX_SpecificationAssignment_Specification", ["SpecificationKey"])], []),
    ("Baseline", "Baseline configurations: named, activatable collections of policy sets and controls.",
     [("BaselineKey", "key64", False), ("Name", "text200", False), ("Description", "text2000", True), ("IsActive", "bit", False), ("IsSystem", "bit", False),
      ("IsModified", "bit", False), ("SeedVersion", "text50", True), ("CreatedBy", "text256", False), ("CreatedAtUtc", "dt", False), ("ModifiedBy", "text256", True),
      ("ModifiedAtUtc", "dt", True)], ["BaselineKey"], [], ["Name"]),
    ("BaselineItem", "Members of a baseline: Framework, ComplianceStandard, Experience, PolicySet, Control or Policy.",
     [("BaselineKey", "key64", False), ("ItemType", "key30", False), ("ItemKey", "key200", False)], ["BaselineKey", "ItemType", "ItemKey"], [], []),
    ("ChangeRequest", "Change requests (extends deploy.ChangeRequest with risk, rollback, approval, window and workflow status).",
     [("ChangeRequestId", "id", False), ("CustomerKey", "text64", False), ("Title", "text300", False), ("Description", "max", True), ("Risk", "text20", False),
      ("Impact", "text2000", True), ("RollbackPlan", "max", True), ("RequestedBy", "text256", False), ("ApprovedBy", "text256", True), ("Owner", "text256", True),
      ("WindowStartUtc", "dt", True), ("WindowEndUtc", "dt", True), ("StatusCode", "text20", False), ("DecisionNote", "text2000", True), ("CreatedAtUtc", "dt", False),
      ("ModifiedAtUtc", "dt", False), ("DecidedAtUtc", "dt", True), ("CompletedAtUtc", "dt", True)],
     ["ChangeRequestId"], [("IX_ChangeRequest_Customer", ["CustomerKey", "StatusCode"])], []),
    ("ChangeRequestItem", "Controls and policies changed by a change request.",
     [("ChangeRequestId", "int", False), ("ItemType", "key30", False), ("ItemKey", "key200", False)], ["ChangeRequestId", "ItemType", "ItemKey"], [], []),
    ("ChangeExecution", "Executions and simulations of approved change requests.",
     [("ChangeExecutionId", "id", False), ("ChangeRequestId", "int", False), ("ModeCode", "text20", False), ("StatusCode", "text20", False), ("StartedAtUtc", "dt", False),
      ("CompletedAtUtc", "dt", True), ("ExecutedBy", "text256", False), ("Log", "max", True), ("ReassessmentRunId", "int", True)],
     ["ChangeExecutionId"], [("IX_ChangeExecution_Request", ["ChangeRequestId"])], []),
    ("CustomerException", "Approved exceptions: a control a customer is not required to meet, with reason, approver and expiry.",
     [("ExceptionId", "id", False), ("CustomerKey", "text64", False), ("ControlId", "text150", False), ("Reason", "text2000", False), ("ApprovedBy", "text256", True),
      ("ExpiresAtUtc", "dt", True), ("StatusCode", "text20", False), ("CreatedBy", "text256", False), ("CreatedAtUtc", "dt", False), ("ModifiedAtUtc", "dt", True)],
     ["ExceptionId"], [("IX_CustomerException_Customer", ["CustomerKey", "ControlId"])], []),
    ("EntityAudit", "Who changed what and when (customer profiles, specifications, baselines, change requests).",
     [("EntityAuditId", "bigid", False), ("EntityType", "text50", False), ("EntityKey", "text200", False), ("Property", "text200", False), ("OldValue", "text2000", True),
      ("NewValue", "text2000", True), ("ChangedBy", "text256", False), ("ChangedAtUtc", "dt", False)],
     ["EntityAuditId"], [("IX_EntityAudit_Entity", ["EntityType", "EntityKey", "ChangedAtUtc"])], []),
]

FOREIGN_KEYS = [
    ("SpecificationItem", "SpecificationKey", "Specification", "SpecificationKey"),
    ("BaselineItem", "BaselineKey", "Baseline", "BaselineKey"),
    ("ChangeRequestItem", "ChangeRequestId", "ChangeRequest", "ChangeRequestId"),
    ("ChangeExecution", "ChangeRequestId", "ChangeRequest", "ChangeRequestId"),
    ("ScheduleRun", "ScheduleId", "AssessmentSchedule", "ScheduleId"),
]


def sql_type(kind):
    if kind == "id":
        return "INT IDENTITY(1,1)"
    if kind == "bigid":
        return "BIGINT IDENTITY(1,1)"
    if kind == "int":
        return "INT"
    if kind == "bit":
        return "BIT"
    if kind == "dt":
        return "DATETIME2"
    if kind == "max":
        return "NVARCHAR(MAX)"
    return f"NVARCHAR({kind.lstrip('keytx')})"


def sqlite_type(kind):
    return {"id": "INTEGER", "bigid": "INTEGER", "int": "INTEGER", "bit": "INTEGER"}.get(kind, "TEXT")


HEADER = ["-- SECURE PLUS EXTENSION tables for SQL Server 2022 / Azure SQL (release 2.0): seed versions, control remediation and",
          "-- implementation details, schedules v2 and run history, specifications, baselines, change requests, exceptions and audit."]
SQLITE_HEADER = "-- SECURE PLUS EXTENSION tables for SQLite (release 2.0). Generated by build/scripts/schema/generate_extension_migration.py."


def qualified(table):
    """ALTERS keys are secureplus table names, or schema.table for EaaS v3 tables."""
    return tuple(table.split(".", 1)) if "." in table else ("secureplus", table)


def sql_server():
    out = HEADER + [
           "-- Generated by build/scripts/schema/generate_extension_migration.py. Repeatable: every statement is guarded.", "",
           "IF SCHEMA_ID(N'secureplus') IS NULL EXEC(N'CREATE SCHEMA [secureplus] AUTHORIZATION [dbo]');", "GO"]
    for table, columns in ALTERS.items():
        for name, kind, _ in columns:
            schema, bare = qualified(table)
            out += [f"IF COL_LENGTH(N'{schema}.{bare}', N'{name}') IS NULL",
                    f"    ALTER TABLE [{schema}].[{bare}] ADD [{name}] {sql_type(kind)} NULL;", "GO"]
    for table, description, columns, key, indexes, uniques in TABLES:
        out.append(f"-- {description}")
        out.append(f"IF OBJECT_ID(N'[secureplus].[{table}]', N'U') IS NULL")
        out.append(f"CREATE TABLE [secureplus].[{table}] (")
        lines = [f"    [{name}] {sql_type(kind)} {'NULL' if nullable else 'NOT NULL'}" for name, kind, nullable in columns]
        lines.append(f"    CONSTRAINT [PK_{table}] PRIMARY KEY CLUSTERED ({', '.join(f'[{column}]' for column in key)})")
        out.append(",\n".join(lines))
        out += [");", "GO"]
        for index, index_columns in indexes:
            out += [f"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'{index}' AND object_id = OBJECT_ID(N'[secureplus].[{table}]'))",
                    f"    CREATE NONCLUSTERED INDEX [{index}] ON [secureplus].[{table}] ({', '.join(f'[{column}]' for column in index_columns)});", "GO"]
        for unique in uniques:
            out += [f"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UX_{table}_{unique}' AND object_id = OBJECT_ID(N'[secureplus].[{table}]'))",
                    f"    CREATE UNIQUE NONCLUSTERED INDEX [UX_{table}_{unique}] ON [secureplus].[{table}] ([{unique}]);", "GO"]
    for table, column, target, target_column in FOREIGN_KEYS:
        out += [f"IF OBJECT_ID(N'[secureplus].[FK_{table}_{column}]', N'F') IS NULL",
                f"    ALTER TABLE [secureplus].[{table}] ADD CONSTRAINT [FK_{table}_{column}] FOREIGN KEY ([{column}]) REFERENCES [secureplus].[{target}] ([{target_column}])"
                f"{' ON DELETE CASCADE' if table.endswith('Item') else ''};", "GO"]
    return "\n".join(out) + "\n"


def sqlite():
    out = [SQLITE_HEADER,
           "-- Applied once (recorded in secureplus.SchemaVersion); new tables are guarded.", ""]
    for table, columns in ALTERS.items():
        for name, kind, _ in columns:
            schema, bare = qualified(table)
            out += [f'ALTER TABLE "{schema}.{bare}" ADD COLUMN "{name}" {sqlite_type(kind)};', "GO"]
    fks = {}
    for table, column, target, target_column in FOREIGN_KEYS:
        fks.setdefault(table, []).append((column, target, target_column))
    for table, description, columns, key, indexes, uniques in TABLES:
        out.append(f"-- {description}")
        out.append(f'CREATE TABLE IF NOT EXISTS "secureplus.{table}" (')
        lines = []
        for name, kind, nullable in columns:
            if kind in ("id", "bigid"):
                lines.append(f'    "{name}" INTEGER PRIMARY KEY')
            else:
                lines.append(f'    "{name}" {sqlite_type(kind)}{"" if nullable else " NOT NULL"}')
        if not any(kind in ("id", "bigid") for _, kind, _ in columns):
            lines.append(f'    PRIMARY KEY ({", ".join(f"""\"{column}\"""" for column in key)})')
        for column, target, target_column in fks.get(table, []):
            cascade = " ON DELETE CASCADE" if table.endswith("Item") else ""
            lines.append(f'    FOREIGN KEY ("{column}") REFERENCES "secureplus.{target}" ("{target_column}"){cascade}')
        out.append(",\n".join(lines))
        out += [");", "GO"]
        for index, index_columns in indexes:
            out += [f'CREATE INDEX IF NOT EXISTS "{index}" ON "secureplus.{table}" ({", ".join(f"""\"{column}\"""" for column in index_columns)});', "GO"]
        for unique in uniques:
            out += [f'CREATE UNIQUE INDEX IF NOT EXISTS "UX_{table}_{unique}" ON "secureplus.{table}" ("{unique}");', "GO"]
    return "\n".join(out) + "\n"


def main():
    schema = os.path.join(ROOT, "src", "EngineeringWorkspace.Infrastructure", "Persistence", "Schema")
    with open(os.path.join(schema, "SqlServer", NAME), "w", encoding="utf-8", newline="\n") as handle:
        handle.write(sql_server())
    with open(os.path.join(schema, "Sqlite", NAME), "w", encoding="utf-8", newline="\n") as handle:
        handle.write(sqlite())
    manifest = {table: [name for name, _, _ in columns] for table, _, columns, _, _, _ in TABLES}
    manifest.update({f"+{table}": [name for name, _, _ in columns] for table, columns in ALTERS.items()})
    print(json.dumps(manifest))


if __name__ == "__main__":
    main()
