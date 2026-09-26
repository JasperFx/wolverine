using ImTools;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wolverine.EntityFrameworkCore.Internals;
using Wolverine.Runtime;

namespace Wolverine.EntityFrameworkCore;

/// <summary>
/// Factory that can build EF Core DbContext objects pointed at the correct tenant database
/// and attached to a Wolverine messaging context for full transactional outbox backed
/// message publishing
/// </summary>
public interface IDbContextOutboxFactory
{
    /// <summary>
    /// Given a tenant id, creates an EF Core DbContext enrolled in a Wolverine message context
    /// </summary>
    /// <param name="tenantId"></param>
    /// <param name="cancellationToken"></param>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    ValueTask<IDbContextOutbox<T>> CreateForTenantAsync<T>(string tenantId, CancellationToken cancellationToken) where T : DbContext;
}

public class DbContextOutboxFactory : IDbContextOutboxFactory, IDisposable
{
    private readonly IWolverineRuntime _runtime;
    private readonly IServiceScope _scope;
    private readonly IDomainEventScraper[] _scrapers;
    private ImHashMap<Type, IDbContextBuilder> _builders = ImHashMap<Type, IDbContextBuilder>.Empty;

    public DbContextOutboxFactory(IWolverineRuntime runtime)
    {
        _runtime = runtime;

        // GH-4630: CreateForTenantAsync used to hand every outbox an empty scraper array, so a
        // factory-built tenant outbox never published domain events in ANY transaction mode -- a silent
        // hole in PublishDomainEventsFromEntityFrameworkCore.
        //
        // This factory is a singleton and the outboxes it builds outlive any one request, so the
        // scrapers cannot come from an ambient scope; and resolving them straight off the root provider
        // throws outright when the host validates scopes, because
        // PublishDomainEventsFromEntityFrameworkCore() registers its OutgoingDomainEvents scraper as
        // SCOPED. One scope, owned by this factory for as long as it lives, resolves both kinds without
        // that. The scoped OutgoingDomainEvents collection it holds is not the caller's, so that
        // particular scraper publishes nothing here -- the same as before -- while the DbContext-walking
        // scrapers registered by PublishDomainEventsFromEntityFrameworkCore<T>(...) are singletons and
        // do exactly what they should against whichever tenant DbContext they are handed.
        _scope = runtime.Services.CreateScope();
        _scrapers = _scope.ServiceProvider.GetServices<IDomainEventScraper>().ToArray();
    }

    public async ValueTask<IDbContextOutbox<T>> CreateForTenantAsync<T>(string tenantId, CancellationToken cancellationToken) where T : DbContext
    {
        if (_builders.TryFind(typeof(T), out var raw) && raw is IDbContextBuilder<T> builder)
        {
            var dbContext = await builder.BuildAsync(tenantId, cancellationToken);
            return new DbContextOutbox<T>(_runtime, dbContext, _scrapers){TenantId = tenantId};
        }

        builder = _runtime.Services.GetRequiredService<IDbContextBuilder<T>>();
        _builders = _builders.AddOrUpdate(typeof(T), builder);

        var dbContext2 = await builder.BuildAsync(tenantId, cancellationToken);
        return new DbContextOutbox<T>(_runtime, dbContext2, _scrapers){TenantId = tenantId};
    }

    public void Dispose()
    {
        _scope.Dispose();
    }
}