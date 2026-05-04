using JasperFx.Blocks;
using JasperFx.Core;
using Microsoft.Extensions.Logging;
using Wolverine.Configuration;
using Wolverine.ErrorHandling;
using Wolverine.Logging;
using Wolverine.Persistence.Durability;
using Wolverine.Runtime;
using Wolverine.Runtime.Serialization;
using Wolverine.Runtime.WorkerQueues;
using Wolverine.Transports.Sending;

namespace Wolverine.Transports.Local;

internal class DurableLocalQueue : ISendingAgent, IListenerCircuit, ILocalQueue
{
    private readonly IMessageInbox _inbox;
    private readonly ILogger _logger;
    private readonly IMessageTracker _messageLogger;
    private readonly WolverineRuntime _runtime;
    private readonly IMessageSerializer _serializer;
    private readonly DurabilitySettings _settings;
    private readonly RetryBlock<Envelope> _storeAndEnqueue;
    private DurableReceiver? _receiver;
    private Restarter? _restarter;

    public DurableLocalQueue(Endpoint endpoint, WolverineRuntime runtime)
    {
        Uri = endpoint.Uri;
        _settings = runtime.DurabilitySettings;

        // When ancillary stores exist, wrap the inbox so that envelopes whose
        // Store property has already been stamped (by ApplyAncillaryStoreFrame
        // during handler execution) are persisted in the correct database.
        // Without this, all local-queue messages land in the main store's inbox
        // regardless of the handler's ancillary store association.
        _inbox = runtime.Stores != null && runtime.Stores.HasAnyAncillaryStores()
            ? new DelegatingMessageInbox(runtime.Storage.Inbox, runtime.Stores)
            : runtime.Storage.Inbox;

        _messageLogger = runtime.MessageTracking;
        _serializer = endpoint.DefaultSerializer ??
                      throw new ArgumentOutOfRangeException(nameof(endpoint),
                          "No default serializer for this Endpoint");
        Destination = endpoint.Uri;

        _runtime = runtime;

        Endpoint = endpoint;
        ReplyUri = TransportConstants.RepliesUri;

        _logger = runtime.LoggerFactory.CreateLogger<DurableLocalQueue>();

        if (endpoint.CircuitBreakerOptions != null)
        {
            CircuitBreaker = new CircuitBreaker(endpoint.CircuitBreakerOptions, this, runtime.Observer);
            Pipeline = new HandlerPipeline(runtime, new CircuitBreakerTrackedExecutorFactory(CircuitBreaker, runtime),
                endpoint)
            {
                TelemetryEnabled = endpoint.TelemetryEnabled
            };
        }
        else
        {
            Pipeline = new HandlerPipeline(runtime, runtime, endpoint);
        }

        _receiver = new DurableReceiver(endpoint, runtime, Pipeline);

        _storeAndEnqueue = new RetryBlock<Envelope>((e, _) => storeAndEnqueueAsync(e), _logger, _runtime.Cancellation);
    }

    public CircuitBreaker? CircuitBreaker { get; }

    /// <summary>
    /// Immediately latch the receiver to stop processing new messages.
    /// </summary>
    public void LatchReceiver()
    {
        Latched = true;
        _receiver?.Latch();
    }

    int IListenerCircuit.QueueCount => _receiver?.QueueCount ?? 0;

    async Task IListenerCircuit.EnqueueDirectlyAsync(IEnumerable<Envelope> envelopes)
    {
        if (_receiver == null)
        {
            return;
        }

        foreach (var envelope in envelopes) await _receiver.EnqueueAsync(envelope);
    }

    public async ValueTask PauseAsync(TimeSpan pauseTime)
    {
        Latched = true;

        if (_receiver != null)
        {
            try
            {
                _receiver.Latch();
                await _receiver.DrainAsync();
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Error trying to drain in flight messages for {Uri}", Destination);
            }
        }

        _receiver = null;

        CircuitBreaker?.Reset();

        _runtime.Tracker.Publish(
            new ListenerState(Endpoint.Uri, Endpoint.EndpointName, ListeningStatus.Stopped));

        _logger.LogInformation("Pausing message listening at {Uri}", Endpoint.Uri);

        _restarter = new Restarter(this, pauseTime);
    }

    public ValueTask StartAsync()
    {
        _receiver = new DurableReceiver(Endpoint, _runtime, Pipeline);
        Latched = false;
        _runtime.Tracker.Publish(new ListenerState(_receiver.Uri, Endpoint.EndpointName,
            ListeningStatus.Accepting));
        _restarter?.Dispose();
        _restarter = null;
        return ValueTask.CompletedTask;
    }

