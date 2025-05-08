using ImTools;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Wolverine.Runtime;

namespace Wolverine.EntityFrameworkCore.Internals;

public class EfCoreDbContextFactory
{
    private readonly IServiceProvider _services;
    private readonly IServiceContainer _container;
    private ImHashMap<Type, object> _builders = ImHashMap<Type, object>.Empty;

    public EfCoreDbContextFactory(IServiceContainer container)
    {
        _services = container.Services;
        _container = container;
    }

    public bool CanApply(Type dbContextType)
    {
        throw new NotImplementedException();
    }

    public ValueTask<T> BuildAsync<T>(MessageContext context, CancellationToken cancellationToken)
        where T : DbContext
    {
        return findBuilder<T>().BuildAndEnrollAsync(context, cancellationToken);
    }

    private IDbContextBuilder<T> findBuilder<T>() where T : DbContext
    {
        if (_builders.TryFind(typeof(T), out var raw))
        {
            if (raw is IDbContextBuilder<T> builder) return builder;
        }
        
        // If the DbContextOptions<T> is a singleton, resolve that. Then check if IsWolverineEnabled
        // If the DbContextOptions<T> is container scoped, then just bummer

        var newBuilder = _services.GetRequiredService<IDbContextBuilder<T>>();
        _builders = _builders.AddOrUpdate(typeof(T), newBuilder);
        return newBuilder;
    }
    
    
    // Only really use when multi-tenanted
    public DbContextOptions<TContext> CreateDbContextOptions<TContext>(
        Action<IServiceProvider, DbContextOptionsBuilder>? optionsAction)
        where TContext : DbContext
    {
        var builder = new DbContextOptionsBuilder<TContext>(
            new DbContextOptions<TContext>(new Dictionary<Type, IDbContextOptionsExtension>()));

        builder.UseApplicationServiceProvider(_services);

        optionsAction?.Invoke(_services, builder);

        return builder.Options;
    }
}