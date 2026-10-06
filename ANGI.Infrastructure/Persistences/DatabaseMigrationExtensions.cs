using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ANGI.Infrastructure.Persistences
{
    public static class DatabaseMigrationExtensions
    {
        // Applies pending EF migrations to schema core. Called at startup in Development only;
        // other environments run migrations as a separate deploy step.
        public static async Task MigrateDatabaseAsync(this IServiceProvider services, CancellationToken ct = default)
        {
            await using var scope = services.CreateAsyncScope();
            var context = scope.ServiceProvider.GetRequiredService<ANGIContext>();
            await context.Database.MigrateAsync(ct);
        }
    }
}
