using JasperFx.Core;
using JasperFx.Core.Reflection;
using Marten;
using Wolverine.Runtime;

namespace Wolverine.Marten.Publishing;

public class OutboxedSessionFactory
{
    private readonly ISessionFactory _factory;
    private readonly IDocumentStore _store;
    private readonly bool _shouldPublishEvents;

    private readonly Func<MessageContext, IDocumentSession> _builder;

    public OutboxedSessionFactory(ISessionFactory factory, IWolverineRuntime runtime, IDocumentStore store)
    {
        _factory = factory;
        _store = store;
        _shouldPublishEvents = runtime.TryFindExtension<MartenIntegration>()?.ShouldPublishEvents ?? false;

        if (factory is SessionFactoryBase factoryBase)
        {
            _builder = c =>
            {
                var options = factoryBase.BuildOptions();
                if (c.TenantId.IsNotEmpty())
                {
                    options.TenantId = c.TenantId;
                }

                return _store.OpenSession(options);
            };
        }
        else
        {
            _builder = c =>
            {
                var tenantId = c.Envelope?.TenantId ?? c.TenantId;
                
                return tenantId.IsEmpty() 
                    ? _factory.OpenSession() 
                    : _store.LightweightSession(tenantId);
            };
        }
    }

    /// <summary>Build new instances of IQuerySession on demand</summary>
    /// <returns></returns>
    public IQuerySession QuerySession(MessageContext context)
    {
        var tenantId = context.Envelope?.TenantId ?? context.TenantId;
        return tenantId.IsNotEmpty() 
            ? _store.QuerySession(tenantId) 
            : _factory.QuerySession();
    }
    
    /// <summary>Build new instances of IQuerySession on demand</summary>
    /// <returns></returns>
    public IQuerySession QuerySession(MessageContext context, string? tenantId)
    {
        tenantId ??= context.Envelope?.TenantId;
        return tenantId.IsNotEmpty() 
            ? _store.QuerySession(tenantId) 
            : _factory.QuerySession();
    }
    
    public IQuerySession QuerySession(IMessageContext context)
    {
        var tenantId = context.Envelope?.TenantId ?? context.TenantId;
        return tenantId.IsNotEmpty() 
            ? _store.QuerySession(tenantId) 
            : _factory.QuerySession();
    }

    /// <summary>Build new instances of IDocumentSession on demand</summary>
    /// <returns></returns>
    public IDocumentSession OpenSession(MessageContext context)
    {
        var session = _builder(context);

        configureSession(context, session);

        return session;
    }
    
    /// <summary>Build new instances of IDocumentSession on demand</summary>
    /// <returns></returns>
    public IDocumentSession OpenSession(MessageContext context, string? tenantId)
    {
        context.TenantId ??= tenantId;
        var session = _builder(context);

        configureSession(context, session);

        return session;
    }

    private void configureSession(MessageContext context, IDocumentSession session)
    {
        if (context.ConversationId != Guid.Empty)
        {
            session.CausationId = context.ConversationId.ToString();
        }

        session.CorrelationId = context.CorrelationId;

        context.EnlistInOutbox(new MartenEnvelopeTransaction(session, context));

        if (_shouldPublishEvents)
        {
            session.Listeners.Add(new PublishIncomingEventsBeforeCommit(context));
        }

        session.Listeners.Add(new FlushOutgoingMessagesOnCommit(context));
    }

    /// <summary>Build new instances of IDocumentSession on demand</summary>
    /// <returns></returns>
    public IDocumentSession OpenSession(IMessageBus bus)
    {
        var context = bus.As<MessageContext>();
        return OpenSession(context);
    }
}