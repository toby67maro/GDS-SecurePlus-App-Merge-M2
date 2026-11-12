-- SECURE PLUS 4.0: Industry list, change request templates, project task keys, details and history. Repeatable: every statement is guarded.

IF OBJECT_ID(N'[secureplus].[Industry]', N'U') IS NULL
CREATE TABLE [secureplus].[Industry] (
    [Code] NVARCHAR(40) NOT NULL CONSTRAINT [PK_Industry] PRIMARY KEY,
    [Name] NVARCHAR(200) NOT NULL,
    [SortOrder] INT NOT NULL,
    [IsActive] BIT NOT NULL CONSTRAINT [DF_Industry_IsActive] DEFAULT (1),
    [IsSystem] BIT NOT NULL CONSTRAINT [DF_Industry_IsSystem] DEFAULT (0),
    [IsModified] BIT NOT NULL CONSTRAINT [DF_Industry_IsModified] DEFAULT (0),
    [ModifiedBy] NVARCHAR(256) NULL,
    [ModifiedAtUtc] DATETIME2 NULL);
GO
IF OBJECT_ID(N'[secureplus].[ChangeRequestTemplate]', N'U') IS NULL
CREATE TABLE [secureplus].[ChangeRequestTemplate] (
    [TemplateKey] NVARCHAR(64) NOT NULL CONSTRAINT [PK_ChangeRequestTemplate] PRIMARY KEY,
    [CustomerKey] NVARCHAR(64) NULL,
    [Name] NVARCHAR(200) NOT NULL,
    [Version] INT NOT NULL,
    [SourceTemplateKey] NVARCHAR(64) NULL,
    [SourceVersion] INT NULL,
    [IsSystem] BIT NOT NULL CONSTRAINT [DF_ChangeRequestTemplate_IsSystem] DEFAULT (0),
    [ContentJson] NVARCHAR(MAX) NOT NULL,
    [CreatedBy] NVARCHAR(256) NOT NULL,
    [CreatedAtUtc] DATETIME2 NOT NULL,
    [ModifiedBy] NVARCHAR(256) NULL,
    [ModifiedAtUtc] DATETIME2 NULL);
GO
IF COL_LENGTH(N'dlv.ProjectTask', N'TaskKey') IS NULL ALTER TABLE [dlv].[ProjectTask] ADD [TaskKey] NVARCHAR(64) NULL;
GO
IF COL_LENGTH(N'dlv.ProjectTask', N'SortOrder') IS NULL ALTER TABLE [dlv].[ProjectTask] ADD [SortOrder] INT NULL;
GO
IF COL_LENGTH(N'dlv.ProjectTask', N'Notes') IS NULL ALTER TABLE [dlv].[ProjectTask] ADD [Notes] NVARCHAR(4000) NULL;
GO
IF COL_LENGTH(N'dlv.ProjectTask', N'ActualFinishDate') IS NULL ALTER TABLE [dlv].[ProjectTask] ADD [ActualFinishDate] DATE NULL;
GO
IF COL_LENGTH(N'dlv.ProjectTask', N'ChangeRequestIds') IS NULL ALTER TABLE [dlv].[ProjectTask] ADD [ChangeRequestIds] NVARCHAR(400) NULL;
GO
IF COL_LENGTH(N'dlv.ProjectTask', N'ControlIds') IS NULL ALTER TABLE [dlv].[ProjectTask] ADD [ControlIds] NVARCHAR(MAX) NULL;
GO
IF COL_LENGTH(N'dlv.ProjectTask', N'ScheduleRank') IS NULL ALTER TABLE [dlv].[ProjectTask] ADD [ScheduleRank] FLOAT NULL;
GO
IF COL_LENGTH(N'dlv.ProjectTask', N'PinnedStartDate') IS NULL ALTER TABLE [dlv].[ProjectTask] ADD [PinnedStartDate] DATE NULL;
GO
IF OBJECT_ID(N'[secureplus].[ProjectTaskEvent]', N'U') IS NULL
CREATE TABLE [secureplus].[ProjectTaskEvent] (
    [ProjectTaskEventId] BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT [PK_ProjectTaskEvent] PRIMARY KEY,
    [ProjectPlanId] INT NOT NULL,
    [TaskKey] NVARCHAR(64) NOT NULL,
    [EventType] NVARCHAR(40) NOT NULL,
    [Summary] NVARCHAR(1000) NOT NULL,
    [DetailJson] NVARCHAR(MAX) NULL,
    [AttachmentName] NVARCHAR(260) NULL,
    [Attachment] VARBINARY(MAX) NULL,
    [CreatedBy] NVARCHAR(256) NOT NULL,
    [CreatedAtUtc] DATETIME2 NOT NULL);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_ProjectTaskEvent_Task' AND object_id = OBJECT_ID(N'[secureplus].[ProjectTaskEvent]'))
    CREATE NONCLUSTERED INDEX [IX_ProjectTaskEvent_Task] ON [secureplus].[ProjectTaskEvent] ([ProjectPlanId], [TaskKey], [CreatedAtUtc]);
GO
-- Task history is evidence: only appended.
IF DATABASE_PRINCIPAL_ID(N'secureplus_sales') IS NOT NULL
    DENY UPDATE, DELETE ON [secureplus].[ProjectTaskEvent] TO [secureplus_sales], [secureplus_engineer], [secureplus_technician], [secureplus_noc], [secureplus_lead], [secureplus_manager];
GO
