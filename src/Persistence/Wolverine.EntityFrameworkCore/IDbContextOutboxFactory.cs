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

public class DbContextOutboxFactory : IDbContextOutboxFactory
{
    private readonly IWolverineRuntime _runtime;
    private ImHashMap<Type, IDbContextBuilder> _builders = ImHashMap<Type, IDbContextBuilder>.Empty;

    public DbContextOutboxFactory(IWolverineRuntime runtime)
    {
        _runtime = runtime;
    }

    public async ValueTask<IDbContextOutbox<T>> CreateForTenantAsync<T>(string tenantId, CancellationToken cancellationToken) where T : DbContext
    {
        if (_builders.TryFind(typeof(T), out var raw) && raw is IDbContextBuilder<T> builder)
        {
            var dbContext = await builder.BuildAsync(tenantId, cancellationToken);
            return new DbContextOutbox<T>(_runtime, dbContext){TenantId = tenantId};
        }

        builder = _runtime.Services.GetRequiredService<IDbContextBuilder<T>>();
        _builders = _builders.AddOrUpdate(typeof(T), builder);
        
        var dbContext2 = await builder.BuildAsync(tenantId, cancellationToken);
        return new DbContextOutbox<T>(_runtime, dbContext2){TenantId = tenantId};
    }
}