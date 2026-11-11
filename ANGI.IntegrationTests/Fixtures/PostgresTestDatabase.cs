using ANGI.Infrastructure.Persistences;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace ANGI.IntegrationTests.Fixtures;

/// <summary>Creates ANGIContext instances outside the web host for repository-level PostgreSQL tests.</summary>
public static class PostgresTestDatabase
{
    /// <summary>
    /// Uses the provider options of AddInfrastructure, above all the migration history in core. With the default
    /// (public) history table, MigrateAsync after PostgresApiFactory found no history and re-ran InitialCreate on the
    /// existing tables ("relation already exists").
    /// </summary>
    public static ANGIContext CreateContext(string connectionString, TimeProvider timeProvider)
    {
        var options = new DbContextOptionsBuilder<ANGIContext>()
            .UseNpgsql(connectionString, npgsql => npgsql.MigrationsHistoryTable("__EFMigrationsHistory", ANGIContext.Schema))
            .UseSnakeCaseNamingConvention()
            .Options;
        return new ANGIContext(options, timeProvider);
    }
}

/// <summary>Guards repository-level PostgreSQL tests from running against an unapproved database.</summary>
public sealed class PostgresIntegrationFactAttribute : FactAttribute
{
    public const string ConnectionStringVariable = PostgresApiFactory.ConnectionStringVariable;

    /// <summary>Enables the test only when a dedicated PostgreSQL test database is explicitly configured.</summary>
    public PostgresIntegrationFactAttribute()
    {
        var connectionString = Environment.GetEnvironmentVariable(ConnectionStringVariable);
        if (Environment.GetEnvironmentVariable(PostgresApiFactory.RunVariable) != "1" ||
            string.IsNullOrWhiteSpace(connectionString))
        {
            Skip = $"Set {PostgresApiFactory.RunVariable}=1 and {ConnectionStringVariable} to a dedicated test database.";
            return;
        }

        var database = new NpgsqlConnectionStringBuilder(connectionString).Database;
        if (string.IsNullOrWhiteSpace(database) ||
            !database.Contains("test", StringComparison.OrdinalIgnoreCase))
        {
            Skip = "The PostgreSQL integration database name must contain 'test' to protect development data.";
        }
    }
}
