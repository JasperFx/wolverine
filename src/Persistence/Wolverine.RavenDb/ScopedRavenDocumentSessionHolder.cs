using Raven.Client.Documents.Session;
using Wolverine.Persistence;

namespace Wolverine.RavenDb;

/// <summary>
/// Scope-local carrier for the outbox-enrolled <see cref="IAsyncDocumentSession"/> a handler is using.
/// When a handler falls back to service location, Wolverine's generated code creates a child
/// <see cref="IServiceScope"/> off the root provider; the RavenDb scoping frame primes this holder in
/// that scope so a service-located <see cref="IAsyncDocumentSession"/> resolves to the SAME session
/// enrolled with the active outbox instead of a separate, un-enrolled one (which would defeat the
/// transaction boundary). See GH-4145, the RavenDb half of GH-3001.
///
/// The holder is empty in non-handler scopes (hosted services, admin tools, raw resolution), where the
/// registration falls back to opening a fresh session off the <c>IDocumentStore</c>.
/// </summary>
public sealed class ScopedRavenDocumentSessionHolder : IScopedSessionHolder<IAsyncDocumentSession>
{
    public IAsyncDocumentSession? Session { get; set; }
}
