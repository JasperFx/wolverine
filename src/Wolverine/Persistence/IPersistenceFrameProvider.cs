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
    ///     The service type that owns this chain's transaction, when that owner is itself one of the
    ///     chain's own service dependencies — EF Core's <c>DbContext</c>, chosen by
    ///     <c>DetermineDbContextType</c>. Wolverine uses this to route a handler's durable inbox row to
    ///     the ancillary message store enrolled for that owner, so the inbox update and the handler's
    ///     writes share one transaction (GH-3870).
    /// </summary>
    /// <remarks>
    ///     Returns null by default, which is the correct answer for every provider whose ancillary store
    ///     must be named explicitly with <c>[Storage]</c> / <c>[MartenStore]</c> / <c>[PolecatStore]</c>
    ///     (Marten, Polecat, Fisher). Those designations already populate
    ///     <see cref="IChain.AncillaryStoreType" />, and a bare dependency on the store interface says
    ///     nothing about who commits — a store injected only to run read-only queries would otherwise
    ///     pull the inbox row away from the store the handler actually writes to (GH-3953).
    ///     Implementations must not throw: an unresolvable or ambiguous owner is reported as null and
    ///     left for codegen to diagnose.
    /// </remarks>
    Type? TryDetermineTransactionOwnerType(IChain chain, IServiceContainer container) => null;

    /// <summary>
    ///     Use for Saga creation support as returned value
    /// </summary>
    /// <param name="entityType"></param>
    /// <param name="container"></param>
    /// <param name="persistenceService"></param>
    /// <returns></returns>
    bool CanPersist(Type entityType, IServiceContainer container, out Type persistenceService);

    Type DetermineSagaIdType(Type sagaType, IServiceContainer container);

    /// <summary>
    ///     The identity type of <paramref name="sagaType" /> <b>on the store this chain is routed to</b>.
    ///     GH-4441.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///     An identity type can be a fact about a store rather than about the type: Marten answers with the
    ///     configured document mapping's id type, and a store may name a different member as the identity
    ///     (<c>Schema.For&lt;T&gt;().Identity(x =&gt; x.Code)</c>). An implementation that asks the default
    ///     store therefore answers for the wrong store on a chain routed elsewhere with
    ///     <c>[Storage]</c> / <c>[MartenStore]</c>, and it does so <b>silently</b> — Marten resolves a
    ///     conventional mapping for a type it has never been told about rather than failing, so the caller
    ///     gets a confident wrong answer and then fails further downstream looking for an identity member of
    ///     a type that does not exist on the message.
    ///     </para>
    ///     <para>
    ///     Optional, and defaulted to the chain-less overload, on the same terms as
    ///     <see cref="TryDetermineTransactionOwnerType" />: a provider that derives identity from the type
    ///     alone — Polecat and Fisher reflect over the <c>Id</c> property and consult no store — has no store
    ///     to be wrong about and needs no override. Implementations that do consult a store should resolve it
    ///     with <c>chain.DetermineAncillaryStoreType()</c> and fall back to the default store.
    ///     </para>
    /// </remarks>
    Type DetermineSagaIdType(Type sagaType, IChain chain, IServiceContainer container)
        => DetermineSagaIdType(sagaType, container);

    Frame DetermineLoadFrame(IServiceContainer container, Type sagaType, Variable sagaId);
    Frame DetermineInsertFrame(Variable saga, IServiceContainer container);
    Frame CommitUnitOfWorkFrame(Variable saga, IServiceContainer container);
    Frame DetermineUpdateFrame(Variable saga, IServiceContainer container);
    Frame DetermineDeleteFrame(Variable sagaId, Variable saga, IServiceContainer container);

    /// <summary>
    ///     The update frame for an entity that arrived as a <see cref="Update{T}" /> return value, rather
    ///     than as a saga Wolverine itself read into a local of its own. GH-4613.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///     <see cref="DetermineUpdateFrame" /> serves two callers that need different code, and the
    ///     difference is not visible in its arguments. <see cref="Sagas.SagaChain" /> calls it for a saga
    ///     Wolverine loaded a moment earlier, so the session or context is already tracking that instance
    ///     and a version, revision or ETag from the read is in scope. <c>Update&lt;T&gt;.BuildFrame</c>
    ///     calls it for whatever the handler returned, which may be an entity built from the message, taken
    ///     from a request body, or loaded with <c>AsNoTracking()</c> — nothing tracks it, and there was no
    ///     read to declare a version against.
    ///     </para>
    ///     <para>
    ///     A provider whose write is unconditional — every document store that spells this
    ///     <c>session.Store(entity)</c> — needs no override; the default delegates and the two paths stay
    ///     identical. Override it when the saga path does something the storage-action path must not,
    ///     which in practice means either optimistic concurrency (Redis compare-and-swap, Marten's
    ///     <c>UpdateRevision</c>, Cosmos' <c>IfMatchEtag</c>) or, for EF Core, relying on change tracking
    ///     that a returned entity was never enrolled in.
    ///     </para>
    /// </remarks>
    Frame DetermineStorageUpdateFrame(Variable entity, IServiceContainer container)
        => DetermineUpdateFrame(entity, container);

    /// <summary>
    ///     The insert frame for an entity that arrived as an <see cref="Insert{T}" /> return value rather
    ///     than as a saga Wolverine is starting. GH-4613, and the same reasoning as
    ///     <see cref="DetermineStorageUpdateFrame" />.
    /// </summary>
    /// <remarks>
    ///     There is deliberately no <c>DetermineStorageStoreFrame</c> or storage-action delete: nothing but
    ///     <c>Store&lt;T&gt;.BuildFrame</c> calls <see cref="DetermineStoreFrame" />, and nothing but
    ///     <c>Delete&lt;T&gt;.BuildFrame</c> calls the single-argument
    ///     <see cref="DetermineDeleteFrame(Variable,IServiceContainer)" />, so both of those are already
    ///     the storage-action entry point and an alias would carry no information.
    /// </remarks>
    Frame DetermineStorageInsertFrame(Variable entity, IServiceContainer container)
        => DetermineInsertFrame(entity, container);

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
    ///     GH-4505. Attempt to supply frames that make a logical deduplication claim ride this provider's
    ///     own transaction, replacing the claim-and-release pair that
    ///     <see cref="Codegen.ChainDeduplicationExtensions" /> otherwise weaves in.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///     Returning <see langword="false" /> — the default — keeps the shipped behaviour: the claim is
    ///     written up front on a connection of its own and given back in a <c>finally</c> when the chain
    ///     fails. That is correct, and it is what a non-transactional chain must keep, because there is no
    ///     transaction for the claim to ride.
    ///     </para>
    ///     <para>
    ///     A provider should return <see langword="true" /> only when it can prove the deduplication table
    ///     is reachable from the same transaction the handler commits through. For Marten that means a
    ///     single-database store, where Wolverine's message store is built from Marten's own
    ///     <c>NpgsqlDataSource</c>; database-per-tenant builds the message store somewhere else entirely
    ///     and must fall through.
    ///     </para>
    /// </remarks>
    /// <param name="chain">The chain being woven. Consult it for the ancillary store and the refusal shape.</param>
    /// <param name="deduplicationId">The resolved logical id, which may be null or empty at runtime.</param>
    /// <param name="requirement">What the chain asked for, including whether the id is required.</param>
    /// <param name="container">Active codegen service container.</param>
    /// <param name="deduplication">The frames, when this provider owns the claim.</param>
    bool TryBuildTransactionalDeduplication(
        IChain chain,
        Variable deduplicationId,
        DeduplicationRequirement requirement,
        IServiceContainer container,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out TransactionalDeduplication? deduplication)
    {
        deduplication = null;
        return false;
    }

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



