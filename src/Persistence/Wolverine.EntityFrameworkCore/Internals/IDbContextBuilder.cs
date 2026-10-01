using System.Reflection;
using JasperFx.CodeGeneration;
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

/// <summary>
///     Builds the DbContext for the current message's tenant WITHOUT enlisting the <see cref="MessageContext" /> in
///     an EF Core outbox transaction. <see cref="CreateTenantedDbContext{T}" /> enlists, which is right for the
///     transactional middleware -- it inserts that frame itself and commits the transaction it enlisted in -- and
///     wrong for any other chain: nothing commits the enlisted transaction, so every message the chain sends or
///     schedules is written into it and silently dropped.
/// </summary>
internal class BuildTenantedDbContext<T> : AsyncFrame where T : DbContext
{
    private Variable _builder = null!;
    private Variable _messaging = null!;
    private Variable _cancellation = null!;

    public BuildTenantedDbContext()
    {
        DbContext = new Variable(typeof(T), this);
    }

    public Variable DbContext { get; }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        _builder = chain.FindVariable(typeof(IDbContextBuilder<T>));
        yield return _builder;

        _messaging = chain.FindVariable(typeof(MessageContext));
        yield return _messaging;

        _cancellation = chain.FindVariable(typeof(CancellationToken));
        yield return _cancellation;
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.Write(
            $"await using var {DbContext.Usage} = await {_builder.Usage}.{nameof(IDbContextBuilder<T>.BuildAsync)}({_messaging.Usage}.{nameof(MessageContext.TenantId)}, {_cancellation.Usage}).ConfigureAwait(false);");
        Next?.GenerateCode(method, writer);
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
        return new BuildTenantedDbContext<T>().DbContext;
    }
}
