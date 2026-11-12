-- SECURE PLUS 4.0 beta.2: tenant inventory (users, groups, members, Intune devices) and scan type. Repeatable: every statement is guarded.
-- Directory attributes only; never credentials. Removed objects keep their row with RemovedAtUtc set.

IF COL_LENGTH(N'secureplus.AssessmentStatus', N'ScanType') IS NULL ALTER TABLE [secureplus].[AssessmentStatus] ADD [ScanType] NVARCHAR(20) NULL;
GO
IF COL_LENGTH(N'secureplus.AssessmentSchedule', N'ScanType') IS NULL ALTER TABLE [secureplus].[AssessmentSchedule] ADD [ScanType] NVARCHAR(20) NULL;
GO
IF OBJECT_ID(N'[secureplus].[TenantUser]', N'U') IS NULL
CREATE TABLE [secureplus].[TenantUser] (
    [TenantId] NVARCHAR(36) NOT NULL,
    [ObjectId] NVARCHAR(36) NOT NULL,
    [CustomerKey] NVARCHAR(64) NOT NULL,
    [DisplayName] NVARCHAR(256) NOT NULL,
    [UserPrincipalName] NVARCHAR(320) NULL,
    [Mail] NVARCHAR(320) NULL,
    [AccountEnabled] BIT NOT NULL,
    [UserType] NVARCHAR(20) NULL,
    [IsSynced] BIT NOT NULL,
    [Department] NVARCHAR(256) NULL,
    [JobTitle] NVARCHAR(256) NULL,
    [UsageLocation] NVARCHAR(10) NULL,
    [Licenses] NVARCHAR(2000) NULL,
    [AdminRoles] NVARCHAR(2000) NULL,
    [IsMfaRegistered] BIT NULL,
    [MfaMethods] NVARCHAR(1000) NULL,
    [LastSignInUtc] DATETIME2 NULL,
    [CreatedUtc] DATETIME2 NULL,
    [FirstSeenUtc] DATETIME2 NOT NULL,
    [LastSeenUtc] DATETIME2 NOT NULL,
    [RemovedAtUtc] DATETIME2 NULL,
    [LastScanRunId] INT NULL,
    CONSTRAINT [PK_TenantUser] PRIMARY KEY ([TenantId], [ObjectId]));
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_TenantUser_Customer' AND object_id = OBJECT_ID(N'[secureplus].[TenantUser]'))
    CREATE NONCLUSTERED INDEX [IX_TenantUser_Customer] ON [secureplus].[TenantUser] ([CustomerKey], [DisplayName]);
GO
IF OBJECT_ID(N'[secureplus].[TenantGroup]', N'U') IS NULL
CREATE TABLE [secureplus].[TenantGroup] (
    [TenantId] NVARCHAR(36) NOT NULL,
    [ObjectId] NVARCHAR(36) NOT NULL,
    [CustomerKey] NVARCHAR(64) NOT NULL,
    [DisplayName] NVARCHAR(256) NOT NULL,
    [Description] NVARCHAR(1024) NULL,
    [Mail] NVARCHAR(320) NULL,
    [Kind] NVARCHAR(40) NOT NULL,
    [IsDynamic] BIT NOT NULL,
    [MembershipRule] NVARCHAR(3072) NULL,
    [IsRoleAssignable] BIT NOT NULL,
    [IsSynced] BIT NOT NULL,
    [MemberCount] BIGINT NULL,
    [FirstSeenUtc] DATETIME2 NOT NULL,
    [LastSeenUtc] DATETIME2 NOT NULL,
    [RemovedAtUtc] DATETIME2 NULL,
    [LastScanRunId] INT NULL,
    CONSTRAINT [PK_TenantGroup] PRIMARY KEY ([TenantId], [ObjectId]));
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_TenantGroup_Customer' AND object_id = OBJECT_ID(N'[secureplus].[TenantGroup]'))
    CREATE NONCLUSTERED INDEX [IX_TenantGroup_Customer] ON [secureplus].[TenantGroup] ([CustomerKey], [DisplayName]);
GO
IF OBJECT_ID(N'[secureplus].[TenantGroupMember]', N'U') IS NULL
CREATE TABLE [secureplus].[TenantGroupMember] (
    [TenantId] NVARCHAR(36) NOT NULL,
    [GroupObjectId] NVARCHAR(36) NOT NULL,
    [MemberObjectId] NVARCHAR(36) NOT NULL,
    [CustomerKey] NVARCHAR(64) NOT NULL,
    [MemberType] NVARCHAR(40) NULL,
    [CollectedAtUtc] DATETIME2 NOT NULL,
    CONSTRAINT [PK_TenantGroupMember] PRIMARY KEY ([TenantId], [GroupObjectId], [MemberObjectId]));
GO
IF OBJECT_ID(N'[secureplus].[TenantDevice]', N'U') IS NULL
CREATE TABLE [secureplus].[TenantDevice] (
    [TenantId] NVARCHAR(36) NOT NULL,
    [DeviceId] NVARCHAR(64) NOT NULL,
    [CustomerKey] NVARCHAR(64) NOT NULL,
    [DeviceName] NVARCHAR(256) NOT NULL,
    [OperatingSystem] NVARCHAR(60) NULL,
    [OsVersion] NVARCHAR(60) NULL,
    [ComplianceState] NVARCHAR(40) NULL,
    [Ownership] NVARCHAR(20) NULL,
    [IsEncrypted] BIT NULL,
    [UserPrincipalName] NVARCHAR(320) NULL,
    [EntraDeviceId] NVARCHAR(64) NULL,
    [Model] NVARCHAR(256) NULL,
    [Manufacturer] NVARCHAR(256) NULL,
    [LastSyncUtc] DATETIME2 NULL,
    [EnrolledUtc] DATETIME2 NULL,
    [FirstSeenUtc] DATETIME2 NOT NULL,
    [LastSeenUtc] DATETIME2 NOT NULL,
    [RemovedAtUtc] DATETIME2 NULL,
    [LastScanRunId] INT NULL,
    CONSTRAINT [PK_TenantDevice] PRIMARY KEY ([TenantId], [DeviceId]));
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_TenantDevice_Customer' AND object_id = OBJECT_ID(N'[secureplus].[TenantDevice]'))
    CREATE NONCLUSTERED INDEX [IX_TenantDevice_Customer] ON [secureplus].[TenantDevice] ([CustomerKey], [DeviceName]);
GO
