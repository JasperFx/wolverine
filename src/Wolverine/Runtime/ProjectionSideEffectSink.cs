using JasperFx.Events;
using Wolverine.Persistence.Durability;

namespace Wolverine.Runtime;

/// <summary>
///     The store agnostic half of a projection outbox batch: maps a projection's
///     <see cref="MessageMetadata" /> onto Wolverine delivery options, and applies an
///     <see cref="ISendMyself" /> against its own enlisted context instead of routing the wrapper
///     type. Marten, Polecat and Fisher all delegate their IMessageBatch implementations here so
///     the three bridges cannot drift apart again. See GH-2545 and GH-4556.
/// </summary>
internal class ProjectionSideEffectSink
{
    private readonly MessageContext _context;
    private readonly Func<MessageContext, IEnvelopeTransaction> _transactionSource;
    private readonly object _lock = new();

    // Lazily allocated. A sink is built for every commit of every session in any application
    // running IntegrateWithWolverine(), and the overwhelming majority of them never publish an
    // ISendMyself at all.
    private List<ProjectionSideEffectContext>? _sendsThemselves;

    /// <param name="context">The batch's own context, already enlisted in the store's session.</param>
    /// <param name="transactionSource">
    ///     Builds the store specific <see cref="IEnvelopeTransaction" /> for a side effect context, so
    ///     that a durable send from an <see cref="ISendMyself" /> still commits with the projection
    ///     rather than racing it.
    /// </param>
    public ProjectionSideEffectSink(MessageContext context,
        Func<MessageContext, IEnvelopeTransaction> transactionSource)
    {
        _context = context;
        _transactionSource = transactionSource;
    }

    public ValueTask PublishAsync<T>(T message, MessageMetadata metadata)
    {
        // MessageBus only lets an ISendMyself apply itself when NO DeliveryOptions are passed (the
        // guard that keeps TimeoutMessage from looping), and a side effect always has some -- at
        // minimum the tenant id. So a ToEndpoint() / DelayedFor() / ToWebSocketGroup() published
        // from RaiseSideEffects used to be routed as the wrapper type, find no subscriber, and be
        // dropped as NoRoutes. GH-4556.
        if (message is ISendMyself sendsItself)
        {
            return applyAsync(sendsItself, metadata);
        }

        return _context.PublishAsync(message, toDeliveryOptions(metadata));
    }

    /// <summary>
    ///     Maps the incoming <see cref="MessageMetadata" /> onto a <see cref="DeliveryOptions" /> so
    ///     projection authored side effect messages can override tenant, correlation id, causation id
    ///     and headers per message. See GH-2545.
    /// </summary>
    private static DeliveryOptions toDeliveryOptions(MessageMetadata metadata)
    {
        var options = new DeliveryOptions
        {
            TenantId = metadata.TenantId
        };

        if (metadata.CorrelationIdEnabled)
        {
            options.CorrelationId = metadata.CorrelationId;
        }

        if (metadata.CausationIdEnabled)
        {
            options.CausationId = metadata.CausationId;
        }

        if (metadata.HeadersEnabled)
        {
            foreach (var header in metadata.Headers!)
            {
                options.Headers[header.Key] = header.Value?.ToString();
            }
        }

        return options;
    }

    private async ValueTask applyAsync(ISendMyself message, MessageMetadata metadata)
    {
        var context = new ProjectionSideEffectContext(_context.Runtime, metadata, _context.CorrelationId);
        context.OverrideStorage(_context.Storage);
        await context.EnlistInOutboxAsync(_transactionSource(context));

        await message.ApplyAsync(context);

        lock (_lock)
        {
            (_sendsThemselves ??= new List<ProjectionSideEffectContext>(1)).Add(context);
        }
    }

    /// <summary>
    ///     Flush the batch's own context, then every side effect context built during it.
    /// </summary>
    /// <remarks>
    ///     The list is CLEARED here, and that matters: Marten only discards its cached message batch
    ///     after a SUCCESSFUL commit -- DocumentSessionBase.SaveChangesAsync does not reset it in a
    ///     finally -- so a session that retries a failed SaveChangesAsync() gets this same sink back.
    ///     Without the clear it would accumulate, and re-flush, every context from every failed
    ///     attempt for as long as the session lives.
    /// </remarks>
    public async Task FlushAsync()
    {
        await _context.FlushOutgoingMessagesAsync();

        ProjectionSideEffectContext[] contexts;
        lock (_lock)
        {
            if (_sendsThemselves is not { Count: > 0 })
            {
                return;
            }

            contexts = _sendsThemselves.ToArray();
            _sendsThemselves.Clear();
        }

        foreach (var context in contexts)
        {
            await context.FlushOutgoingMessagesAsync();
        }
    }
}
