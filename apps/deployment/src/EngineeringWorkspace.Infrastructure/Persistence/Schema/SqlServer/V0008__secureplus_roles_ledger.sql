-- SECURE PLUS 3.2: per-user roles, append-only audit ledger, designated scheduling PC and database roles.
-- Repeatable: every statement is guarded. Ledger tables need SQL Server 2022 (or Azure SQL); older servers get a
-- normal table whose UPDATE and DELETE are denied to every Secure Plus role.

IF SCHEMA_ID(N'secureplus') IS NULL EXEC(N'CREATE SCHEMA [secureplus] AUTHORIZATION [dbo]');
GO
IF OBJECT_ID(N'[secureplus].[AppUser]', N'U') IS NULL
CREATE TABLE [secureplus].[AppUser] (
    [WindowsAccount] NVARCHAR(256) NOT NULL CONSTRAINT [PK_AppUser] PRIMARY KEY,
    [Upn] NVARCHAR(320) NULL,
    [DisplayName] NVARCHAR(200) NULL,
    [RoleCode] NVARCHAR(40) NOT NULL,
    [IsActive] BIT NOT NULL CONSTRAINT [DF_AppUser_IsActive] DEFAULT (1),
    [LastSeenUtc] DATETIME2 NULL,
    [CreatedBy] NVARCHAR(256) NOT NULL,
    [CreatedAtUtc] DATETIME2 NOT NULL,
    [ModifiedBy] NVARCHAR(256) NULL,
    [ModifiedAtUtc] DATETIME2 NULL);
GO
IF OBJECT_ID(N'[secureplus].[SchedulerHost]', N'U') IS NULL
CREATE TABLE [secureplus].[SchedulerHost] (
    [HostKey] NVARCHAR(20) NOT NULL CONSTRAINT [PK_SchedulerHost] PRIMARY KEY,
    [ComputerName] NVARCHAR(128) NULL,
    [DesignatedBy] NVARCHAR(256) NULL,
    [DesignatedAtUtc] DATETIME2 NULL,
    [HeartbeatUtc] DATETIME2 NULL,
    [LastRunUtc] DATETIME2 NULL,
    [LastStatus] NVARCHAR(40) NULL,
    [LastMessage] NVARCHAR(1000) NULL,
    [AppVersion] NVARCHAR(50) NULL);
GO
IF OBJECT_ID(N'[secureplus].[AuditLedger]', N'U') IS NULL
   AND (CAST(SERVERPROPERTY('EngineEdition') AS INT) IN (5, 8) OR CAST(SERVERPROPERTY('ProductMajorVersion') AS INT) >= 16)
EXEC(N'CREATE TABLE [secureplus].[AuditLedger] (
    [EventId] UNIQUEIDENTIFIER NOT NULL,
    [OccurredAtUtc] DATETIME2 NOT NULL,
    [Category] NVARCHAR(40) NOT NULL,
    [EventType] NVARCHAR(100) NOT NULL,
    [Outcome] NVARCHAR(20) NOT NULL,
    [Actor] NVARCHAR(256) NOT NULL,
    [ComputerName] NVARCHAR(128) NULL,
    [WorkspaceId] NVARCHAR(64) NULL,
    [CustomerId] NVARCHAR(64) NULL,
    [TenantId] NVARCHAR(64) NULL,
    [Summary] NVARCHAR(1000) NOT NULL,
    [PropertiesJson] NVARCHAR(MAX) NULL)
    WITH (LEDGER = ON (APPEND_ONLY = ON));');
GO
IF OBJECT_ID(N'[secureplus].[AuditLedger]', N'U') IS NULL
CREATE TABLE [secureplus].[AuditLedger] (
    [EventId] UNIQUEIDENTIFIER NOT NULL,
    [OccurredAtUtc] DATETIME2 NOT NULL,
    [Category] NVARCHAR(40) NOT NULL,
    [EventType] NVARCHAR(100) NOT NULL,
    [Outcome] NVARCHAR(20) NOT NULL,
    [Actor] NVARCHAR(256) NOT NULL,
    [ComputerName] NVARCHAR(128) NULL,
    [WorkspaceId] NVARCHAR(64) NULL,
    [CustomerId] NVARCHAR(64) NULL,
    [TenantId] NVARCHAR(64) NULL,
    [Summary] NVARCHAR(1000) NOT NULL,
    [PropertiesJson] NVARCHAR(MAX) NULL);
