-- SECURE PLUS 4.0 beta.2: tenant inventory and scan type (SQLite, automated tests only).

ALTER TABLE "secureplus.AssessmentStatus" ADD COLUMN "ScanType" TEXT NULL;
GO
ALTER TABLE "secureplus.AssessmentSchedule" ADD COLUMN "ScanType" TEXT NULL;
GO
CREATE TABLE IF NOT EXISTS "secureplus.TenantUser" ("TenantId" TEXT NOT NULL, "ObjectId" TEXT NOT NULL, "CustomerKey" TEXT NOT NULL, "DisplayName" TEXT NOT NULL,
    "UserPrincipalName" TEXT NULL, "Mail" TEXT NULL, "AccountEnabled" INTEGER NOT NULL, "UserType" TEXT NULL, "IsSynced" INTEGER NOT NULL, "Department" TEXT NULL,
    "JobTitle" TEXT NULL, "UsageLocation" TEXT NULL, "Licenses" TEXT NULL, "AdminRoles" TEXT NULL, "IsMfaRegistered" INTEGER NULL, "MfaMethods" TEXT NULL,
    "LastSignInUtc" TEXT NULL, "CreatedUtc" TEXT NULL, "FirstSeenUtc" TEXT NOT NULL, "LastSeenUtc" TEXT NOT NULL, "RemovedAtUtc" TEXT NULL, "LastScanRunId" INTEGER NULL,
    PRIMARY KEY ("TenantId", "ObjectId"));
GO
CREATE TABLE IF NOT EXISTS "secureplus.TenantGroup" ("TenantId" TEXT NOT NULL, "ObjectId" TEXT NOT NULL, "CustomerKey" TEXT NOT NULL, "DisplayName" TEXT NOT NULL,
    "Description" TEXT NULL, "Mail" TEXT NULL, "Kind" TEXT NOT NULL, "IsDynamic" INTEGER NOT NULL, "MembershipRule" TEXT NULL, "IsRoleAssignable" INTEGER NOT NULL,
    "IsSynced" INTEGER NOT NULL, "MemberCount" INTEGER NULL, "FirstSeenUtc" TEXT NOT NULL, "LastSeenUtc" TEXT NOT NULL, "RemovedAtUtc" TEXT NULL, "LastScanRunId" INTEGER NULL,
    PRIMARY KEY ("TenantId", "ObjectId"));
GO
CREATE TABLE IF NOT EXISTS "secureplus.TenantGroupMember" ("TenantId" TEXT NOT NULL, "GroupObjectId" TEXT NOT NULL, "MemberObjectId" TEXT NOT NULL, "CustomerKey" TEXT NOT NULL,
    "MemberType" TEXT NULL, "CollectedAtUtc" TEXT NOT NULL, PRIMARY KEY ("TenantId", "GroupObjectId", "MemberObjectId"));
GO
CREATE TABLE IF NOT EXISTS "secureplus.TenantDevice" ("TenantId" TEXT NOT NULL, "DeviceId" TEXT NOT NULL, "CustomerKey" TEXT NOT NULL, "DeviceName" TEXT NOT NULL,
    "OperatingSystem" TEXT NULL, "OsVersion" TEXT NULL, "ComplianceState" TEXT NULL, "Ownership" TEXT NULL, "IsEncrypted" INTEGER NULL, "UserPrincipalName" TEXT NULL,
    "EntraDeviceId" TEXT NULL, "Model" TEXT NULL, "Manufacturer" TEXT NULL, "LastSyncUtc" TEXT NULL, "EnrolledUtc" TEXT NULL, "FirstSeenUtc" TEXT NOT NULL,
    "LastSeenUtc" TEXT NOT NULL, "RemovedAtUtc" TEXT NULL, "LastScanRunId" INTEGER NULL, PRIMARY KEY ("TenantId", "DeviceId"));
GO
