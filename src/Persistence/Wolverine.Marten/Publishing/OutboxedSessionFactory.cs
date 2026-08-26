using JasperFx.Core;
using JasperFx.Core.Reflection;
using Marten;
using Marten.Services;
using Wolverine.Persistence.Durability;
using Wolverine.Runtime;

namespace Wolverine.Marten.Publishing;

public class OutboxedSessionFactory<T> : OutboxedSessionFactory, ISessionFactory where T : IDocumentStore
{
    private readonly T _store;
    
    public OutboxedSessionFactory(IWolverineRuntime runtime, T store) : base(new SessionFactory(store), runtime, store)
    {
        _store = store;
        _factory = this;

        MessageStore = runtime.FindAncillaryStoreForMarkerType(typeof(T));
    }

    public IQuerySession QuerySession()
    {
        return _store.QuerySession();
    }

    public IDocumentSession OpenSession()
    {
        return _store.LightweightSession();
    }

    public class SessionFactory(T parent) : SessionFactoryBase(parent)
    {
        public override SessionOptions BuildOptions()
        {
            return new SessionOptions { Tracking = DocumentTracking.None };
        }
    }
}

public class OutboxedSessionFactory
{
    protected ISessionFactory _factory;
    private readonly IDocumentStore _store;
    private readonly bool _shouldPublishEvents;
    private readonly bool _shouldTrackAppends;
    private readonly IWolverineRuntime _runtime;
    private IMessageStore? _messageStore;

    private readonly Func<MessageContext, IDocumentSession> _builder;

    public OutboxedSessionFactory(ISessionFactory factory, IWolverineRuntime runtime, IDocumentStore store)
    {
        _factory = factory;
        _store = store;

        _shouldPublishEvents = runtime.TryFindExtension<MartenIntegration>()?.UseFastEventForwarding ?? false;
        _shouldTrackAppends = runtime.Options.Tracking.EnableEventAppendTracking;

        _runtime = runtime;
        
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
    
    /// <summary>
    /// The message store this factory enlists sessions in. Defaults to the runtime's <b>Main</b> store,
    /// resolved on every read rather than captured in the constructor; an ancillary-store subclass
    /// (<c>OutboxedSessionFactory&lt;T&gt;</c>) assigns a fixed store and that assignment wins.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>GH-4130. Do not go back to <c>MessageStore = runtime.Storage</c> in the constructor.</b>
    /// <c>IWolverineRuntime.Storage</c> is <c>Stores.Main</c>, which is the placeholder
    /// <see cref="NullMessageStore"/> until <c>MessageStoreCollection.InitializeAsync()</c> assigns the
    /// real one — and that assignment is deferred whenever more than one store claims
    /// <see cref="MessageStoreRole.Main"/> and <c>DurabilitySettings.ResolveMainStoreOnConflict</c> has to
    /// reconcile them (GH-3226). A database-backed queue transport alongside an event-store-integrated
    /// Main is exactly that shape. Capturing early left this factory holding the placeholder for the life
    /// of the process while <c>Stores.Main</c> read perfectly correct afterwards, so the host booted and
    /// listened cleanly and then failed EVERY message and HTTP request with "requires a SQL Server-backed
    /// message store / is not using Postgresql + Marten as the backing message persistence". Nothing
    /// pointed at the store roles, which were right the whole time.
    /// </remarks>
    internal IMessageStore MessageStore
    {
        get => _messageStore ?? _runtime.Storage;
        set => _messageStore = value;
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
        context.OverrideStorage(MessageStore);

        // Per-message CausationId override supplied via
        // DeliveryOptions.CausationId (envelope header "causation-id") takes
        // precedence over the default Wolverine ConversationId-based causation
        // chain. This is how a projection that calls
        // slice.PublishMessage(cmd, metadata with CausationId = ...) gets the
        // overridden id onto the events the command's handler writes.
        if (context.Envelope is { } env
            && env.Headers.TryGetValue(EnvelopeConstants.CausationIdKey, out var headerCausationId)
            && !string.IsNullOrEmpty(headerCausationId))
        {
            session.CausationId = headerCausationId;
        }
        else if (context.ConversationId != Guid.Empty)
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

        var transaction = new MartenEnvelopeTransaction(session, context);
        context.EnlistInOutbox(transaction);

        if (_shouldPublishEvents)
        {
            session.Listeners.Add(new PublishIncomingEventsBeforeCommit(context));
        }

        if (_shouldTrackAppends)
        {
            session.Listeners.Add(new NotifyObserverOfAppendedEvents(context));
        }

        session.Listeners.Add(new FlushOutgoingMessagesOnCommit(context, transaction.Store));
    }

    /// <summary>Build new instances of IDocumentSession on demand</summary>
    /// <returns></returns>
    public IDocumentSession OpenSession(IMessageBus bus)
    {
        var context = bus.As<MessageContext>();
        return OpenSession(context);
    }
}