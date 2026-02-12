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

    DbContextOptions<T> BuildOptionsForMain();
    
}

internal class CreateTenantedDbContext<T> : MethodCall where T : DbContext
{
    public CreateTenantedDbContext() : base(typeof(IDbContextBuilder<T>), ReflectionHelper.GetMethod<IDbContextBuilder<T>>(x => x.BuildAndEnrollAsync(null, CancellationToken.None)))
    {
    }
}

internal class TenantedDbContextSource<T> : IVariableSource where T : DbContext
{
    public bool Matches(Type type)
    {
        return type == typeof(T);
    }

    public Variable Create(Type type)
    {
        return new CreateTenantedDbContext<T>().ReturnVariable!;
    }
}
