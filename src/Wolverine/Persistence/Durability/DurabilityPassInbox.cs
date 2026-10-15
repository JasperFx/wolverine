using Microsoft.Extensions.Logging;
using Wolverine.Configuration;
using Wolverine.Logging;
using Wolverine.Runtime;
using Wolverine.Runtime.Routing;
using Wolverine.Runtime.Serialization;
using Wolverine.Transports;
using Wolverine.Transports.Local;
using Wolverine.Transports.Sending;

namespace Wolverine.Persistence.Durability;

/// <summary>
///     GH-5046. Where <see cref="DurabilityMode.ServerlessWithOutbox" /> sends the messages this application handles
///     itself. There are no local queues in that mode, and nothing can be trusted to run after a request completes,
///     so a locally handled, locally scheduled or wrapped scheduled message is written to the durable inbox, owned by
///     any node (0), and the next durability pass executes it: an incoming row at once, a scheduled row when it is
///     due. Within a handler's transaction the row is written by that transaction, exactly as a durable local queue's
///     would be, and this agent only writes it itself when there is no transaction. Nothing is ever handed to an
///     in-memory queue.
/// </summary>
/// <remarks>
///     It sits at <see cref="TransportConstants.DurableLocalUri" /> in place of the durable local queue, so routing,
///     <c>IMessageBus.EndpointFor(...)</c> and the wrapping of a scheduled send to a transport that cannot schedule
///     natively all find it where they would find that queue in the other modes.
/// </remarks>
internal class DurabilityPassInbox : ISendingAgent
{
    private readonly WolverineRuntime _runtime;
    private readonly IMessageInbox _inbox;
    private readonly IMessageTracker _messageLogger;
    private readonly IMessageSerializer _serializer;
    private readonly ILogger _logger;

    // Captured before the mode removes the local transport and turns off conventional local routing
    private readonly bool _routesHandledMessageTypes;
    private readonly Endpoint[] _explicitLocalQueues;

    public DurabilityPassInbox(WolverineRuntime runtime, LocalTransport? localTransport, bool routesHandledMessageTypes)
    {
        _runtime = runtime;
        _routesHandledMessageTypes = routesHandledMessageTypes;

        // An explicit PublishMessage<T>().ToLocalQueue("name") rule
        _explicitLocalQueues = localTransport?.AllQueues()
            .Where(x => x.Role == EndpointRole.Application && x.Subscriptions.Count != 0)
            .Cast<Endpoint>()
            .ToArray() ?? [];

        var endpoint = new LocalQueue(TransportConstants.Durable)
        {
            Mode = EndpointMode.Durable,
            Role = EndpointRole.System
        };

        endpoint.Compile(runtime);
        endpoint.Agent = this;
        Endpoint = endpoint;

        _serializer = endpoint.DefaultSerializer ?? runtime.Options.DefaultSerializer;
        _messageLogger = runtime.MessageTrackingFor(endpoint);
        _logger = runtime.LoggerFactory.CreateLogger<DurabilityPassInbox>();

        // Same as the durable local queue: an envelope already stamped with an ancillary store is written there
        _inbox = runtime.Stores != null && runtime.Stores.HasAnyAncillaryStores()
            ? new DelegatingMessageInbox(runtime.Storage.Inbox, runtime.Stores)
            : runtime.Storage.Inbox;
    }

    /// <summary>
    ///     The route for an explicit rule to a local queue, if there is one for this message type
    /// </summary>
    internal IEnumerable<IMessageRoute> ExplicitRoutesFor(Type messageType)
    {
        foreach (var queue in _explicitLocalQueues)
        {
            if (queue.ShouldSendMessage(messageType))
            {
                // Every one of them would run the same handlers in a pass, so one row is enough
                yield return MessageRoute.For(messageType, Endpoint, _runtime);
                yield break;
            }
        }
    }

    /// <summary>
    ///     What conventional local routing would find in the other modes: a route for any message type this
    ///     application handles, unless the application turned that convention off itself
    /// </summary>
    internal IEnumerable<IMessageRoute> ConventionalRoutesFor(Type messageType)
    {
        if (_routesHandledMessageTypes && _runtime.Options.HandlerGraph.CanHandle(messageType))
        {
            yield return MessageRoute.For(messageType, Endpoint, _runtime);
        }
    }

    public Uri Destination => TransportConstants.DurableLocalUri;

    public Uri? ReplyUri { get; set; } = TransportConstants.RepliesUri;

    public bool Latched => false;

    public bool IsDurable => true;

    // Every scheduled row is released by a pass, so nothing sent here needs wrapping
    public bool SupportsNativeScheduledSend => true;

    public Endpoint Endpoint { get; }

    public DateTimeOffset LastMessageSentAt => DateTimeOffset.UtcNow;

    // The envelope was already written by the outbox transaction it was persisted with. The pass runs it
    public ValueTask EnqueueOutgoingAsync(Envelope envelope)
    {
        _messageLogger.Sent(envelope);
        return ValueTask.CompletedTask;
    }

    // No transaction to write it with, so write it now
    public ValueTask StoreAndForwardAsync(Envelope envelope)
    {
        if (envelope.Data == null || envelope.Data.Length == 0)
        {
            if (envelope.Message is null)
            {
                throw new ArgumentOutOfRangeException(nameof(envelope), "Envelope.Message is null");
            }

            _serializer.Write(envelope);
            envelope.ContentType = _serializer.ContentType;
        }

        envelope.Status = envelope.IsScheduledForLater(DateTimeOffset.UtcNow)
            ? EnvelopeStatus.Scheduled
            : EnvelopeStatus.Incoming;
        envelope.OwnerId = TransportConstants.AnyNode;

        return new ValueTask(storeAsync(envelope));
    }

    private async Task storeAsync(Envelope envelope)
    {
        assignAncillaryStoreIfNeeded(envelope);

        try
        {
            await DurableWriteRetry.ExecuteAsync(() => _inbox.StoreIncomingAsync(envelope), envelope, _logger,
                _runtime.Cancellation).ConfigureAwait(false);

            envelope.WasPersistedInInbox = true;
        }
        catch (DuplicateIncomingEnvelopeException e)
        {
            _logger.LogError(e, "Duplicate incoming envelope detected");
            return;
        }

        _messageLogger.Sent(envelope);
    }

    // See DurableLocalQueue: the handler's store association wins over the publishing context's store
    private void assignAncillaryStoreIfNeeded(Envelope envelope)
    {
        if (_runtime.Stores == null) return;

        var store = _runtime.Stores.TryFindAncillaryStoreForMessageType(Endpoint.Uri, envelope.MessageType);
        if (store != null)
        {
            envelope.Store = store;
        }
    }
}
