using JasperFx.Events;
using Polecat;
using Polecat.Events.Aggregation;
using Wolverine.Runtime;

namespace Wolverine.Polecat.Publishing;

/// <summary>
///     Polecat side of the projection-message bridge to Wolverine. One instance
///     per projection daemon batch; <see cref="IMessageOutbox.CreateBatch(IDocumentSession)"/>
///     vends a fresh batch on the first <c>slice.PublishMessage(...)</c> within a
///     given daemon write. Messages buffer in the underlying
///     <see cref="MessageContext"/> outbox and flush once the projection's SQL
///     transaction has committed durably (see <see cref="AfterCommitAsync"/>).
/// </summary>
/// <remarks>
///     Mirrors <see cref="Wolverine.Marten.Publishing.MartenToWolverineMessageBatch"/>.
///     The differences from Marten:
///     <list type="bullet">
///         <item><description>
///             Polecat's <see cref="IMessageBatch.BeforeCommitAsync"/> /
///             <see cref="IMessageBatch.AfterCommitAsync"/> take just a
///             <see cref="CancellationToken"/> — Polecat already owns the
///             session + commit context internally.
///         </description></item>
///         <item><description>
///             The session field is captured but unused at flush time (the message
///             flush goes through <see cref="MessageContext"/>, which already holds
///             the enlisted <see cref="PolecatEnvelopeTransaction"/>). Kept on the
///             constructor for symmetry with Marten and in case future per-batch
///             session-scoped behavior needs it.
///         </description></item>
///     </list>
/// </remarks>
#pragma warning disable CS9113 // Parameter is unread
internal class PolecatToWolverineMessageBatch(MessageContext Context, IDocumentSession Session) : IMessageBatch
#pragma warning restore CS9113
{
    public ValueTask PublishAsync<T>(T message, string tenantId)
    {
        return Context.PublishAsync(message, new DeliveryOptions { TenantId = tenantId });
    }

    /// <summary>
    ///     Metadata-aware overload backing <see cref="IMessageSink.PublishAsync{T}(T, MessageMetadata)"/>
    ///     (JasperFx.Events 2.0+). Maps the incoming <see cref="MessageMetadata"/>
    ///     onto a <see cref="DeliveryOptions"/> so projection-authored side-effect
    ///     messages can override tenant, correlation id, causation id, and headers
    ///     on a per-message basis. Behavior is identical to Marten's mapping; see
    ///     https://github.com/JasperFx/wolverine/issues/2545 for the original motivation.
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

    public Task BeforeCommitAsync(CancellationToken token)
    {
        // Wolverine's MessageContext flushes after commit (post-#2545); the
        // Marten bridge does the same. Polecat surfaces a pre-commit hook for
        // future "outbox row participates in the projection SQL transaction"
        // strategies, but the current bridge stays best-effort post-commit.
        return Task.CompletedTask;
    }

    public Task AfterCommitAsync(CancellationToken token)
    {
        return Context.FlushOutgoingMessagesAsync();
    }
}
