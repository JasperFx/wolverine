using Marten;

namespace Wolverine.Marten;

/// <summary>
/// Scope-local carrier for the outbox-enrolled <see cref="IDocumentSession"/> a handler is using.
/// When a handler falls back to service location, Wolverine's generated code creates a child
/// <see cref="IServiceScope"/> off the root provider; the Marten scoping frame primes this holder in
/// that scope so a service-located <see cref="IDocumentSession"/> / <see cref="IQuerySession"/>
/// resolves to the SAME session enrolled with the active outbox instead of a separate, un-enrolled
/// one (which would defeat the transaction boundary). See GH-3001.
///
/// The holder is empty in non-handler scopes (hosted services, admin tools, raw resolution), where
/// the decorated registration falls back to Marten's own session factory.
/// </summary>
public sealed class ScopedDocumentSessionHolder
{
    public IDocumentSession? Session { get; set; }
}
