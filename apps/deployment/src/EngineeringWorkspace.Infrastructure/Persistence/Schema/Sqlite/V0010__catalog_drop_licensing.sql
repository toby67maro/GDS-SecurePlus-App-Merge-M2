-- Secure Plus 4.0: the control catalogue no longer stores free-text licensing on each control.
ALTER TABLE "secureplus.CatalogControl" DROP COLUMN "Licensing";
