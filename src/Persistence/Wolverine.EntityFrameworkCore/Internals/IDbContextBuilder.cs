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
    /// Builds the DbContext for the message's tenant WITHOUT enlisting the <see cref="MessageContext" /> in an EF
    /// Core outbox transaction, unlike <see cref="BuildAndEnrollAsync" />. For a chain that takes the DbContext but
    /// is not transactional: nothing would commit a transaction it was enlisted in, so every message it sent or
    /// scheduled would be written into that transaction and silently dropped.
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

/// <summary>
///     The non-enlisting counterpart of <see cref="CreateTenantedDbContext{T}" />: the transactional middleware
///     inserts that frame itself and commits the transaction it enlists in, and every other chain gets this one.
/// </summary>
internal class BuildTenantedDbContext<T> : MethodCall where T : DbContext
{
    public BuildTenantedDbContext() : base(typeof(IDbContextBuilder<T>), ReflectionHelper.GetMethod<IDbContextBuilder<T>>(x => x.BuildForTenantAsync(null!, CancellationToken.None))!)
    {
    }
}

/// <summary>
///     Supplies the tenant's DbContext to any chain the transactional middleware did not already build one for.
///     The middleware inserts its own enlisting <see cref="CreateTenantedDbContext{T}" />, and a frame that creates
///     the variable wins over every source, so this only ever serves non-transactional chains.
/// </summary>
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
