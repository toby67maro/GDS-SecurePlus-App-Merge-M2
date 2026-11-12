-- SALES <-> DEPLOYMENT BRIDGE for the Secure Plus (EaaS v3) database. Idempotent: safe to run repeatedly.
-- Purely additive: creates objects in the [secureplus] schema and never alters or drops anything the
-- Secure Plus desktop application owns. Requires the Secure Plus schema (V0001-V0012) to exist first.
--
-- Role of each piece
--   secureplus.SalesCustomerMap      sales-app customer GUID  <->  Secure Plus CustomerKey / cust.Customer
--   secureplus.SalesEngagement       one row per intake submitted in the sales form (immutable history)
--   secureplus.usp_SalesUpsertCustomer   the single write path used by the sales API
--   secureplus.vw_SalesEngagementOverview  engagement + latest assessment (management dashboard)
--   secureplus.vw_CustomerAssessmentSummary  latest assessment per customer (reporting in the form)

IF OBJECT_ID(N'[secureplus].[CustomerProfile]', N'U') IS NULL OR OBJECT_ID(N'[secureplus].[CustomerLink]', N'U') IS NULL
    THROW 51000, 'The Secure Plus schema is not installed. Start the Secure Plus desktop app (or run its V0001-V0012 scripts) before installing the sales bridge.', 1;
GO

IF OBJECT_ID(N'[secureplus].[SalesCustomerMap]', N'U') IS NULL
CREATE TABLE [secureplus].[SalesCustomerMap] (
    [SalesCustomerId] UNIQUEIDENTIFIER NOT NULL,
    [CustomerKey]     NVARCHAR(64)     NOT NULL,
    [CustomerId]      INT              NOT NULL,
    [CreatedAtUtc]    DATETIME2        NOT NULL,
    [ModifiedAtUtc]   DATETIME2        NOT NULL,
    CONSTRAINT [PK_SalesCustomerMap] PRIMARY KEY CLUSTERED ([SalesCustomerId]),
    CONSTRAINT [UQ_SalesCustomerMap_CustomerKey] UNIQUE ([CustomerKey])
);
GO

IF OBJECT_ID(N'[secureplus].[SalesEngagement]', N'U') IS NULL
CREATE TABLE [secureplus].[SalesEngagement] (
    [SalesEngagementId] UNIQUEIDENTIFIER NOT NULL,
    [SalesCustomerId]   UNIQUEIDENTIFIER NOT NULL,
    [CustomerKey]       NVARCHAR(64)     NOT NULL,
    [EngagementId]      INT              NULL,
    [Salesperson]       NVARCHAR(160)    NOT NULL,
    [OwnerId]           NVARCHAR(200)    NULL,
    [OwnerEmail]        NVARCHAR(320)    NULL,
    [MeetingDate]       DATE             NULL,
    [UserCount]         INT              NULL,
    [EndpointCount]     INT              NULL,
    [Notes]             NVARCHAR(4000)   NULL,
    [OneTimeHoursMin]   DECIMAL(9, 2)    NOT NULL,
    [OneTimeHoursMax]   DECIMAL(9, 2)    NOT NULL,
    [MonthlyHoursMin]   DECIMAL(9, 2)    NOT NULL,
    [MonthlyHoursMax]   DECIMAL(9, 2)    NOT NULL,
    [TaskCount]         INT              NOT NULL,
    [AnswersJson]       NVARCHAR(MAX)    NULL,
    [PlannerStatus]     NVARCHAR(24)     NULL,
    [SubmittedAtUtc]    DATETIME2        NOT NULL,
    [SyncedAtUtc]       DATETIME2        NOT NULL,
    CONSTRAINT [PK_SalesEngagement] PRIMARY KEY CLUSTERED ([SalesEngagementId])
);
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_SalesEngagement_CustomerKey' AND object_id = OBJECT_ID(N'[secureplus].[SalesEngagement]'))
    CREATE INDEX [IX_SalesEngagement_CustomerKey] ON [secureplus].[SalesEngagement] ([CustomerKey], [SubmittedAtUtc] DESC);
