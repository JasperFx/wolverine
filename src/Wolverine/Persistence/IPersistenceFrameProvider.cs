using JasperFx;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using Wolverine.Configuration;
using Wolverine.Persistence.Sagas;
using Wolverine.Runtime;

namespace Wolverine.Persistence;

public interface IPersistenceFrameProvider
{
    /// <summary>
    ///     Whether this provider's <see cref="CanPersist"/> claims every entity type it is asked
    ///     about — a "catch-all" document store like Marten that can genuinely persist any document —
    ///     rather than checking the type against its own mapping or model (like EF Core, which only
    ///     claims types mapped in a registered DbContext). Catch-all providers are consulted after
    ///     selective providers regardless of registration order, so that an entity mapped by a
    ///     selective provider deterministically resolves to that provider in mixed-persistence
    ///     applications.
    /// </summary>
    bool IsCatchAll => false;

    void ApplyTransactionSupport(IChain chain, IServiceContainer container);
    void ApplyTransactionSupport(IChain chain, IServiceContainer container, Type entityType);
    bool CanApply(IChain chain, IServiceContainer container);

    /// <summary>
    ///     Use for Saga creation support as returned value
    /// </summary>
    /// <param name="entityType"></param>
    /// <param name="container"></param>
    /// <param name="persistenceService"></param>
    /// <returns></returns>
    bool CanPersist(Type entityType, IServiceContainer container, out Type persistenceService);

    Type DetermineSagaIdType(Type sagaType, IServiceContainer container);
    Frame DetermineLoadFrame(IServiceContainer container, Type sagaType, Variable sagaId);
    Frame DetermineInsertFrame(Variable saga, IServiceContainer container);
    Frame CommitUnitOfWorkFrame(Variable saga, IServiceContainer container);
    Frame DetermineUpdateFrame(Variable saga, IServiceContainer container);
    Frame DetermineDeleteFrame(Variable sagaId, Variable saga, IServiceContainer container);
    
    /// <summary>
    /// Create an "upsert" Frame for the variable. Not every persistence provider will be able to support this
    /// and should throw NotSupportedException if it does not
    /// </summary>
    /// <param name="saga"></param>
    /// <param name="container"></param>
    /// <returns></returns>
    Frame DetermineStoreFrame(Variable saga, IServiceContainer container);

    /// <summary>
    /// Create a delete Frame for the variable, not every persistence provider will be able to support this
    /// and should throw NotSupportedException if it does not
    /// </summary>
    /// <param name="variable"></param>
    /// <param name="container"></param>
    /// <returns></returns>
    Frame DetermineDeleteFrame(Variable variable, IServiceContainer container);

    Frame DetermineStorageActionFrame(Type entityType, Variable action, IServiceContainer container);

    Frame[] DetermineFrameToNullOutMaybeSoftDeleted(Variable entity);

    /// <summary>
    /// Attempt to build a codegen <see cref="Frame"/> that executes a query specification
    /// (e.g. a Marten <c>ICompiledQuery&lt;,&gt;</c> or <c>IQueryPlan&lt;&gt;</c>, or a
    /// Wolverine.EntityFrameworkCore <c>IQueryPlan&lt;TDbContext,TResult&gt;</c>) and produces
    /// its materialized result as a new variable for downstream frames to consume.
    ///
    /// <para>
    /// Return <c>true</c> if the provider recognizes the variable's type as one of its
    /// specification contracts. The default implementation returns <c>false</c>, signaling
    /// "this provider doesn't handle this spec type — try another".
    /// </para>
    /// <para>
    /// Consumed by <see cref="FromQuerySpecificationAttribute"/> to dispatch cross-provider.
    /// </para>
    /// </summary>
    /// <param name="specVariable">Variable holding the constructed specification instance.</param>
    /// <param name="container">Active codegen service container.</param>
    /// <param name="frame">The built frame, when the provider handles the spec type.</param>
    /// <param name="result">The result variable produced by the frame, when built.</param>
    bool TryBuildFetchSpecificationFrame(
        Variable specVariable,
        IServiceContainer container,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out Frame? frame,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out Variable? result)
    {
        frame = null;
        result = null;
        return false;
    }

