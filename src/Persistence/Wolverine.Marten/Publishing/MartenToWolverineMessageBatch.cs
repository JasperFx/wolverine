using JasperFx.Events;
using Marten;
using Marten.Events.Aggregation;
using Marten.Internal.Sessions;
using Marten.Services;
using Wolverine.Runtime;

namespace Wolverine.Marten.Publishing;

internal class MartenToWolverineMessageBatch(MessageContext Context, DocumentSessionBase Session) : IMessageBatch
{
    private readonly List<ProjectionSideEffectContext> _sendsThemselves = new();

    public ValueTask PublishAsync<T>(T message, string tenantId)
    {
        return PublishAsync(message, new MessageMetadata(tenantId));
    }

    /// <summary>
    ///     Metadata-aware overload backing <see cref="IMessageSink.PublishAsync{T}(T, MessageMetadata)"/>
    ///     (JasperFx.Events 1.29+). Maps the incoming <see cref="MessageMetadata"/>
    ///     onto a <see cref="DeliveryOptions"/> so projection-authored side-effect
    ///     messages can override tenant, correlation id, causation id, and headers
    ///     on a per-message basis. See https://github.com/JasperFx/wolverine/issues/2545.
    /// </summary>
    public ValueTask PublishAsync<T>(T message, MessageMetadata metadata)
    {
        // MessageBus only lets an ISendMyself apply itself when no DeliveryOptions are passed, and a side
        // effect always has some, so it would otherwise be routed as the wrapper type and dropped.
        if (message is ISendMyself sendsItself)
        {
            return applyAsync(sendsItself, metadata);
        }

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

        return Context.PublishAsync(message, options);
    }

    private async ValueTask applyAsync(ISendMyself message, MessageMetadata metadata)
    {
        var context = new ProjectionSideEffectContext(Context.Runtime, metadata);
        context.OverrideStorage(Context.Storage);
        await context.EnlistInOutboxAsync(new MartenEnvelopeTransaction(Session, context));

        await message.ApplyAsync(context);

        lock (_sendsThemselves)
        {
            _sendsThemselves.Add(context);
        }
    }

    public async Task AfterCommitAsync(IDocumentSession session, IChangeSet commit, CancellationToken token)
    {
        await Context.FlushOutgoingMessagesAsync();

        foreach (var context in _sendsThemselves)
        {
            await context.FlushOutgoingMessagesAsync();
        }
    }

    public Task BeforeCommitAsync(IDocumentSession session, IChangeSet commit, CancellationToken token)
    {
        return Task.CompletedTask;
    }
}