-- SECURE PLUS 4.0 beta.2: Lead edits of the control catalogue (SQLite, automated tests only).

CREATE TABLE IF NOT EXISTS "secureplus.CatalogControlEdit" ("ControlId" TEXT NOT NULL PRIMARY KEY, "BasedOnControlId" TEXT NULL, "IsHidden" INTEGER NOT NULL DEFAULT 0,
    "ContentJson" TEXT NULL, "BaseHash" TEXT NULL, "Version" INTEGER NOT NULL, "CreatedBy" TEXT NOT NULL, "CreatedAtUtc" TEXT NOT NULL, "ModifiedBy" TEXT NOT NULL,
    "ModifiedAtUtc" TEXT NOT NULL);
GO
CREATE TABLE IF NOT EXISTS "secureplus.CatalogPolicySetEdit" ("SetId" TEXT NOT NULL PRIMARY KEY, "BasedOnSetId" TEXT NULL, "IsHidden" INTEGER NOT NULL DEFAULT 0,
    "DisplayName" TEXT NOT NULL, "Description" TEXT NULL, "DeploymentOrder" INTEGER NOT NULL, "ControlIdsJson" TEXT NOT NULL, "IncludesJson" TEXT NULL, "BaseHash" TEXT NULL,
    "Version" INTEGER NOT NULL, "CreatedBy" TEXT NOT NULL, "CreatedAtUtc" TEXT NOT NULL, "ModifiedBy" TEXT NOT NULL, "ModifiedAtUtc" TEXT NOT NULL);
GO
CREATE TABLE IF NOT EXISTS "secureplus.CatalogEditHistory" ("CatalogEditHistoryId" INTEGER PRIMARY KEY AUTOINCREMENT, "ItemType" TEXT NOT NULL, "ItemId" TEXT NOT NULL,
    "Action" TEXT NOT NULL, "BeforeJson" TEXT NULL, "AfterJson" TEXT NULL, "ChangedBy" TEXT NOT NULL, "ChangedAtUtc" TEXT NOT NULL);
GO
CREATE TRIGGER IF NOT EXISTS "secureplus_CatalogEditHistory_NoUpdate" BEFORE UPDATE ON "secureplus.CatalogEditHistory" BEGIN SELECT RAISE(ABORT, 'Catalogue edit history is append-only.'); END;
GO
CREATE TRIGGER IF NOT EXISTS "secureplus_CatalogEditHistory_NoDelete" BEFORE DELETE ON "secureplus.CatalogEditHistory" BEGIN SELECT RAISE(ABORT, 'Catalogue edit history is append-only.'); END;
GO
