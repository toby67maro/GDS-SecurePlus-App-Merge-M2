-- SECURE PLUS EXTENSION tables for SQL Server 2022 / Azure SQL.
-- These hold application concerns that EaaS v3 does not model. They never hold secret material:
-- credential rows carry metadata and a SecretReference only; the secret stays in ISecretStore.
-- Repeatable: every statement is guarded and never drops or alters existing objects.

IF SCHEMA_ID(N'secureplus') IS NULL EXEC(N'CREATE SCHEMA [secureplus] AUTHORIZATION [dbo]');
GO
IF OBJECT_ID(N'[secureplus].[Workspace]', N'U') IS NULL
CREATE TABLE [secureplus].[Workspace] (
    [WorkspaceId] NVARCHAR(64) NOT NULL,
    [Name] NVARCHAR(200) NOT NULL,
    [Directory] NVARCHAR(1000) NOT NULL,
    [CustomerId] INT NULL,
    [CreatedAtUtc] DATETIME2 NULL,
    [ModifiedAtUtc] DATETIME2 NULL,
    CONSTRAINT [PK_Workspace] PRIMARY KEY CLUSTERED ([WorkspaceId])
);
GO
IF OBJECT_ID(N'[secureplus].[FK_Workspace_CustomerId]', N'F') IS NULL
    ALTER TABLE [secureplus].[Workspace] ADD CONSTRAINT [FK_Workspace_CustomerId] FOREIGN KEY ([CustomerId]) REFERENCES [cust].[Customer] ([CustomerId]);
GO
IF OBJECT_ID(N'[secureplus].[CredentialMetadata]', N'U') IS NULL
CREATE TABLE [secureplus].[CredentialMetadata] (
    [CredentialId] NVARCHAR(128) NOT NULL,
    [DisplayName] NVARCHAR(200) NOT NULL,
    [CredentialType] NVARCHAR(40) NOT NULL,
    [Provider] NVARCHAR(40) NOT NULL,
    [CreatedAtUtc] DATETIME2 NOT NULL,
    [UpdatedAtUtc] DATETIME2 NOT NULL,
    [ExpirationDateUtc] DATETIME2 NULL,
    [SecretReference] NVARCHAR(300) NOT NULL,
    [CustomerId] NVARCHAR(64) NULL,
    [TenantId] NVARCHAR(64) NULL,
    CONSTRAINT [PK_CredentialMetadata] PRIMARY KEY CLUSTERED ([CredentialId]),
    CONSTRAINT [UQ_CredentialMetadata_SecretReference] UNIQUE ([SecretReference])
);
GO
IF OBJECT_ID(N'[secureplus].[AuditEvent]', N'U') IS NULL
CREATE TABLE [secureplus].[AuditEvent] (
    [AuditEventId] BIGINT NOT NULL,
    [EventType] NVARCHAR(100) NOT NULL,
    [Summary] NVARCHAR(2000) NOT NULL,
    [OccurredAtUtc] DATETIME2 NOT NULL,
    [CustomerId] NVARCHAR(64) NULL,
    [TenantId] NVARCHAR(64) NULL,
    CONSTRAINT [PK_AuditEvent] PRIMARY KEY CLUSTERED ([AuditEventId])
);
GO