GO

CREATE OR ALTER PROCEDURE [secureplus].[usp_SalesUpsertCustomer]
    @SalesCustomerId   UNIQUEIDENTIFIER,
    @CustomerKey       NVARCHAR(64),
    @Name              NVARCHAR(200),
    @ProfileJson       NVARCHAR(MAX),
    @ExpectedVersion   INT = NULL,          -- NULL when the profile does not exist yet
    @Actor             NVARCHAR(256),
    @ContactName       NVARCHAR(200) = NULL,
    @ContactEmail      NVARCHAR(320) = NULL,
    @SalesOwner        NVARCHAR(200) = NULL,
    @SalesEngagementId UNIQUEIDENTIFIER = NULL,
    @Salesperson       NVARCHAR(160) = NULL,
    @OwnerId           NVARCHAR(200) = NULL,
    @OwnerEmail        NVARCHAR(320) = NULL,
    @MeetingDate       DATE = NULL,
    @UserCount         INT = NULL,
    @EndpointCount     INT = NULL,
    @Notes             NVARCHAR(4000) = NULL,
    @OneTimeHoursMin   DECIMAL(9, 2) = 0,
    @OneTimeHoursMax   DECIMAL(9, 2) = 0,
    @MonthlyHoursMin   DECIMAL(9, 2) = 0,
    @MonthlyHoursMax   DECIMAL(9, 2) = 0,
    @TaskCount         INT = 0,
    @AnswersJson       NVARCHAR(MAX) = NULL,
    @PlannerStatus     NVARCHAR(24) = NULL,
    @SubmittedAtUtc    DATETIME2 = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    IF ISJSON(@ProfileJson) <> 1 THROW 51002, 'ProfileJson is not valid JSON.', 1;

    DECLARE @now DATETIME2 = SYSUTCDATETIME();
    DECLARE @customerId INT, @engagementId INT, @currentVersion INT;

    BEGIN TRANSACTION;

    SELECT @customerId = CustomerId, @engagementId = EngagementId
    FROM [secureplus].[CustomerLink] WITH (UPDLOCK, HOLDLOCK) WHERE CustomerKey = @CustomerKey;

    IF @customerId IS NULL
    BEGIN
        INSERT INTO [cust].[Customer] (Name, Notes, IsActive) VALUES (@Name, N'Created from the sales intake form.', 1);
        SET @customerId = CAST(SCOPE_IDENTITY() AS INT);
        INSERT INTO [secureplus].[CustomerLink] (CustomerKey, CustomerId, EngagementId, DisplayName, IsSample, CreatedAtUtc, ModifiedAtUtc)
        VALUES (@CustomerKey, @customerId, NULL, @Name, 0, @now, @now);
    END
    ELSE
    BEGIN
        UPDATE [cust].[Customer] SET Name = @Name, IsActive = 1 WHERE CustomerId = @customerId;
        UPDATE [secureplus].[CustomerLink] SET DisplayName = @Name, ModifiedAtUtc = @now WHERE CustomerKey = @CustomerKey;
    END;

    UPDATE [cust].[Customer] SET CustomerKey = @CustomerKey, ModifiedBy = @Actor, ModifiedAtUtc = @now
    WHERE CustomerId = @customerId AND (CustomerKey IS NULL OR CustomerKey = @CustomerKey);

    IF @engagementId IS NULL
    BEGIN
        INSERT INTO [cust].[Engagement] (CustomerId, Name, EngagementTypeCode, StatusCode, StartDate, SalesOwner)
        VALUES (@customerId, LEFT(@Name + N' - Secure Plus', 200), N'SecurePlus', N'Active', CAST(@now AS DATE), @SalesOwner);
        SET @engagementId = CAST(SCOPE_IDENTITY() AS INT);
        UPDATE [secureplus].[CustomerLink] SET EngagementId = @engagementId WHERE CustomerKey = @CustomerKey;
    END
    ELSE IF @SalesOwner IS NOT NULL
        UPDATE [cust].[Engagement] SET SalesOwner = @SalesOwner WHERE EngagementId = @engagementId;

    IF @ContactName IS NOT NULL AND LTRIM(RTRIM(@ContactName)) <> N''
       AND NOT EXISTS (SELECT 1 FROM [cust].[Contact] WHERE CustomerId = @customerId
                       AND ((@ContactEmail IS NOT NULL AND Email = @ContactEmail) OR (@ContactEmail IS NULL AND Name = @ContactName)))
    BEGIN
        INSERT INTO [cust].[Contact] (CustomerId, Name, Email, RoleCode, IsDecisionOwner, IsPrimary, IsChangeApprover, SortOrder)
        VALUES (@customerId, @ContactName, @ContactEmail, N'Sales Contact', 0,
                CASE WHEN EXISTS (SELECT 1 FROM [cust].[Contact] WHERE CustomerId = @customerId AND IsPrimary = 1) THEN 0 ELSE 1 END, 0,
                (SELECT ISNULL(MAX(SortOrder), 0) + 1 FROM [cust].[Contact] WHERE CustomerId = @customerId));
    END;

    SELECT @currentVersion = [Version] FROM [secureplus].[CustomerProfile] WITH (UPDLOCK, HOLDLOCK) WHERE CustomerKey = @CustomerKey;
    IF @currentVersion IS NULL
    BEGIN
        INSERT INTO [secureplus].[CustomerProfile] (CustomerKey, Name, ProfileJson, Version, IsSample, IsArchived, CreatedBy, CreatedAtUtc, ModifiedBy, ModifiedAtUtc)
        VALUES (@CustomerKey, @Name, @ProfileJson, 1, 0, 0, @Actor, @now, @Actor, @now);
        SET @currentVersion = 1;
    END
    ELSE
    BEGIN
        IF @ExpectedVersion IS NULL OR @ExpectedVersion <> @currentVersion
            THROW 51001, 'The customer profile changed in Secure Plus while the sale was being synced. Retry.', 1;
        UPDATE [secureplus].[CustomerProfile]
        SET Name = @Name, ProfileJson = @ProfileJson, Version = Version + 1, IsArchived = 0, ModifiedBy = @Actor, ModifiedAtUtc = @now
        WHERE CustomerKey = @CustomerKey;
        SET @currentVersion = @currentVersion + 1;
    END;

    MERGE [secureplus].[SalesCustomerMap] AS target
    USING (SELECT @SalesCustomerId AS SalesCustomerId) AS source ON target.SalesCustomerId = source.SalesCustomerId
    WHEN MATCHED THEN UPDATE SET CustomerKey = @CustomerKey, CustomerId = @customerId, ModifiedAtUtc = @now
    WHEN NOT MATCHED THEN INSERT (SalesCustomerId, CustomerKey, CustomerId, CreatedAtUtc, ModifiedAtUtc)
        VALUES (@SalesCustomerId, @CustomerKey, @customerId, @now, @now);

    IF @SalesEngagementId IS NOT NULL
    BEGIN
        MERGE [secureplus].[SalesEngagement] AS target
        USING (SELECT @SalesEngagementId AS SalesEngagementId) AS source ON target.SalesEngagementId = source.SalesEngagementId
        WHEN MATCHED THEN UPDATE SET EngagementId = @engagementId, Salesperson = ISNULL(@Salesperson, target.Salesperson),
            OwnerId = @OwnerId, OwnerEmail = @OwnerEmail, MeetingDate = @MeetingDate, UserCount = @UserCount, EndpointCount = @EndpointCount,
            Notes = @Notes, OneTimeHoursMin = @OneTimeHoursMin, OneTimeHoursMax = @OneTimeHoursMax, MonthlyHoursMin = @MonthlyHoursMin,
            MonthlyHoursMax = @MonthlyHoursMax, TaskCount = @TaskCount, AnswersJson = @AnswersJson, PlannerStatus = @PlannerStatus, SyncedAtUtc = @now
        WHEN NOT MATCHED THEN INSERT (SalesEngagementId, SalesCustomerId, CustomerKey, EngagementId, Salesperson, OwnerId, OwnerEmail, MeetingDate,
            UserCount, EndpointCount, Notes, OneTimeHoursMin, OneTimeHoursMax, MonthlyHoursMin, MonthlyHoursMax, TaskCount, AnswersJson,
            PlannerStatus, SubmittedAtUtc, SyncedAtUtc)
            VALUES (@SalesEngagementId, @SalesCustomerId, @CustomerKey, @engagementId, ISNULL(@Salesperson, N''), @OwnerId, @OwnerEmail, @MeetingDate,
            @UserCount, @EndpointCount, @Notes, @OneTimeHoursMin, @OneTimeHoursMax, @MonthlyHoursMin, @MonthlyHoursMax, @TaskCount, @AnswersJson,
            @PlannerStatus, ISNULL(@SubmittedAtUtc, @now), @now);
    END;

    COMMIT TRANSACTION;

    SELECT @CustomerKey AS CustomerKey, @customerId AS CustomerId, @engagementId AS EngagementId, @currentVersion AS ProfileVersion;
