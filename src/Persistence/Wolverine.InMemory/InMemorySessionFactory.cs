using JasperFx.Core;
using JasperFx.Events.InMemory;

namespace Wolverine.InMemory;

/// <summary>
/// Opens the in-memory prototyping store's sessions for generated handler code (wolverine#4838), for the
/// tenant of the message being handled.
/// </summary>
/// <remarks>
/// There is no outbox to enlist in: messaging on this store runs without durable inbox or outbox, so a
/// session is opened against the store and nothing else.
/// </remarks>
public class InMemorySessionFactory
{
    private readonly InMemoryDocumentStore _store;

    public InMemorySessionFactory(InMemoryDocumentStore store)
    {
        _store = store;
    }

    public IInMemoryDocumentSession OpenSession(IMessageContext? context, string? tenantId = null)
    {
        var session = open(context, tenantId);

        // Tracing context reaches the events this session appends, as on the other stores
        if (context is not null)
        {
            if (context.CorrelationId.IsNotEmpty()) session.CorrelationId = context.CorrelationId;
            if (context.Envelope is { } envelope) session.CausationId = envelope.Id.ToString();
        }

        return session;
    }

    public IInMemoryQuerySession QuerySession(IMessageContext? context, string? tenantId = null)
        => open(context, tenantId);

    private InMemoryDocumentSession open(IMessageContext? context, string? tenantId)
    {
        tenantId ??= context?.Envelope?.TenantId ?? context?.TenantId;
        return tenantId.IsNotEmpty() ? _store.LightweightSession(tenantId) : _store.LightweightSession();
    }
}
