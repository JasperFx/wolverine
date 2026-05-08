using JasperFx.Events;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Wolverine.EntityFrameworkCore.Internals;

/// <summary>
/// Walks the <see cref="IServiceCollection"/> for any <see cref="DbContext"/>
/// registration that wasn't already covered by an explicit
/// <see cref="IDbContextUsageSource"/>, and registers a generic
/// <see cref="DbContextUsageSource{T}"/> for each. The result is that an
/// application using plain <c>AddDbContext&lt;TFooContext&gt;()</c> still
/// gets a snapshot in CritterWatch's Storage tab — these contexts show with
/// <c>WolverineEnabled = false</c> since their model isn't annotated.
/// </summary>
/// <remarks>
/// Runs at registration time (called from
/// <c>UseEntityFrameworkCoreTransactions</c>) rather than from a hosted
/// service: hosted services can't add DI registrations after the container
/// is built. The cost is that this only catches DbContexts registered
/// before <c>UseEntityFrameworkCoreTransactions</c> on the same
/// <see cref="IServiceCollection"/>; that's the same constraint Wolverine's
/// other discovery hooks operate under.
/// </remarks>
public static class UntrackedDbContextDiscovery
{
    public static void RegisterImplicitUsageSources(IServiceCollection services)
    {
        var alreadyCovered = new HashSet<Type>();

        // Anything already wired through AddDbContextWithWolverineIntegration
        // / AddDbContextWithWolverineManagedMultiTenancy registers an
        // IDbContextUsageSource of the matching closed-generic type — pull
        // those out so we don't double-register.
        foreach (var descriptor in services.Where(s => s.ServiceType == typeof(IDbContextUsageSource)))
        {
            var implementationType = descriptor.ImplementationType
                                     ?? descriptor.ImplementationFactory?.Method.ReturnType;
            if (implementationType is { IsGenericType: true })
            {
                alreadyCovered.Add(implementationType.GetGenericArguments()[0]);
            }
        }

        // Walk all DbContext registrations.
        var dbContextTypes = services
            .Where(s => s.ServiceType.IsClass
                        && !s.ServiceType.IsAbstract
                        && typeof(DbContext).IsAssignableFrom(s.ServiceType))
            .Select(s => s.ServiceType)
            .Distinct()
            .Where(t => !alreadyCovered.Contains(t))
            .ToArray();

        foreach (var dbContextType in dbContextTypes)
        {
            var sourceType = typeof(DbContextUsageSource<>).MakeGenericType(dbContextType);
            services.AddSingleton(typeof(IDbContextUsageSource), sourceType);
            alreadyCovered.Add(dbContextType);
        }
    }
}
