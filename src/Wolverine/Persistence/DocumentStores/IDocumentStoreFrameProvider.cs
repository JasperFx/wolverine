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
///     GH-5514: marks the store-agnostic <c>[DocumentExists&lt;T&gt;]</c> and <c>[DocumentDoesNotExist&lt;T&gt;]</c> in
///     <c>Wolverine.Persistence.Requirements</c>, so a store's persistence frame provider can see that a chain needs a
///     document session. The per-store copies kept until 8.0 do not carry it: each store only claims its own.
/// </summary>
internal interface IDocumentExistenceAttribute;
