using System.Reflection;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;
using JasperFx.Descriptors;
using Microsoft.EntityFrameworkCore;
using Wolverine.Runtime;

namespace Wolverine.EntityFrameworkCore.Internals;

public interface IDbContextBuilder
{
    DbContext BuildForMain();
    Type DbContextType { get; }
    
    /// <summary>
    /// Migrates the underlying database to the current configuration of the DbContext through
    /// EF Core, but skips any migrations
    /// </summary>
    /// <returns></returns>
    Task ApplyAllChangesToDatabasesAsync();
    
    Task EnsureAllDatabasesAreCreatedAsync();

    Task<IReadOnlyList<DbContext>> FindAllAsync();
    
    DatabaseCardinality Cardinality { get; }
}

public interface IDbContextBuilder<T> : IDbContextBuilder where T : DbContext
{
    ValueTask<T> BuildAndEnrollAsync(MessageContext messaging, CancellationToken cancellationToken);
    
    ValueTask<T> BuildAsync(string tenantId, CancellationToken cancellationToken);
    
    ValueTask<T> BuildAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Builds the DbContext for the message's tenant without enlisting the <see cref="MessageContext" /> in an
    /// outbox transaction, unlike <see cref="BuildAndEnrollAsync" />.
    /// </summary>
    ValueTask<T> BuildForTenantAsync(MessageContext messaging, CancellationToken cancellationToken)
    {
        return BuildAsync(messaging.TenantId!, cancellationToken);
    }

    DbContextOptions<T> BuildOptionsForMain();
    
}

internal class CreateTenantedDbContext<T> : MethodCall where T : DbContext
{
    public CreateTenantedDbContext() : base(typeof(IDbContextBuilder<T>), ReflectionHelper.GetMethod<IDbContextBuilder<T>>(x => x.BuildAndEnrollAsync(null!, CancellationToken.None))!)
    {
    }
}

internal class BuildTenantedDbContext<T> : MethodCall where T : DbContext
{
    public BuildTenantedDbContext() : base(typeof(IDbContextBuilder<T>), ReflectionHelper.GetMethod<IDbContextBuilder<T>>(x => x.BuildForTenantAsync(null!, CancellationToken.None))!)
    {
    }
}

// Only reached by non-transactional chains: the transactional middleware inserts its own enlisting
// CreateTenantedDbContext<T>, and nothing would commit a transaction this enlisted in
internal class TenantedDbContextSource<T> : IVariableSource where T : DbContext
{
    public bool Matches(Type type)
    {
        return type == typeof(T);
    }

    public Variable Create(Type type)
    {
        return new BuildTenantedDbContext<T>().ReturnVariable!;
    }
}