GO
IF DATABASE_PRINCIPAL_ID(N'secureplus_sales') IS NULL CREATE ROLE [secureplus_sales] AUTHORIZATION [dbo];
GO
IF DATABASE_PRINCIPAL_ID(N'secureplus_engineer') IS NULL CREATE ROLE [secureplus_engineer] AUTHORIZATION [dbo];
GO
IF DATABASE_PRINCIPAL_ID(N'secureplus_technician') IS NULL CREATE ROLE [secureplus_technician] AUTHORIZATION [dbo];
GO
IF DATABASE_PRINCIPAL_ID(N'secureplus_noc') IS NULL CREATE ROLE [secureplus_noc] AUTHORIZATION [dbo];
GO
IF DATABASE_PRINCIPAL_ID(N'secureplus_lead') IS NULL CREATE ROLE [secureplus_lead] AUTHORIZATION [dbo];
GO
IF DATABASE_PRINCIPAL_ID(N'secureplus_manager') IS NULL CREATE ROLE [secureplus_manager] AUTHORIZATION [dbo];
GO
ALTER ROLE [db_datareader] ADD MEMBER [secureplus_sales];
ALTER ROLE [db_datareader] ADD MEMBER [secureplus_engineer];
ALTER ROLE [db_datareader] ADD MEMBER [secureplus_technician];
ALTER ROLE [db_datareader] ADD MEMBER [secureplus_noc];
ALTER ROLE [db_datareader] ADD MEMBER [secureplus_lead];
ALTER ROLE [db_datareader] ADD MEMBER [secureplus_manager];
ALTER ROLE [db_datawriter] ADD MEMBER [secureplus_sales];
ALTER ROLE [db_datawriter] ADD MEMBER [secureplus_engineer];
ALTER ROLE [db_datawriter] ADD MEMBER [secureplus_technician];
ALTER ROLE [db_datawriter] ADD MEMBER [secureplus_noc];
ALTER ROLE [db_datawriter] ADD MEMBER [secureplus_lead];
ALTER ROLE [db_datawriter] ADD MEMBER [secureplus_manager];
GO
-- Tenant credentials: only Engineer, Technician/Deployer and Lead may read or change them.
DENY SELECT, INSERT, UPDATE, DELETE ON [secureplus].[TenantSecret] TO [secureplus_sales], [secureplus_noc], [secureplus_manager];
GO
-- The audit ledger is append-only for everyone (enforced by the ledger itself on SQL Server 2022 and by DENY otherwise).
DENY UPDATE, DELETE ON [secureplus].[AuditLedger] TO [secureplus_sales], [secureplus_engineer], [secureplus_technician], [secureplus_noc], [secureplus_lead], [secureplus_manager];
GO
-- Only Leads manage users and roles; everyone may update their own LastSeenUtc through the application.
DENY INSERT, DELETE ON [secureplus].[AppUser] TO [secureplus_sales], [secureplus_engineer], [secureplus_technician], [secureplus_noc], [secureplus_manager];
GO
GRANT ALTER ON ROLE::[secureplus_sales] TO [secureplus_lead];
GRANT ALTER ON ROLE::[secureplus_engineer] TO [secureplus_lead];
GRANT ALTER ON ROLE::[secureplus_technician] TO [secureplus_lead];
GRANT ALTER ON ROLE::[secureplus_noc] TO [secureplus_lead];
GRANT ALTER ON ROLE::[secureplus_lead] TO [secureplus_lead];
GRANT ALTER ON ROLE::[secureplus_manager] TO [secureplus_lead];
GO
