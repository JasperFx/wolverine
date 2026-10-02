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
    public CreateTenantedDbContext() : base(typeof(IDbContextBuilder<T>), ReflectionHelper.GetMethod<IDbContextBuilder<T>>(x => x.BuildAndEnrollAsync(null!, CancellationToken.None))!)
    {
    }
}

// Builds the DbContext for the message's tenant without enlisting the MessageContext in an outbox transaction
internal class BuildTenantedDbContext<T> : MethodCall where T : DbContext
{
    public BuildTenantedDbContext() : base(typeof(IDbContextBuilder<T>), ReflectionHelper.GetMethod<IDbContextBuilder<T>>(x => x.BuildAsync(string.Empty, CancellationToken.None))!)
    {
    }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        var context = chain.FindVariable(typeof(MessageContext));
        Arguments[0] = new MemberAccessVariable(context, typeof(MessageContext).GetProperty(nameof(MessageContext.TenantId))!);

        // The context itself has to be a dependency too, or F# discards the handler's context argument
        yield return context;
        foreach (var variable in base.FindVariables(chain)) yield return variable;
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
