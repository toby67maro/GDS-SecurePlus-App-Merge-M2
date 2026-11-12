-- SECURE PLUS 3.2 (SQLite, automated tests only): per-user roles, append-only audit ledger and scheduling PC.
-- SQLite has no roles; triggers keep the ledger append-only.

CREATE TABLE IF NOT EXISTS "secureplus.AppUser" (
    "WindowsAccount" TEXT NOT NULL PRIMARY KEY COLLATE NOCASE,
    "Upn" TEXT NULL,
    "DisplayName" TEXT NULL,
    "RoleCode" TEXT NOT NULL,
    "IsActive" INTEGER NOT NULL DEFAULT 1,
    "LastSeenUtc" TEXT NULL,
    "CreatedBy" TEXT NOT NULL,
    "CreatedAtUtc" TEXT NOT NULL,
    "ModifiedBy" TEXT NULL,
    "ModifiedAtUtc" TEXT NULL);
GO
CREATE TABLE IF NOT EXISTS "secureplus.SchedulerHost" (
    "HostKey" TEXT NOT NULL PRIMARY KEY,
    "ComputerName" TEXT NULL,
    "DesignatedBy" TEXT NULL,
    "DesignatedAtUtc" TEXT NULL,
    "HeartbeatUtc" TEXT NULL,
    "LastRunUtc" TEXT NULL,
    "LastStatus" TEXT NULL,
    "LastMessage" TEXT NULL,
    "AppVersion" TEXT NULL);
GO
CREATE TABLE IF NOT EXISTS "secureplus.AuditLedger" (
    "EventId" TEXT NOT NULL,
    "OccurredAtUtc" TEXT NOT NULL,
    "Category" TEXT NOT NULL,
    "EventType" TEXT NOT NULL,
    "Outcome" TEXT NOT NULL,
    "Actor" TEXT NOT NULL,
    "ComputerName" TEXT NULL,
    "WorkspaceId" TEXT NULL,
    "CustomerId" TEXT NULL,
    "TenantId" TEXT NULL,
    "Summary" TEXT NOT NULL,
    "PropertiesJson" TEXT NULL);
GO
CREATE TRIGGER IF NOT EXISTS "secureplus_AuditLedger_NoUpdate" BEFORE UPDATE ON "secureplus.AuditLedger"
BEGIN SELECT RAISE(ABORT, 'The audit ledger is append-only.'); END;
GO
CREATE TRIGGER IF NOT EXISTS "secureplus_AuditLedger_NoDelete" BEFORE DELETE ON "secureplus.AuditLedger"
BEGIN SELECT RAISE(ABORT, 'The audit ledger is append-only.'); END;
GO
