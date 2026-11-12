-- B0002: publish the sales form's WBS task set into the Secure Plus project planner (dlv.ProjectPlan / dlv.ProjectTask).
-- The form's 95-task WBS (ids like 0.1) is a sales-scoped catalog distinct from the 193-task Secure Plus master WBS,
-- so tasks are published as planner tasks with TaskKey = 'SALES-<id>' and no WbsTaskId link.
-- Insert-only and idempotent: tasks already present (by TaskKey) are never modified, so engineer scheduling edits survive.
IF OBJECT_ID(N'[secureplus].[usp_SalesPublishProjectPlan]', N'P') IS NOT NULL DROP PROCEDURE [secureplus].[usp_SalesPublishProjectPlan];
GO
CREATE PROCEDURE [secureplus].[usp_SalesPublishProjectPlan]
    @CustomerKey NVARCHAR(64),
    @TasksJson   NVARCHAR(MAX),   -- [{ "id", "name", "bucket", "sequence", "minHours", "maxHours" }]
    @PlanName    NVARCHAR(200) = N'Sales intake plan'
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    IF ISJSON(@TasksJson) <> 1 THROW 51004, 'TasksJson is not valid JSON.', 1;

    DECLARE @engagementId INT = (SELECT EngagementId FROM [secureplus].[CustomerLink] WHERE CustomerKey = @CustomerKey);
    IF @engagementId IS NULL THROW 51003, 'Customer has no Secure Plus engagement yet; sync the customer first.', 1;

    BEGIN TRANSACTION;

    DECLARE @planId INT = (SELECT TOP 1 ProjectPlanId FROM [dlv].[ProjectPlan] WITH (UPDLOCK, HOLDLOCK)
                           WHERE EngagementId = @engagementId AND Name = @PlanName ORDER BY ProjectPlanId);
    IF @planId IS NULL
    BEGIN
        INSERT INTO [dlv].[ProjectPlan] (EngagementId, Name, StatusCode) VALUES (@engagementId, @PlanName, N'Draft');
        SET @planId = SCOPE_IDENTITY();
    END

    INSERT INTO [dlv].[ProjectTask]
        (ProjectPlanId, OutlineNumber, Name, Bucket, EffortHours, PctComplete, IsMilestone, StatusCode, TaskKey, SortOrder, Notes)
    SELECT @planId, j.id, j.name, j.bucket, (j.minHours + j.maxHours) / 2.0, 0, 0, N'NotStarted',
           N'SALES-' + j.id, j.sequence, N'Scoped by sales intake.'
    FROM OPENJSON(@TasksJson) WITH (
            id NVARCHAR(60) '$.id', name NVARCHAR(400) '$.name', bucket NVARCHAR(200) '$.bucket',
            sequence INT '$.sequence', minHours DECIMAL(9, 2) '$.minHours', maxHours DECIMAL(9, 2) '$.maxHours') AS j
    WHERE NOT EXISTS (SELECT 1 FROM [dlv].[ProjectTask] t WHERE t.ProjectPlanId = @planId AND t.TaskKey = N'SALES-' + j.id);

    DECLARE @added INT = @@ROWCOUNT;
    COMMIT TRANSACTION;

    SELECT @planId AS ProjectPlanId, @added AS TasksAdded,
           (SELECT COUNT(*) FROM [dlv].[ProjectTask] WHERE ProjectPlanId = @planId) AS TasksTotal;
END
GO
