using JasperFx.Events;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace Wolverine.EntityFrameworkCore.Internals;

/// <summary>
/// Probes a registered <see cref="IDbContextUsageSource"/> for its target
/// database's pending migrations. Runs in response to a
/// <see cref="RequestPendingMigrations"/> message; see
/// <see cref="PendingMigrationsReported"/> for the result shape.
/// </summary>
/// <remarks>
/// Resolves the matching <see cref="IDbContextUsageSource"/> by URI, opens
/// a fresh request scope, builds a <see cref="DbContext"/>, and calls
/// <c>Database.GetPendingMigrationsAsync</c>. Exceptions are caught and
/// surfaced through <see cref="PendingMigrationsReported.Error"/> so the UI
/// can render a meaningful error rather than failing the round-trip.
/// </remarks>
public static class PendingMigrationsProbe
{
    public static async Task<PendingMigrationsReported> RunAsync(
        IServiceProvider services,
        Uri subjectUri,
        CancellationToken token)
    {
        try
        {
            var source = services
                .GetServices<IDbContextUsageSource>()
                .FirstOrDefault(s => s.Subject == subjectUri);

            if (source == null)
            {
                return new PendingMigrationsReported(
                    subjectUri,
                    Array.Empty<string>(),
                    DateTimeOffset.UtcNow,
                    $"No IDbContextUsageSource registered for subject '{subjectUri}'");
            }

            // Resolve the matching DbContext. The single-DB and tenanted
            // sources both expose their DbContext type via the closed-
            // generic type argument on the source; use reflection so this
            // stays a non-generic API entry point.
            var sourceType = source.GetType();
            if (!sourceType.IsGenericType)
            {
                return new PendingMigrationsReported(
                    subjectUri,
                    Array.Empty<string>(),
                    DateTimeOffset.UtcNow,
                    $"Source type {sourceType.Name} doesn't expose a DbContext type argument");
            }

            var dbContextType = sourceType.GetGenericArguments()[0];

            using var scope = services.CreateScope();
            var dbContext = scope.ServiceProvider.GetService(dbContextType) as DbContext;
            if (dbContext == null)
            {
                return new PendingMigrationsReported(
                    subjectUri,
                    Array.Empty<string>(),
                    DateTimeOffset.UtcNow,
                    $"Could not resolve {dbContextType.Name} from the request scope");
            }

            var pending = await dbContext.Database.GetPendingMigrationsAsync(token);
            return new PendingMigrationsReported(
                subjectUri,
                pending.ToArray(),
                DateTimeOffset.UtcNow,
                Error: null);
        }
        catch (Exception ex)
        {
            return new PendingMigrationsReported(
                subjectUri,
                Array.Empty<string>(),
                DateTimeOffset.UtcNow,
                ex.Message);
        }
    }
}
