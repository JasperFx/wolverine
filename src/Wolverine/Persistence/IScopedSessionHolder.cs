namespace Wolverine.Persistence;

/// <summary>
/// Implemented by a document store integration's scope-local session holder (Marten's
/// <c>ScopedDocumentSessionHolder</c> and its Polecat / Fisher / RavenDb twins). When a handler falls
/// back to service location, Wolverine's generated code creates a child <c>IServiceScope</c> off the
/// root provider and primes that scope's holder with the handler's outbox-enrolled session, so a
/// service-located session resolves to the SAME session enrolled with the active outbox instead of a
/// separate, un-enrolled one. See GH-3001 and GH-4145.
/// </summary>
/// <remarks>
/// The holder is empty in non-handler scopes (hosted services, admin tools, raw resolution), where the
/// decorated registration falls back to the store's own session factory.
/// </remarks>
/// <typeparam name="TSession">The store's own session type — Marten's <c>IDocumentSession</c>,
/// RavenDb's <c>IAsyncDocumentSession</c>, and so on.</typeparam>
public interface IScopedSessionHolder<TSession> where TSession : class
{
    TSession? Session { get; set; }
}
