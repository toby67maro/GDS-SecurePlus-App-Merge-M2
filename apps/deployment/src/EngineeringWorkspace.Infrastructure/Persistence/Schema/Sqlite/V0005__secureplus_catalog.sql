-- SECURE PLUS EXTENSION table for SQLite: the control catalog as one flat, filterable table linked to pol.Artifact.
-- Repeatable: every statement is guarded.

CREATE TABLE IF NOT EXISTS "secureplus.CatalogControl" (
    "ControlId" TEXT PRIMARY KEY,
    "ArtifactId" INTEGER,
    "CatalogVersion" TEXT NOT NULL,
    "Number" TEXT,
    "Kind" TEXT NOT NULL,
    "KindName" TEXT,
    "Category" TEXT,
    "Platform" TEXT,
    "Name" TEXT NOT NULL,
    "Setting" TEXT,
    "Value" TEXT,
    "Description" TEXT,
    "Source" TEXT,
    "Frameworks" TEXT,
    "Severity" TEXT,
    "UserImpact" TEXT,
    "Experience" TEXT,
    "PainPoints" TEXT,
    "Licensing" TEXT,
    "Evaluation" TEXT,
    "IsAutomatic" INTEGER,
    "PolicySets" TEXT,
    "Path" TEXT,
    "GraphResource" TEXT,
    "OdataType" TEXT,
    "SyncedAtUtc" TEXT,
    FOREIGN KEY ("ArtifactId") REFERENCES "pol.Artifact" ("ArtifactId")
);
GO
CREATE INDEX IF NOT EXISTS "IX_CatalogControl_Kind" ON "secureplus.CatalogControl" ("Kind", "Category");
GO
CREATE INDEX IF NOT EXISTS "IX_CatalogControl_ArtifactId" ON "secureplus.CatalogControl" ("ArtifactId");
GO
