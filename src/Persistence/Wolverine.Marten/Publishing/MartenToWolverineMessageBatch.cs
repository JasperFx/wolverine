using JasperFx.Events;
using Marten;
using Marten.Events.Aggregation;
using Marten.Internal.Sessions;
using Marten.Services;
using Wolverine.Runtime;

namespace Wolverine.Marten.Publishing;

#pragma warning disable CS9113 // Parameter is unread
internal class MartenToWolverineMessageBatch(MessageContext Context, DocumentSessionBase Session) : IMessageBatch
#pragma warning restore CS9113
{
    public ValueTask PublishAsync<T>(T message, string tenantId)
    {
        return Context.PublishAsync(message, new DeliveryOptions { TenantId = tenantId });
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

    public Task AfterCommitAsync(IDocumentSession session, IChangeSet commit, CancellationToken token)
    {
        return Context.FlushOutgoingMessagesAsync();
    }

    public Task BeforeCommitAsync(IDocumentSession session, IChangeSet commit, CancellationToken token)
    {
        return Task.CompletedTask;
    }
}