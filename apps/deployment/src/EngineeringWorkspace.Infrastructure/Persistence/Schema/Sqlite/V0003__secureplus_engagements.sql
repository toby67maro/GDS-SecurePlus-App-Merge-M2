-- SECURE PLUS EXTENSION tables for SQLite: customer, workspace and assessment history.
-- See the SQL Server script for the rationale. Repeatable: every statement is guarded.

CREATE TABLE IF NOT EXISTS "secureplus.CustomerLink" (
    "CustomerKey" TEXT NOT NULL PRIMARY KEY,
    "CustomerId" INTEGER NOT NULL,
    "EngagementId" INTEGER,
    "DisplayName" TEXT NOT NULL,
    "IsSample" INTEGER NOT NULL DEFAULT 0,
    "CreatedAtUtc" TEXT NOT NULL,
    "ModifiedAtUtc" TEXT NOT NULL,
    FOREIGN KEY ("CustomerId") REFERENCES "cust.Customer" ("CustomerId"),
    FOREIGN KEY ("EngagementId") REFERENCES "cust.Engagement" ("EngagementId")
);
GO
CREATE TABLE IF NOT EXISTS "secureplus.AssessmentStatus" (
    "AssessmentRunId" INTEGER NOT NULL PRIMARY KEY,
    "CustomerKey" TEXT NOT NULL,
    "WorkspaceId" TEXT,
    "Name" TEXT NOT NULL,
    "StatusCode" TEXT NOT NULL,
    "Score" REAL,
    "ControlsTotal" INTEGER,
    "ControlsPassed" INTEGER,
    "RequestedAtUtc" TEXT NOT NULL,
    "CompletedAtUtc" TEXT,
    "IsSample" INTEGER NOT NULL DEFAULT 0,
    FOREIGN KEY ("AssessmentRunId") REFERENCES "cust.AssessmentRun" ("AssessmentRunId"),
    FOREIGN KEY ("CustomerKey") REFERENCES "secureplus.CustomerLink" ("CustomerKey")
);
GO
CREATE INDEX IF NOT EXISTS "IX_AssessmentStatus_CustomerKey" ON "secureplus.AssessmentStatus" ("CustomerKey", "RequestedAtUtc");
GO
CREATE TABLE IF NOT EXISTS "secureplus.AssessmentFinding" (
    "AssessmentFindingId" INTEGER PRIMARY KEY,
    "AssessmentRunId" INTEGER NOT NULL,
    "Area" TEXT NOT NULL,
    "Control" TEXT NOT NULL,
    "Result" TEXT NOT NULL,
    "Severity" TEXT NOT NULL,
    "Recommendation" TEXT,
    FOREIGN KEY ("AssessmentRunId") REFERENCES "secureplus.AssessmentStatus" ("AssessmentRunId")
);
GO
CREATE INDEX IF NOT EXISTS "IX_AssessmentFinding_AssessmentRunId" ON "secureplus.AssessmentFinding" ("AssessmentRunId");
GO