    ListeningStatus IListenerCircuit.Status => Latched ? ListeningStatus.TooBusy : ListeningStatus.Accepting;

    public Uri Uri { get; }

    public IHandlerPipeline Pipeline { get; }

    public void Dispose()
    {
        _receiver?.SafeDispose();
        CircuitBreaker?.SafeDisposeSynchronously();
        _receiver?.SafeDispose();
        _storeAndEnqueue.SafeDispose();
    }

    ValueTask IReceiver.ReceivedAsync(IListener listener, Envelope[] messages)
    {
        return _receiver!.ReceivedAsync(listener, messages);
    }

    ValueTask IReceiver.ReceivedAsync(IListener listener, Envelope envelope)
    {
        return _receiver!.ReceivedAsync(listener, envelope);
    }

    async ValueTask IReceiver.DrainAsync()
    {
        _receiver!.Latch();
        await _storeAndEnqueue.DrainAsync();
        await _receiver!.DrainAsync();
    }

    void ILocalReceiver.Enqueue(Envelope envelope)
    {
        _receiver?.Enqueue(envelope);
    }

    ValueTask ILocalReceiver.EnqueueAsync(Envelope envelope)
    {
        return _receiver!.EnqueueAsync(envelope);
    }

    int ILocalQueue.QueueCount => _receiver?.QueueCount ?? 0;

    public Uri Destination { get; }

    public Endpoint Endpoint { get; }

    public Uri? ReplyUri { get; set; }

    public bool Latched { get; private set; }

    public bool IsDurable => true;

    public async ValueTask EnqueueOutgoingAsync(Envelope envelope)
    {
        // The envelope would be persisted regardless
        if (Latched)
        {
            return;
        }

        _messageLogger.Sent(envelope);

        await _receiver!.EnqueueAsync(envelope);
    }

    public ValueTask StoreAndForwardAsync(Envelope envelope)
    {
        // Try this first, let everything fail if it fails, don't want to log
        writeMessageData(envelope);

        _messageLogger.Sent(envelope);

        envelope.PrepareForIncomingPersistence(DateTimeOffset.UtcNow, _settings);

        if (Latched)
        {
            envelope.OwnerId = TransportConstants.AnyNode;
        }

        return new ValueTask(_storeAndEnqueue.PostAsync(envelope));
    }

    public bool SupportsNativeScheduledSend => true;

    public DateTimeOffset LastMessageSentAt => DateTimeOffset.UtcNow;

    /// <summary>
    /// If the handler for this message type targets an ancillary store on a
    /// different database, set envelope.Store so that the DelegatingMessageInbox
    /// persists it in the correct store for transactional atomicity. The
    /// receiving handler's store association wins over the publishing context's
    /// store: a message published from a main-store handler can be persisted
    /// transactionally by an ancillary-store handler. Without overriding here,
    /// a publisher-stamped envelope.Store (the main store) would carry through
    /// the inbox and cause FlushOutgoingMessagesOnCommit to point at the
    /// publisher's inbox table while the receiving Marten/Polecat session was
    /// connected to the ancillary database. See GH-2669.
    /// </summary>
    private void assignAncillaryStoreIfNeeded(Envelope envelope)
    {
        if (_runtime.Stores == null) return;
        var store = _runtime.Stores.TryFindAncillaryStoreForMessageType(envelope.MessageType);
        if (store != null)
        {
            envelope.Store = store;
        }
    }

    private async Task storeAndEnqueueAsync(Envelope envelope)
    {
        try
        {
            envelope.OwnerId = _settings.AssignedNodeNumber;
            assignAncillaryStoreIfNeeded(envelope);
            await _inbox.StoreIncomingAsync(envelope);
            envelope.WasPersistedInInbox = true;
        }
        catch (DuplicateIncomingEnvelopeException e)
        {
            _logger.LogError(e, "Duplicate incoming envelope detected");
            return;
        }

        if (Latched)
        {
            return;
        }

        if (envelope.Status == EnvelopeStatus.Incoming)
        {
            await _receiver!.EnqueueAsync(envelope);
        }
    }

    private void writeMessageData(Envelope envelope)
    {
        if (envelope.Message is null)
        {
            throw new ArgumentOutOfRangeException(nameof(envelope), "Envelope.Message is null");
        }

        if (envelope.Data == null || envelope.Data.Length == 0)
        {
            _serializer.Write(envelope);
            envelope.ContentType = _serializer.ContentType;
        }
    }
}