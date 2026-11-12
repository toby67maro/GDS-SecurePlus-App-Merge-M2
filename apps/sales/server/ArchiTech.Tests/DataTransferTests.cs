using ArchiTech.Api.Data;
using ArchiTech.Api.Domain;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace ArchiTech.Tests;

public sealed class DataTransferTests
{
    [Fact]
    public void Conversion_and_hash_preserve_provider_values()
    {
        var id = Guid.NewGuid();
        var utc = DateTime.SpecifyKind(new DateTime(2026, 11, 6, 15, 30, 0), DateTimeKind.Utc);
        object[] source =
        [
            Transfer.ConvertValue(id.ToString(), "uniqueidentifier"),
            Transfer.ConvertValue("12.50", "decimal"),
            Transfer.ConvertValue(1L, "bit"),
            Transfer.ConvertValue(42L, "int"),
            Transfer.ConvertValue(5.25, "float"),
            Transfer.ConvertValue(utc.ToString("O"), "datetime2"),
            DBNull.Value
        ];
        object[] destination = [id, 12.5m, true, 42, 5.25, DateTime.SpecifyKind(utc, DateTimeKind.Unspecified), DBNull.Value];

        Assert.Equal(Transfer.Hash(source), Transfer.Hash(destination));
        destination[3] = 43;
        Assert.NotEqual(Transfer.Hash(source), Transfer.Hash(destination));
        Assert.Throws<InvalidOperationException>(() => Transfer.ConvertValue(2L, "bit"));
        Assert.Throws<InvalidOperationException>(() => Transfer.ConvertValue("x", "unsupported"));
    }

    [Fact]
    public async Task Deleted_sqlite_identity_values_are_not_silently_reused()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE Example (Id INTEGER PRIMARY KEY AUTOINCREMENT); INSERT INTO Example DEFAULT VALUES;";
        await command.ExecuteNonQueryAsync();
        await Transfer.ValidateSourceSequences(connection);
        command.CommandText = "DELETE FROM Example;";
        await command.ExecuteNonQueryAsync();
        await Assert.ThrowsAsync<Transfer.TransferFailure>(() => Transfer.ValidateSourceSequences(connection));
    }

    [Fact]
    public void Foreign_keys_are_ordered_and_cycles_rejected()
    {
        var parent = new Transfer.Table("Parent");
        var child = new Transfer.Table("Child");
        child.Dependencies.Add(parent.Name);
        Assert.Equal(["Parent", "Child"], Transfer.OrderTables([child, parent]).Select(t => t.Name));
        parent.Dependencies.Add(child.Name);
        Assert.Throws<InvalidOperationException>(() => Transfer.OrderTables([child, parent]));
    }

    [Fact]
    public async Task Backup_checksum_is_checked_before_connecting()
    {
        var path = Path.Combine(Path.GetTempPath(), $"architech-transfer-test-{Guid.NewGuid():N}.db");
        try
        {
            await File.WriteAllTextAsync(path, "not a database");
            await Assert.ThrowsAsync<Transfer.TransferFailure>(() => Transfer.RunAsync(
                path, "Server=invalid;Database=unused;Integrated Security=true", "invalid", false));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Seeded_target_model_values_fit_the_configured_sql_limit()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();
        await StandardsCatalogSeeder.SeedAsync(db, Path.Combine(AppContext.BaseDirectory, "Data", "standards"));

        var limit = db.Model.FindEntityType(typeof(TargetModelTable))!
            .FindProperty(nameof(TargetModelTable.EstimatedRows))!.GetMaxLength();
        Assert.NotNull(limit);
        var values = await db.TargetModelTables.Select(item => item.EstimatedRows).ToListAsync();
        Assert.NotEmpty(values);
        Assert.All(values, value => Assert.True(value.Length <= limit));
    }
}
