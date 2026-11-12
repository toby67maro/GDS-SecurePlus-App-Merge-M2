-- SECURE PLUS 4.0 beta.2: Lead edits of the control catalogue, kept apart from the GDS catalogue tables so a catalogue reload never
-- deletes or overwrites them. Repeatable: every statement is guarded.

IF OBJECT_ID(N'[secureplus].[CatalogControlEdit]', N'U') IS NULL
CREATE TABLE [secureplus].[CatalogControlEdit] (
    [ControlId] NVARCHAR(150) NOT NULL CONSTRAINT [PK_CatalogControlEdit] PRIMARY KEY,
    [BasedOnControlId] NVARCHAR(150) NULL,
    [IsHidden] BIT NOT NULL CONSTRAINT [DF_CatalogControlEdit_IsHidden] DEFAULT (0),
    [ContentJson] NVARCHAR(MAX) NULL,
    [BaseHash] NVARCHAR(64) NULL,
    [Version] INT NOT NULL,
    [CreatedBy] NVARCHAR(256) NOT NULL,
    [CreatedAtUtc] DATETIME2 NOT NULL,
    [ModifiedBy] NVARCHAR(256) NOT NULL,
    [ModifiedAtUtc] DATETIME2 NOT NULL);
GO
IF OBJECT_ID(N'[secureplus].[CatalogPolicySetEdit]', N'U') IS NULL
CREATE TABLE [secureplus].[CatalogPolicySetEdit] (
    [SetId] NVARCHAR(100) NOT NULL CONSTRAINT [PK_CatalogPolicySetEdit] PRIMARY KEY,
    [BasedOnSetId] NVARCHAR(100) NULL,
    [IsHidden] BIT NOT NULL CONSTRAINT [DF_CatalogPolicySetEdit_IsHidden] DEFAULT (0),
    [DisplayName] NVARCHAR(200) NOT NULL,
    [Description] NVARCHAR(2000) NULL,
    [DeploymentOrder] INT NOT NULL,
    [ControlIdsJson] NVARCHAR(MAX) NOT NULL,
    [IncludesJson] NVARCHAR(MAX) NULL,
    [BaseHash] NVARCHAR(64) NULL,
    [Version] INT NOT NULL,
    [CreatedBy] NVARCHAR(256) NOT NULL,
    [CreatedAtUtc] DATETIME2 NOT NULL,
    [ModifiedBy] NVARCHAR(256) NOT NULL,
    [ModifiedAtUtc] DATETIME2 NOT NULL);
GO
IF OBJECT_ID(N'[secureplus].[CatalogEditHistory]', N'U') IS NULL
CREATE TABLE [secureplus].[CatalogEditHistory] (
    [CatalogEditHistoryId] BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT [PK_CatalogEditHistory] PRIMARY KEY,
    [ItemType] NVARCHAR(20) NOT NULL,
    [ItemId] NVARCHAR(150) NOT NULL,
    [Action] NVARCHAR(20) NOT NULL,
    [BeforeJson] NVARCHAR(MAX) NULL,
    [AfterJson] NVARCHAR(MAX) NULL,
    [ChangedBy] NVARCHAR(256) NOT NULL,
    [ChangedAtUtc] DATETIME2 NOT NULL);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_CatalogEditHistory_Item' AND object_id = OBJECT_ID(N'[secureplus].[CatalogEditHistory]'))
    CREATE NONCLUSTERED INDEX [IX_CatalogEditHistory_Item] ON [secureplus].[CatalogEditHistory] ([ItemId], [ChangedAtUtc]);
GO
-- Edit history is append-only for every Secure Plus role.
IF DATABASE_PRINCIPAL_ID(N'secureplus_sales') IS NOT NULL DENY UPDATE, DELETE ON [secureplus].[CatalogEditHistory] TO [secureplus_sales];
GO
IF DATABASE_PRINCIPAL_ID(N'secureplus_engineer') IS NOT NULL DENY UPDATE, DELETE ON [secureplus].[CatalogEditHistory] TO [secureplus_engineer];
GO
IF DATABASE_PRINCIPAL_ID(N'secureplus_technician') IS NOT NULL DENY UPDATE, DELETE ON [secureplus].[CatalogEditHistory] TO [secureplus_technician];
GO
IF DATABASE_PRINCIPAL_ID(N'secureplus_noc') IS NOT NULL DENY UPDATE, DELETE ON [secureplus].[CatalogEditHistory] TO [secureplus_noc];
GO
IF DATABASE_PRINCIPAL_ID(N'secureplus_lead') IS NOT NULL DENY UPDATE, DELETE ON [secureplus].[CatalogEditHistory] TO [secureplus_lead];
GO
IF DATABASE_PRINCIPAL_ID(N'secureplus_manager') IS NOT NULL DENY UPDATE, DELETE ON [secureplus].[CatalogEditHistory] TO [secureplus_manager];
GO
