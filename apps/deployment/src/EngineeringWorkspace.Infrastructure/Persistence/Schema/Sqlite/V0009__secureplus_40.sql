-- SECURE PLUS 4.0 (SQLite, automated tests only).

CREATE TABLE IF NOT EXISTS "secureplus.Industry" ("Code" TEXT NOT NULL PRIMARY KEY, "Name" TEXT NOT NULL, "SortOrder" INTEGER NOT NULL, "IsActive" INTEGER NOT NULL DEFAULT 1,
    "IsSystem" INTEGER NOT NULL DEFAULT 0, "IsModified" INTEGER NOT NULL DEFAULT 0, "ModifiedBy" TEXT NULL, "ModifiedAtUtc" TEXT NULL);
GO
CREATE TABLE IF NOT EXISTS "secureplus.ChangeRequestTemplate" ("TemplateKey" TEXT NOT NULL PRIMARY KEY, "CustomerKey" TEXT NULL, "Name" TEXT NOT NULL, "Version" INTEGER NOT NULL,
    "SourceTemplateKey" TEXT NULL, "SourceVersion" INTEGER NULL, "IsSystem" INTEGER NOT NULL DEFAULT 0, "ContentJson" TEXT NOT NULL, "CreatedBy" TEXT NOT NULL, "CreatedAtUtc" TEXT NOT NULL,
    "ModifiedBy" TEXT NULL, "ModifiedAtUtc" TEXT NULL);
GO
ALTER TABLE "dlv.ProjectTask" ADD COLUMN "TaskKey" TEXT NULL;
GO
ALTER TABLE "dlv.ProjectTask" ADD COLUMN "SortOrder" INTEGER NULL;
GO
ALTER TABLE "dlv.ProjectTask" ADD COLUMN "Notes" TEXT NULL;
GO
ALTER TABLE "dlv.ProjectTask" ADD COLUMN "ActualFinishDate" TEXT NULL;
GO
ALTER TABLE "dlv.ProjectTask" ADD COLUMN "ChangeRequestIds" TEXT NULL;
GO
ALTER TABLE "dlv.ProjectTask" ADD COLUMN "ControlIds" TEXT NULL;
GO
ALTER TABLE "dlv.ProjectTask" ADD COLUMN "ScheduleRank" REAL NULL;
GO
ALTER TABLE "dlv.ProjectTask" ADD COLUMN "PinnedStartDate" TEXT NULL;
GO
CREATE TABLE IF NOT EXISTS "secureplus.ProjectTaskEvent" ("ProjectTaskEventId" INTEGER PRIMARY KEY AUTOINCREMENT, "ProjectPlanId" INTEGER NOT NULL, "TaskKey" TEXT NOT NULL,
    "EventType" TEXT NOT NULL, "Summary" TEXT NOT NULL, "DetailJson" TEXT NULL, "AttachmentName" TEXT NULL, "Attachment" BLOB NULL, "CreatedBy" TEXT NOT NULL, "CreatedAtUtc" TEXT NOT NULL);
GO
CREATE TRIGGER IF NOT EXISTS "secureplus_ProjectTaskEvent_NoUpdate" BEFORE UPDATE ON "secureplus.ProjectTaskEvent" BEGIN SELECT RAISE(ABORT, 'Task history is append-only.'); END;
GO
