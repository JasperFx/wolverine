using Microsoft.AspNetCore.SignalR;
using System.Text.Json;
using Wolverine.Configuration;

namespace Wolverine.SignalR.Internals;

public class SignalRSubscriberConfiguration : SubscriberConfiguration<SignalRSubscriberConfiguration, SignalRTransport>
{
    internal SignalRSubscriberConfiguration(SignalRTransport endpoint) : base(endpoint)
    {
    }
    
    /// <summary>
    /// Override the JSON serialization settings
    /// </summary>
    /// <param name="options"></param>
    /// <returns></returns>
    public SignalRSubscriberConfiguration OverrideJson(JsonSerializerOptions options)
    {
        add(e => e.JsonOptions = options);
        return this;
    }

    /// <summary>
    ///     GH-3972. Coalesce outgoing messages into a single envelope per destination on a flush interval,
    ///     rather than sending one SignalR invocation per message.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         This is a sender-side buffer, which is the point: the alternative — routing outbound messages
    ///         through a local queue to get them batched — makes the queue a cascade target for its own
    ///         handlers, and that recursion is the direct cause of a whole family of hard-to-see bugs. Nothing
    ///         round-trips a queue here, so there is no queue to re-enter, and the buffer sits <b>after</b> the
    ///         outbox rather than before it.
    ///     </para>
    ///     <para>
    ///         Buffers are keyed by destination, so a message bound for one connection is never coalesced
    ///         together with one bound for another.
    ///     </para>
    ///     <para>
    ///         Batches are delivered on the <c>ReceiveCoalescedMessages</c> client operation, carrying the
    ///         individual CloudEvents documents in arrival order. Clients must handle that operation to receive
    ///         them; Wolverine's own SignalR client does so automatically.
    ///     </para>
    /// </remarks>
    /// <example>
    ///     <code>
    ///     opts.PublishAllMessages().ToSignalR()
    ///         .CoalesceOutgoing(o =>
    ///         {
    ///             o.FlushInterval = 100.Milliseconds();
    ///             o.MaxBatchSize  = 200;
    ///         });
    ///     </code>
    /// </example>
    public SignalRSubscriberConfiguration CoalesceOutgoing(Action<OutgoingCoalescingOptions>? configure = null)
    {
        add(e =>
        {
            var options = new OutgoingCoalescingOptions();
            configure?.Invoke(options);
            e.Coalescing = options;
        });

        return this;
    }
}