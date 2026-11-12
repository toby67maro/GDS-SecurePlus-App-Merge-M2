using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace ArchiTech.Api.Data;

public sealed class AppDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        var provider = Environment.GetEnvironmentVariable("Database__Provider") ?? "SqlServer";
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__Default");
        var options = new DbContextOptionsBuilder<AppDbContext>();

        if (provider.Equals("Sqlite", StringComparison.OrdinalIgnoreCase))
        {
            options.UseSqlite(connectionString ?? "Data Source=architech-design-time.db");
        }
        else if (provider.Equals("SqlServer", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                throw new InvalidOperationException(
                    "Set ConnectionStrings__Default for SQL Server migration commands. No local SQL service is assumed.");
            }

            options.UseSqlServer(connectionString);
        }
        else
        {
            throw new InvalidOperationException(
                $"Unsupported Database__Provider '{provider}'. Use Sqlite or SqlServer.");
        }

        return new AppDbContext(options.Options);
    }
}
