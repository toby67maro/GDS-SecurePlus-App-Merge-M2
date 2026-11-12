-- Secure Plus 4.0: the control catalogue no longer stores free-text licensing on each control.
-- Licensing comes from the knowledge base (SKUs, capabilities and the capabilities each requirement needs).
IF COL_LENGTH('secureplus.CatalogControl', 'Licensing') IS NOT NULL
    ALTER TABLE [secureplus].[CatalogControl] DROP COLUMN [Licensing];
GO
