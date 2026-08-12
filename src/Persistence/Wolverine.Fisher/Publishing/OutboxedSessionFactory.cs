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

    public OutboxedSessionFactory(ISessionFactory factory, IWolverineRuntime runtime, IDocumentStore store)
    {
        _factory = factory;
        _store = store;

        _shouldPublishEvents = runtime.TryFindExtension<FisherIntegration>()?.UseFastEventForwarding ?? false;
        _shouldTrackAppends = runtime.Options.Tracking.EnableEventAppendTracking;

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

        // Add listeners before session creation (Fisher requirement)
        if (_shouldPublishEvents)
        {
            options.Listeners.Add(new PublishIncomingEventsBeforeCommit(context));
        }

        if (_shouldTrackAppends)
        {
            options.Listeners.Add(new NotifyObserverOfAppendedEvents(context));
        }

        // The FlushOutgoingMessagesOnCommit listener needs the SQLite
        // message store so it can mark the incoming envelope as Handled in
        // the same transaction as the document changes. The factory's
        // MessageStore property carries this from runtime.Storage at ctor
        // time — earlier code passed `null!` here with a comment claiming a
        // post-construction setter would fill it in, but no such setter
        // exists on the listener (the field is readonly), and the result
        // was a NullReferenceException the first time the listener tried
        // to read messageStore.Role. See GH-2668.
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
