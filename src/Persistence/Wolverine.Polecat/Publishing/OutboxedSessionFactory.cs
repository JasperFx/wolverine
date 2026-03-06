using JasperFx.Core;
using JasperFx.Core.Reflection;
using Polecat;
using Wolverine.Persistence.Durability;
using Wolverine.Runtime;

namespace Wolverine.Polecat.Publishing;

public class OutboxedSessionFactory
{
    private readonly ISessionFactory _factory;
    private readonly IDocumentStore _store;
    private readonly bool _shouldPublishEvents;

    public OutboxedSessionFactory(ISessionFactory factory, IWolverineRuntime runtime, IDocumentStore store)
    {
        _factory = factory;
        _store = store;

        _shouldPublishEvents = runtime.TryFindExtension<PolecatIntegration>()?.UseFastEventForwarding ?? false;

        MessageStore = runtime.Storage;
    }

    internal IMessageStore MessageStore { get; set; }

    /// <summary>Build new instances of IQuerySession on demand</summary>
    public IQuerySession QuerySession(MessageContext context)
    {
        var tenantId = context.Envelope?.TenantId ?? context.TenantId;
        return tenantId.IsNotEmpty()
            ? _store.QuerySession(new SessionOptions { TenantId = tenantId })
            : _factory.QuerySession();
    }

    /// <summary>Build new instances of IQuerySession on demand</summary>
    public IQuerySession QuerySession(MessageContext context, string? tenantId)
    {
        tenantId ??= context.Envelope?.TenantId;
        return tenantId.IsNotEmpty()
            ? _store.QuerySession(new SessionOptions { TenantId = tenantId })
            : _factory.QuerySession();
    }

    public IQuerySession QuerySession(IMessageContext context)
    {
        var tenantId = context.Envelope?.TenantId ?? context.TenantId;
        return tenantId.IsNotEmpty()
            ? _store.QuerySession(new SessionOptions { TenantId = tenantId })
            : _factory.QuerySession();
    }

    /// <summary>Build new instances of IDocumentSession on demand</summary>
    public IDocumentSession OpenSession(MessageContext context)
    {
        var options = buildSessionOptions(context);
        var session = _store.OpenSession(options);
        configureSession(context, session);
        return session;
    }

    /// <summary>Build new instances of IDocumentSession on demand</summary>
    public IDocumentSession OpenSession(MessageContext context, string? tenantId)
    {
        context.TenantId ??= tenantId;
        var options = buildSessionOptions(context);
        var session = _store.OpenSession(options);
        configureSession(context, session);
        return session;
    }

    private SessionOptions buildSessionOptions(MessageContext context)
    {
        var options = new SessionOptions
        {
            Tracking = DocumentTracking.None
        };

        var tenantId = context.Envelope?.TenantId ?? context.TenantId;
        if (tenantId.IsNotEmpty())
        {
            options.TenantId = tenantId;
        }

        // Add listeners before session creation (Polecat requirement)
        if (_shouldPublishEvents)
        {
            options.Listeners.Add(new PublishIncomingEventsBeforeCommit(context));
        }

        options.Listeners.Add(new FlushOutgoingMessagesOnCommit(context, null!)); // store set after transaction creation

        return options;
    }

    private void configureSession(MessageContext context, IDocumentSession session)
    {
        context.OverrideStorage(MessageStore);

        if (context.ConversationId != Guid.Empty)
        {
            session.CausationId = context.ConversationId.ToString();
        }

        session.CorrelationId = context.CorrelationId;

        if (context.Envelope?.UserName is not null)
        {
            session.LastModifiedBy = context.Envelope.UserName;
        }
        else if (context.UserName is not null)
        {
            session.LastModifiedBy = context.UserName;
        }

        var transaction = new PolecatEnvelopeTransaction(session, context);
        context.EnlistInOutbox(transaction);

        // Now register the transaction participant for flushing outgoing messages
        session.AddTransactionParticipant(new FlushOutgoingMessagesParticipant(context, transaction.Store));
    }

    /// <summary>Build new instances of IDocumentSession on demand</summary>
    public IDocumentSession OpenSession(IMessageBus bus)
    {
        var context = bus.As<MessageContext>();
        return OpenSession(context);
    }
}
