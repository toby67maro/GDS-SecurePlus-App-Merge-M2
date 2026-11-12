-- SECURE PLUS EXTENSION tables for SQL Server 2022 / Azure SQL: customer, workspace and assessment history.
-- Customers and assessments are written to the EaaS v3 tables (cust.Customer, cust.Engagement, cust.AssessmentRun);
-- these tables link them to the application's stable customer identifiers and hold assessment status and findings.
-- Repeatable: every statement is guarded and never drops or alters existing objects.

IF SCHEMA_ID(N'secureplus') IS NULL EXEC(N'CREATE SCHEMA [secureplus] AUTHORIZATION [dbo]');
GO
IF OBJECT_ID(N'[secureplus].[CustomerLink]', N'U') IS NULL
CREATE TABLE [secureplus].[CustomerLink] (
    [CustomerKey] NVARCHAR(64) NOT NULL,
    [CustomerId] INT NOT NULL,
    [EngagementId] INT NULL,
    [DisplayName] NVARCHAR(200) NOT NULL,
    [IsSample] BIT NOT NULL CONSTRAINT [DF_CustomerLink_IsSample] DEFAULT (0),
    [CreatedAtUtc] DATETIME2 NOT NULL,
    [ModifiedAtUtc] DATETIME2 NOT NULL,
    CONSTRAINT [PK_CustomerLink] PRIMARY KEY CLUSTERED ([CustomerKey])
);
GO
IF OBJECT_ID(N'[secureplus].[FK_CustomerLink_CustomerId]', N'F') IS NULL
    ALTER TABLE [secureplus].[CustomerLink] ADD CONSTRAINT [FK_CustomerLink_CustomerId] FOREIGN KEY ([CustomerId]) REFERENCES [cust].[Customer] ([CustomerId]);
GO
IF OBJECT_ID(N'[secureplus].[FK_CustomerLink_EngagementId]', N'F') IS NULL
    ALTER TABLE [secureplus].[CustomerLink] ADD CONSTRAINT [FK_CustomerLink_EngagementId] FOREIGN KEY ([EngagementId]) REFERENCES [cust].[Engagement] ([EngagementId]);
GO
IF OBJECT_ID(N'[secureplus].[AssessmentStatus]', N'U') IS NULL
CREATE TABLE [secureplus].[AssessmentStatus] (
    [AssessmentRunId] INT NOT NULL,
    [CustomerKey] NVARCHAR(64) NOT NULL,
    [WorkspaceId] NVARCHAR(64) NULL,
    [Name] NVARCHAR(200) NOT NULL,
    [StatusCode] NVARCHAR(20) NOT NULL,
    [Score] DECIMAL(5, 2) NULL,
    [ControlsTotal] INT NULL,
    [ControlsPassed] INT NULL,
    [RequestedAtUtc] DATETIME2 NOT NULL,
    [CompletedAtUtc] DATETIME2 NULL,
    [IsSample] BIT NOT NULL CONSTRAINT [DF_AssessmentStatus_IsSample] DEFAULT (0),
    CONSTRAINT [PK_AssessmentStatus] PRIMARY KEY CLUSTERED ([AssessmentRunId])
);
GO
IF OBJECT_ID(N'[secureplus].[FK_AssessmentStatus_AssessmentRunId]', N'F') IS NULL
    ALTER TABLE [secureplus].[AssessmentStatus] ADD CONSTRAINT [FK_AssessmentStatus_AssessmentRunId] FOREIGN KEY ([AssessmentRunId]) REFERENCES [cust].[AssessmentRun] ([AssessmentRunId]);
GO
IF OBJECT_ID(N'[secureplus].[FK_AssessmentStatus_CustomerKey]', N'F') IS NULL
    ALTER TABLE [secureplus].[AssessmentStatus] ADD CONSTRAINT [FK_AssessmentStatus_CustomerKey] FOREIGN KEY ([CustomerKey]) REFERENCES [secureplus].[CustomerLink] ([CustomerKey]);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_AssessmentStatus_CustomerKey' AND object_id = OBJECT_ID(N'[secureplus].[AssessmentStatus]'))
    CREATE NONCLUSTERED INDEX [IX_AssessmentStatus_CustomerKey] ON [secureplus].[AssessmentStatus] ([CustomerKey], [RequestedAtUtc]);
GO
IF OBJECT_ID(N'[secureplus].[AssessmentFinding]', N'U') IS NULL
CREATE TABLE [secureplus].[AssessmentFinding] (
    [AssessmentFindingId] INT IDENTITY(1,1) NOT NULL,
    [AssessmentRunId] INT NOT NULL,
    [Area] NVARCHAR(100) NOT NULL,
    [Control] NVARCHAR(300) NOT NULL,
    [Result] NVARCHAR(20) NOT NULL,
    [Severity] NVARCHAR(20) NOT NULL,
    [Recommendation] NVARCHAR(1000) NULL,
    CONSTRAINT [PK_AssessmentFinding] PRIMARY KEY CLUSTERED ([AssessmentFindingId])
);
GO
IF OBJECT_ID(N'[secureplus].[FK_AssessmentFinding_AssessmentRunId]', N'F') IS NULL
    ALTER TABLE [secureplus].[AssessmentFinding] ADD CONSTRAINT [FK_AssessmentFinding_AssessmentRunId] FOREIGN KEY ([AssessmentRunId]) REFERENCES [secureplus].[AssessmentStatus] ([AssessmentRunId]);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_AssessmentFinding_AssessmentRunId' AND object_id = OBJECT_ID(N'[secureplus].[AssessmentFinding]'))
    CREATE NONCLUSTERED INDEX [IX_AssessmentFinding_AssessmentRunId] ON [secureplus].[AssessmentFinding] ([AssessmentRunId]);
GO
