// NOTE: This file requires Polecat 1.1+ (AddPolecatStore<T>)
// Uncomment #if POLECAT_1_1 / #endif when ready, or remove the guards after upgrading the Polecat NuGet
#if POLECAT_1_1
using JasperFx.Core.Reflection;
using Polecat;
using Wolverine.Persistence.Durability;
using Wolverine.Runtime;

namespace Wolverine.Polecat.Publishing;

public class OutboxedSessionFactory<T> : OutboxedSessionFactory where T : IDocumentStore
{
    private readonly T _store;

    public OutboxedSessionFactory(IWolverineRuntime runtime, T store)
        : base(new DefaultSessionFactory(store), runtime, store)
    {
        _store = store;

        MessageStore = runtime.FindAncillaryStoreForMarkerType(typeof(T));
    }
}

internal class DefaultSessionFactory : ISessionFactory
{
    private readonly IDocumentStore _store;

    public DefaultSessionFactory(IDocumentStore store)
    {
        _store = store;
    }

    public IDocumentSession OpenSession()
    {
        return _store.OpenSession(new SessionOptions { Tracking = DocumentTracking.None });
    }

    public IQuerySession QuerySession()
    {
        return _store.QuerySession();
    }
}
#endif
