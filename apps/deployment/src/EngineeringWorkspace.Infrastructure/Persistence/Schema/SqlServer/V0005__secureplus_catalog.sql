-- SECURE PLUS EXTENSION table for SQL Server 2022 / Azure SQL: the control catalog as one flat, filterable table.
-- The catalog itself is loaded into the EaaS v3 tables (pol.Repository/RepositoryVersion/PolicyType/Category/Artifact/
-- ArtifactSetting/PolicySet/PolicySetMember/PolicySetInclude/ArtifactMapping, fw.Framework/FrameworkVersion/
-- Requirement, gds.Standard/StandardItem); this table keeps the full control ID and the descriptive columns
-- (experience, pain points, frameworks, policy sets) and links each control to its pol.Artifact row.
-- Repeatable: every statement is guarded and never drops or alters existing objects.

IF SCHEMA_ID(N'secureplus') IS NULL EXEC(N'CREATE SCHEMA [secureplus] AUTHORIZATION [dbo]');
GO
IF OBJECT_ID(N'[secureplus].[CatalogControl]', N'U') IS NULL
CREATE TABLE [secureplus].[CatalogControl] (
    [ControlId] NVARCHAR(150) NOT NULL,
    [ArtifactId] INT NULL,
    [CatalogVersion] NVARCHAR(50) NOT NULL,
    [Number] NVARCHAR(20) NULL,
    [Kind] NVARCHAR(30) NOT NULL,
    [KindName] NVARCHAR(60) NULL,
    [Category] NVARCHAR(200) NULL,
    [Platform] NVARCHAR(100) NULL,
    [Name] NVARCHAR(400) NOT NULL,
    [Setting] NVARCHAR(400) NULL,
    [Value] NVARCHAR(MAX) NULL,
    [Description] NVARCHAR(MAX) NULL,
    [Source] NVARCHAR(200) NULL,
    [Frameworks] NVARCHAR(MAX) NULL,
    [Severity] NVARCHAR(20) NULL,
    [UserImpact] NVARCHAR(50) NULL,
    [Experience] NVARCHAR(400) NULL,
    [PainPoints] NVARCHAR(1000) NULL,
    [Licensing] NVARCHAR(400) NULL,
    [Evaluation] NVARCHAR(30) NULL,
    [IsAutomatic] BIT NULL,
    [PolicySets] NVARCHAR(1000) NULL,
    [Path] NVARCHAR(800) NULL,
    [GraphResource] NVARCHAR(200) NULL,
    [OdataType] NVARCHAR(200) NULL,
    [SyncedAtUtc] DATETIME2 NULL,
    CONSTRAINT [PK_CatalogControl] PRIMARY KEY CLUSTERED ([ControlId])
);
GO
IF OBJECT_ID(N'[secureplus].[FK_CatalogControl_ArtifactId]', N'F') IS NULL
    ALTER TABLE [secureplus].[CatalogControl] ADD CONSTRAINT [FK_CatalogControl_ArtifactId] FOREIGN KEY ([ArtifactId]) REFERENCES [pol].[Artifact] ([ArtifactId]);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_CatalogControl_Kind' AND object_id = OBJECT_ID(N'[secureplus].[CatalogControl]'))
    CREATE NONCLUSTERED INDEX [IX_CatalogControl_Kind] ON [secureplus].[CatalogControl] ([Kind], [Category]);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_CatalogControl_ArtifactId' AND object_id = OBJECT_ID(N'[secureplus].[CatalogControl]'))
    CREATE NONCLUSTERED INDEX [IX_CatalogControl_ArtifactId] ON [secureplus].[CatalogControl] ([ArtifactId]);
GO
