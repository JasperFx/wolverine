using System.Reflection;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;
using JasperFx.Descriptors;
using Microsoft.EntityFrameworkCore;
using Wolverine.Configuration;
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

/// <summary>
///     GH-4765. <b>Public only so that its closed form can be named in an emitted
///     <c>[DynamicDependency]</c></b> — a <c>typeof()</c> in generated code cannot see an internal type, so
///     the registry's root filter drops non-public contributions rather than emitting a file that will not
///     compile. Same reason <c>Applier&lt;T&gt;</c> went public in GH-4790. Nothing outside this assembly
///     has any business constructing one.
/// </summary>
public class CreateTenantedDbContext<T> : MethodCall, IAotRootSource where T : DbContext
{
    public CreateTenantedDbContext() : base(typeof(IDbContextBuilder<T>), ReflectionHelper.GetMethod<IDbContextBuilder<T>>(x => x.BuildAndEnrollAsync(null!, CancellationToken.None))!)
    {
    }

    /// <summary>
    ///     <c>EFCorePersistenceFrameProvider</c> closes this frame over the user's <c>DbContext</c> at four
    ///     sites — both <c>ApplyTransactionSupport</c> overloads, in their Eager and Lightweight
    ///     multi-tenanted branches. Nothing statically references the closed type, so ILC trims it and
    ///     <c>CloseAndBuildAs</c> throws while the chain model is built, which happens at startup under
    ///     <c>TypeLoadMode.Static</c> too.
    /// </summary>
    /// <remarks>
    ///     <see cref="IDbContextBuilder{T}" /> rides along because the base <see cref="MethodCall" />
    ///     constructor resolves <c>BuildAndEnrollAsync</c> off it through an expression tree, and because
    ///     <c>isMultiTenanted</c> reaches the same instantiation through <c>MakeGenericType</c> — the one
    ///     operation measured as unsafe in a native image unless something already names the closed type.
    ///     The user's own <c>AddDbContextWithWolverineManagedMultiTenancy&lt;T&gt;</c> call does name it,
    ///     but rooting it here does not depend on that staying true.
    ///
    ///     <para>Its sibling <see cref="BuildTenantedDbContext{T}" /> needs none of this: it is closed by
    ///     <see cref="TenantedDbContextSource{T}" />, which <c>EntityFrameworkCoreBackedPersistence&lt;T&gt;</c>
    ///     constructs with the type argument already in hand, so the closed form is a static IL reference
    ///     all the way back to the user's registration. It is also built during code generation rather than
    ///     policy application, which would put it outside the frame lists the root walk sees.</para>
    /// </remarks>
    public IEnumerable<Type> AotRoots()
    {
        yield return GetType();
        yield return typeof(IDbContextBuilder<T>);
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