END;
GO

CREATE OR ALTER VIEW [secureplus].[vw_CustomerAssessmentSummary]
AS
SELECT s.CustomerKey, s.AssessmentRunId, s.Name AS AssessmentName, s.StatusCode, s.Score, s.ControlsTotal, s.ControlsPassed,
       s.RequestedAtUtc, s.CompletedAtUtc,
       (SELECT COUNT(*) FROM [secureplus].[AssessmentResult] r WHERE r.AssessmentRunId = s.AssessmentRunId AND r.Result = N'Fail') AS ControlsFailed,
       (SELECT COUNT(*) FROM [secureplus].[AssessmentResult] r WHERE r.AssessmentRunId = s.AssessmentRunId AND r.Result = N'Partial') AS ControlsPartial,
       (SELECT COUNT(*) FROM [secureplus].[AssessmentResult] r WHERE r.AssessmentRunId = s.AssessmentRunId AND r.Result IN (N'Fail', N'Partial') AND r.Severity = N'Critical') AS CriticalGaps,
       (SELECT COUNT(*) FROM [secureplus].[AssessmentResult] r WHERE r.AssessmentRunId = s.AssessmentRunId AND r.Result IN (N'Fail', N'Partial') AND r.Severity = N'High') AS HighGaps
FROM [secureplus].[AssessmentStatus] s
WHERE s.IsSample = 0
  AND s.AssessmentRunId = (SELECT TOP 1 x.AssessmentRunId FROM [secureplus].[AssessmentStatus] x
                           WHERE x.CustomerKey = s.CustomerKey AND x.IsSample = 0
                           ORDER BY x.RequestedAtUtc DESC, x.AssessmentRunId DESC);
GO

CREATE OR ALTER VIEW [secureplus].[vw_SalesEngagementOverview]
AS
SELECT e.SalesEngagementId, e.SalesCustomerId, e.CustomerKey, l.DisplayName AS CustomerName, e.Salesperson, e.OwnerId, e.OwnerEmail,
       e.MeetingDate, e.UserCount, e.EndpointCount, e.OneTimeHoursMin, e.OneTimeHoursMax, e.MonthlyHoursMin, e.MonthlyHoursMax,
       e.TaskCount, e.PlannerStatus, e.SubmittedAtUtc, e.SyncedAtUtc, e.EngagementId,
       a.AssessmentRunId, a.StatusCode AS AssessmentStatus, a.Score AS AssessmentScore, a.CompletedAtUtc AS AssessmentCompletedAtUtc
FROM [secureplus].[SalesEngagement] e
LEFT JOIN [secureplus].[CustomerLink] l ON l.CustomerKey = e.CustomerKey
LEFT JOIN [secureplus].[vw_CustomerAssessmentSummary] a ON a.CustomerKey = e.CustomerKey;
GO