    /// <summary>
    /// Attempt to build a codegen <see cref="Frame"/> that executes the equivalent of
    /// <c>session.Query&lt;T&gt;().FirstOrDefaultAsync()</c> for <paramref name="entityType"/> against this
    /// provider's own session, producing the entity (or <c>null</c>) as a new variable for downstream frames.
    ///
    /// <para>
    /// Return <c>true</c> if the provider can express an unfiltered "first row of this type" read. The default
    /// implementation returns <c>false</c>, signaling "this provider does not support it" — which
    /// <see cref="FirstOrDefaultAttribute"/> turns into a bootstrapping time error naming the provider, rather
    /// than silently doing nothing.
    /// </para>
    /// <para>
    /// Every provider spells the async terminal operator differently — Marten's <c>QueryableExtensions</c>,
    /// EF Core's <c>EntityFrameworkQueryableExtensions</c>, RavenDb's own async LINQ extensions, and CosmosDb
    /// with no such extension at all — which is exactly why this is provider supplied rather than a shared
    /// expression built in core.
    /// </para>
    /// </summary>
    /// <param name="entityType">The entity type to read the first instance of.</param>
    /// <param name="container">Active codegen service container.</param>
    /// <param name="frame">The built frame, when the provider supports this.</param>
    /// <param name="result">The result variable produced by the frame, when built.</param>
    bool TryBuildFirstOrDefaultFrame(
        Type entityType,
        IServiceContainer container,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out Frame? frame,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out Variable? result)
    {
        frame = null;
        result = null;
        return false;
    }

    /// <summary>
    /// Attempt to build a codegen <see cref="Frame"/> that executes the equivalent of
    /// <c>session.Query&lt;T&gt;().ToListAsync()</c> for <paramref name="entityType"/> against this provider's
    /// own session, producing a <c>List&lt;T&gt;</c> as a new variable for downstream frames.
    ///
    /// <para>
    /// Return <c>true</c> if the provider can express an unfiltered "every row of this type" read. The
    /// default implementation returns <c>false</c>, which <see cref="AllAttribute"/> turns into a
    /// bootstrapping time error naming the provider rather than silently doing nothing.
    /// </para>
    /// </summary>
    /// <param name="entityType">The element type to read every instance of.</param>
    /// <param name="container">Active codegen service container.</param>
    /// <param name="frame">The built frame, when the provider supports this.</param>
    /// <param name="result">The <c>List&lt;T&gt;</c> variable produced by the frame, when built.</param>
    bool TryBuildAllFrame(
        Type entityType,
        IServiceContainer container,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out Frame? frame,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out Variable? result)
    {
        frame = null;
        result = null;
        return false;
    }

    /// <summary>
    /// Attempt to build a codegen <see cref="Frame"/> that exposes this provider's raw
    /// <c>IQueryable&lt;T&gt;</c> for <paramref name="elementType"/> — Marten's <c>session.Query&lt;T&gt;()</c>,
    /// EF Core's <c>dbContext.Set&lt;T&gt;()</c>, and so on — as a new variable for the endpoint or handler to
    /// compose a query against directly.
    ///
    /// <para>
    /// Return <c>true</c> if the provider can hand out a queryable. The default returns <c>false</c>, which
    /// <see cref="QueryableAttribute"/> turns into a bootstrapping time error naming the provider.
    /// </para>
    /// </summary>
    /// <param name="elementType">The element type of the queryable.</param>
    /// <param name="container">Active codegen service container.</param>
    /// <param name="frame">The built frame, when the provider supports this.</param>
    /// <param name="result">The <c>IQueryable&lt;T&gt;</c> variable produced by the frame, when built.</param>
    bool TryBuildQueryableFrame(
        Type elementType,
        IServiceContainer container,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out Frame? frame,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out Variable? result)
    {
        frame = null;
        result = null;
        return false;
    }
}



