-- SECURE PLUS EXTENSION tables for SQLite: per-control assessment results and recurring assessment schedules.
-- Repeatable: every statement is guarded.

CREATE TABLE IF NOT EXISTS "secureplus.AssessmentResult" (
    "AssessmentResultId" INTEGER PRIMARY KEY,
    "AssessmentRunId" INTEGER NOT NULL,
    "ControlId" TEXT NOT NULL,
    "Area" TEXT NOT NULL,
    "Control" TEXT NOT NULL,
    "Result" TEXT NOT NULL,
    "Severity" TEXT NOT NULL,
    "Evidence" TEXT,
    "Recommendation" TEXT,
    FOREIGN KEY ("AssessmentRunId") REFERENCES "secureplus.AssessmentStatus" ("AssessmentRunId")
);
GO
CREATE INDEX IF NOT EXISTS "IX_AssessmentResult_Run_Control" ON "secureplus.AssessmentResult" ("AssessmentRunId", "ControlId");
GO
CREATE TABLE IF NOT EXISTS "secureplus.AssessmentSchedule" (
    "ScheduleId" INTEGER PRIMARY KEY,
    "Name" TEXT NOT NULL,
    "CustomerKeys" TEXT NOT NULL,
    "Frequency" TEXT NOT NULL,
    "DayOfWeek" INTEGER,
    "DayOfMonth" INTEGER,
    "TimeOfDay" TEXT NOT NULL,
    "IsEnabled" INTEGER NOT NULL DEFAULT 1,
    "LastRunUtc" TEXT,
    "NextRunUtc" TEXT,
    "CreatedBy" TEXT NOT NULL,
    "CreatedAtUtc" TEXT NOT NULL
);
GO
