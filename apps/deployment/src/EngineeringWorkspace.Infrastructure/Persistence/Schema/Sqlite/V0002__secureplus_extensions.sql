-- SECURE PLUS EXTENSION tables for SQLite. See the SQL Server script for the rationale.
-- These never hold secret material: credential rows carry metadata and a SecretReference only.

CREATE TABLE IF NOT EXISTS "secureplus.Workspace" (
    "WorkspaceId" TEXT NOT NULL PRIMARY KEY,
    "Name" TEXT NOT NULL,
    "Directory" TEXT NOT NULL,
    "CustomerId" INTEGER,
    "CreatedAtUtc" TEXT,
    "ModifiedAtUtc" TEXT,
    FOREIGN KEY ("CustomerId") REFERENCES "cust.Customer" ("CustomerId")
);
GO
CREATE TABLE IF NOT EXISTS "secureplus.CredentialMetadata" (
    "CredentialId" TEXT NOT NULL PRIMARY KEY,
    "DisplayName" TEXT NOT NULL,
    "CredentialType" TEXT NOT NULL,
    "Provider" TEXT NOT NULL,
    "CreatedAtUtc" TEXT NOT NULL,
    "UpdatedAtUtc" TEXT NOT NULL,
    "ExpirationDateUtc" TEXT,
    "SecretReference" TEXT NOT NULL UNIQUE,
    "CustomerId" TEXT,
    "TenantId" TEXT
);
GO
CREATE TABLE IF NOT EXISTS "secureplus.AuditEvent" (
    "AuditEventId" INTEGER NOT NULL PRIMARY KEY,
    "EventType" TEXT NOT NULL,
    "Summary" TEXT NOT NULL,
    "OccurredAtUtc" TEXT NOT NULL,
    "CustomerId" TEXT,
    "TenantId" TEXT
);
GO
