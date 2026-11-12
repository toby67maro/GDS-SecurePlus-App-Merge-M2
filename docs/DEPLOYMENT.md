# Deployment guide (sales app, multi-user)

1. Build: `cd apps/sales/web && npm ci && npm run build` (outputs into the API `wwwroot`), then
   `dotnet publish apps/sales/server/ArchiTech.Api -c Release -o publish`.
   Or build the container: `docker build -t gds-sales apps/..` using the root `Dockerfile`.
2. Host on IIS / Azure App Service / container. Any number of salesmen can use it concurrently; state lives in SQL Server.
3. Configure via host secrets (never commit):
   - `Database__Provider=SqlServer`, `ConnectionStrings__Default=...` (use `Encrypt=True`)
   - `Authentication__Mode=Entra`, `Authentication__TenantId`, `Authentication__ClientId`, `Authentication__ClientSecret`
   - `Authorization__Groups__Sales|Management|DeploymentEngineer|...=<Entra group object id>`
   - `Bridge__Enabled=true`, `Bridge__ConnectionString=<Secure Plus DB>`, `Bridge__ApplySchema=true` (first run only, then false)
   - Existing Planner / Power Automate / SharePoint settings as in `apps/sales/PROJECT-HANDOFF.md`
4. First run against a NON-production database to validate `database/bridge/B0001__sales_bridge.sql`.
5. Add salespeople to the `Sales` group and IT/Management to `Management`.
6. The WPF app needs no change; engineers see sales data as customer profile content.
