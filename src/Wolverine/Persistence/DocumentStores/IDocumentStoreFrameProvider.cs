using Wolverine.Persistence.DocumentStores.Codegen;

namespace Wolverine.Persistence.DocumentStores;

/// <summary>
///     GH-5514: implemented by the persistence frame providers of the document store integrations (Marten, Polecat,
///     Fisher), so store-agnostic attributes in core can find the registered provider of a document store and the
///     session types its codegen needs without naming the store.
/// </summary>
internal interface IDocumentStoreFrameProvider : IPersistenceFrameProvider
{
    DocumentSessionTypes SessionTypes { get; }
}

/// <summary>
///     GH-5514: marks <c>[DocumentExists&lt;T&gt;]</c> and <c>[DocumentDoesNotExist&lt;T&gt;]</c>, both the core attributes
///     in <c>Wolverine.Persistence.Requirements</c> and the per-store copies kept until 8.0, so a store's
///     persistence frame provider can see that a chain needs its session without naming every attribute type.
/// </summary>
internal interface IDocumentExistenceAttribute;
