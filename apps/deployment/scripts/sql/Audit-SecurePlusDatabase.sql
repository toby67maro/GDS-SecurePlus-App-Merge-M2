/*
    Secure Plus – Modern Work: read-only audit of the SecurePlus database.
    Run in SQL Server Management Studio (or sqlcmd) against the SecurePlus database:
        sqlcmd -S localhost -d SecurePlus -E -i Audit-SecurePlusDatabase.sql -o SecurePlus-audit.txt
    Nothing is changed. Needs only SELECT permission (and VIEW DEFINITION on the metadata).

    Result sets:
      1. Schema version and seed datasets applied.
      2. Rows per table, grouped by schema.
      3. Empty tables in the schemas Secure Plus fills (cust, secureplus, pol, ref, svc, sec, eux, audit, deploy).
      4. Column fill rate of the customer tables (how many rows have a value in each column).
*/
SET NOCOUNT ON;

-- 1. Schema and seed versions.
IF OBJECT_ID(N'secureplus.SchemaVersion') IS NOT NULL
    SELECT TOP (20) * FROM secureplus.SchemaVersion ORDER BY 1 DESC;
IF OBJECT_ID(N'secureplus.SeedVersion') IS NOT NULL
    SELECT Dataset, Version, RowsApplied, AppliedAtUtc, AppliedBy FROM secureplus.SeedVersion ORDER BY Dataset;

-- 2. Rows per table.
SELECT s.name AS SchemaName, t.name AS TableName, SUM(p.rows) AS [Rows]
FROM sys.tables AS t
JOIN sys.schemas AS s ON s.schema_id = t.schema_id
JOIN sys.partitions AS p ON p.object_id = t.object_id AND p.index_id IN (0, 1)
WHERE t.is_ms_shipped = 0 AND t.temporal_type <> 1
GROUP BY s.name, t.name
ORDER BY s.name, t.name;

-- 3. Empty tables in the schemas Secure Plus writes or seeds.
SELECT s.name AS SchemaName, t.name AS TableName
FROM sys.tables AS t
JOIN sys.schemas AS s ON s.schema_id = t.schema_id
JOIN sys.partitions AS p ON p.object_id = t.object_id AND p.index_id IN (0, 1)
WHERE t.is_ms_shipped = 0 AND t.temporal_type <> 1
  AND s.name IN (N'cust', N'secureplus', N'pol', N'ref', N'svc', N'sec', N'eux', N'audit', N'deploy')
GROUP BY s.name, t.name
HAVING SUM(p.rows) = 0
ORDER BY s.name, t.name;

-- 4. Column fill rate of the customer tables.
DECLARE @sql NVARCHAR(MAX) = N'';
SELECT @sql = @sql + CASE WHEN @sql = N'' THEN N'' ELSE N' UNION ALL ' END +
    N'SELECT N''' + s.name + N'.' + t.name + N''' AS TableName, N''' + c.name + N''' AS ColumnName, COUNT(*) AS [Rows], COUNT(' + QUOTENAME(c.name) +
    N') AS RowsWithValue FROM ' + QUOTENAME(s.name) + N'.' + QUOTENAME(t.name)
FROM sys.columns AS c
JOIN sys.tables AS t ON t.object_id = c.object_id
JOIN sys.schemas AS s ON s.schema_id = t.schema_id
WHERE s.name = N'cust' AND t.name IN (N'Customer', N'Contact', N'Tenant', N'Engagement', N'TenantLicense', N'Exception')
  AND TYPE_NAME(c.user_type_id) NOT IN (N'text', N'ntext', N'image', N'xml')
ORDER BY t.name, c.column_id;
IF @sql <> N''
BEGIN
    SET @sql = N'SELECT TableName, ColumnName, [Rows], RowsWithValue, CASE WHEN [Rows] = 0 THEN NULL ELSE CAST(100.0 * RowsWithValue / [Rows] AS DECIMAL(5, 1)) END AS PercentFilled FROM (' + @sql + N') AS fill ORDER BY TableName, PercentFilled;';
    EXEC sys.sp_executesql @sql;
END
