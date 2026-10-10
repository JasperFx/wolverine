using JasperFx.Core;
using JasperFx.Core.Reflection;
using Fisher;
using Wolverine.Persistence.Durability;
using Wolverine.Runtime;
using Wolverine.Sqlite;
using MultiTenantedMessageStore = Wolverine.Persistence.Durability.MultiTenantedMessageStore;

namespace Wolverine.Fisher.Publishing;

public class OutboxedSessionFactory
{
    private readonly ISessionFactory _factory;
    private readonly IDocumentStore _store;
    private readonly bool _shouldPublishEvents;
    private readonly bool _shouldTrackAppends;
    private readonly IWolverineRuntime _runtime;
    private IMessageStore? _messageStore;

    public OutboxedSessionFactory(ISessionFactory factory, IWolverineRuntime runtime, IDocumentStore store)
    {
        _factory = factory;
        _store = store;

        _shouldPublishEvents = runtime.TryFindExtension<FisherIntegration>()?.UseFastEventForwarding ?? false;
        _shouldTrackAppends = runtime.Options.Tracking.EnableEventAppendTracking;

        _runtime = runtime;
    }

    /// <summary>
    /// The message store this factory enlists sessions in. Defaults to the runtime's <b>Main</b> store,
    /// resolved on every read rather than captured in the constructor; an ancillary-store subclass
    /// (<c>OutboxedSessionFactory&lt;T&gt;</c>) assigns a fixed store and that assignment wins.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>GH-4130 / GH-4633. Do not go back to <c>MessageStore = runtime.Storage</c> in the constructor.</b>
    /// <c>IWolverineRuntime.Storage</c> is <c>Stores.Main</c>, which is the placeholder
    /// <see cref="NullMessageStore"/> until <c>MessageStoreCollection.InitializeAsync()</c> assigns the
    /// real one — and that assignment is deferred whenever more than one store claims
    /// <see cref="MessageStoreRole.Main"/> and <c>DurabilitySettings.ResolveMainStoreOnConflict</c> has to
    /// reconcile them (GH-3226). Fisher reaches that shape too: a SQLite file IS a database, so
    /// <c>UseSqlitePersistenceAndTransport</c> against one file plus an integrated Fisher store on another
    /// is two Main claimants with two distinct store Uris. Capturing early left this factory holding the
    /// placeholder for the life of the process while <c>Stores.Main</c> read perfectly correct afterwards,
    /// so the host booted and listened cleanly and then failed EVERY message and HTTP request with
    /// "Wolverine.Fisher requires a SQLite-backed message store … was NullMessageStore". Nothing pointed at
    /// the store roles, which were right the whole time.
    /// </remarks>
    internal IMessageStore MessageStore
    {
        get => _messageStore ?? _runtime.Storage;
        set => _messageStore = value;
    }

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

        // Add listeners before session creation (Fisher requirement)
        if (_shouldPublishEvents)
        {
            options.Listeners.Add(new PublishIncomingEventsBeforeCommit(context));
        }

        // GH-4931: a tracked session hears what this session appends, whether or not the observer does
        if (_shouldTrackAppends || _runtime is WolverineRuntime { ActiveSession: not null })
        {
            options.Listeners.Add(new NotifyObserverOfAppendedEvents(context));
        }

        // The FlushOutgoingMessagesOnCommit listener needs the SQLite
        // message store so it can mark the incoming envelope as Handled in
        // the same transaction as the document changes. The factory's
        // MessageStore property reads runtime.Storage lazily, HERE, at the
        // moment the session is opened — never at ctor time, which is the
        // GH-4130 / GH-4633 trap documented on that property. Earlier code
        // passed `null!` here with a comment claiming a post-construction
        // setter would fill it in, but no such setter exists on the listener
        // (the field is readonly), and the result was a
        // NullReferenceException the first time the listener tried to read
        // messageStore.Role. See GH-2668.
        options.Listeners.Add(new FlushOutgoingMessagesOnCommit(
            context,
            resolveSqliteMessageStore()));

        return options;
    }

    /// <summary>
    /// Resolve the SQL-Server-backed message store from the factory's
    /// <see cref="MessageStore"/>. Mirrors the resolution in
    /// <see cref="FisherEnvelopeTransaction"/>'s constructor — for a
    /// multi-tenanted runtime <c>runtime.Storage</c> is a
    /// <see cref="MultiTenantedMessageStore"/> wrapper around the
    /// SQL-Server-backed root, so a direct cast (the original GH-2668 fix)
    /// would <c>InvalidCastException</c> in that mode. Throws a clear error
    /// rather than NRE'ing in a Fisher session callback if the runtime
    /// isn't SQL-Server-backed at all.
    /// </summary>
    private SqliteMessageStore resolveSqliteMessageStore()
    {
        return MessageStore switch
        {
            SqliteMessageStore store => store,
            MultiTenantedMessageStore { Main: SqliteMessageStore mainStore } => mainStore,
            _ => throw new InvalidOperationException(
                "Wolverine.Fisher requires a SQLite-backed message store. " +
                $"The configured store was {MessageStore?.GetType().FullName ?? "null"}. " +
                "Call PersistMessagesWithSqlite(...) on WolverineOptions to wire one up.")
        };
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
            session.CurrentUserName = context.Envelope.UserName;
        }
        else if (context.UserName is not null)
        {
            session.CurrentUserName = context.UserName;
        }

        var transaction = new FisherEnvelopeTransaction(session, context);
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
