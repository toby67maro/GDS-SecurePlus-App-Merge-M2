-- SECURE PLUS EXTENSION tables for SQL Server 2022 / Azure SQL: per-control assessment results and recurring
-- assessment schedules. Repeatable: every statement is guarded and never drops or alters existing objects.

IF SCHEMA_ID(N'secureplus') IS NULL EXEC(N'CREATE SCHEMA [secureplus] AUTHORIZATION [dbo]');
GO
IF OBJECT_ID(N'[secureplus].[AssessmentResult]', N'U') IS NULL
CREATE TABLE [secureplus].[AssessmentResult] (
    [AssessmentResultId] BIGINT IDENTITY(1,1) NOT NULL,
    [AssessmentRunId] INT NOT NULL,
    [ControlId] NVARCHAR(100) NOT NULL,
    [Area] NVARCHAR(100) NOT NULL,
    [Control] NVARCHAR(400) NOT NULL,
    [Result] NVARCHAR(20) NOT NULL,
    [Severity] NVARCHAR(20) NOT NULL,
    [Evidence] NVARCHAR(2000) NULL,
    [Recommendation] NVARCHAR(1000) NULL,
    CONSTRAINT [PK_AssessmentResult] PRIMARY KEY CLUSTERED ([AssessmentResultId])
);
GO
IF OBJECT_ID(N'[secureplus].[FK_AssessmentResult_AssessmentRunId]', N'F') IS NULL
    ALTER TABLE [secureplus].[AssessmentResult] ADD CONSTRAINT [FK_AssessmentResult_AssessmentRunId] FOREIGN KEY ([AssessmentRunId]) REFERENCES [secureplus].[AssessmentStatus] ([AssessmentRunId]);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_AssessmentResult_Run_Control' AND object_id = OBJECT_ID(N'[secureplus].[AssessmentResult]'))
    CREATE NONCLUSTERED INDEX [IX_AssessmentResult_Run_Control] ON [secureplus].[AssessmentResult] ([AssessmentRunId], [ControlId]);
GO
IF OBJECT_ID(N'[secureplus].[AssessmentSchedule]', N'U') IS NULL
CREATE TABLE [secureplus].[AssessmentSchedule] (
    [ScheduleId] INT IDENTITY(1,1) NOT NULL,
    [Name] NVARCHAR(200) NOT NULL,
    [CustomerKeys] NVARCHAR(4000) NOT NULL,
    [Frequency] NVARCHAR(20) NOT NULL,
    [DayOfWeek] INT NULL,
    [DayOfMonth] INT NULL,
    [TimeOfDay] NVARCHAR(5) NOT NULL,
    [IsEnabled] BIT NOT NULL CONSTRAINT [DF_AssessmentSchedule_IsEnabled] DEFAULT (1),
    [LastRunUtc] DATETIME2 NULL,
    [NextRunUtc] DATETIME2 NULL,
    [CreatedBy] NVARCHAR(256) NOT NULL,
    [CreatedAtUtc] DATETIME2 NOT NULL,
    CONSTRAINT [PK_AssessmentSchedule] PRIMARY KEY CLUSTERED ([ScheduleId])
);
GO
