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
using Wolverine.Util.Dataflow;

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
        _inbox = runtime.Storage.Inbox;
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
            CircuitBreaker = new CircuitBreaker(endpoint.CircuitBreakerOptions, this);
            Pipeline = new HandlerPipeline(runtime, new CircuitBreakerTrackedExecutorFactory(CircuitBreaker, runtime), endpoint)
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

    public Uri Uri { get;  }

    public IHandlerPipeline Pipeline { get; }

    public CircuitBreaker? CircuitBreaker { get; }

    int IListenerCircuit.QueueCount => _receiver?.QueueCount ?? 0;

    Task IListenerCircuit.EnqueueDirectlyAsync(IEnumerable<Envelope> envelopes)
    {
        if (_receiver == null)
        {
            return Task.CompletedTask;
        }

        foreach (var envelope in envelopes) _receiver.Enqueue(envelope);

        return Task.CompletedTask;
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
        _receiver.Latch();
        await _storeAndEnqueue.DrainAsync();
        await _receiver!.DrainAsync();
    }

    void ILocalReceiver.Enqueue(Envelope envelope)
    {
        _receiver?.Enqueue(envelope);
    }

    int ILocalQueue.QueueCount => _receiver?.QueueCount ?? 0;

    public Uri Destination { get; }

    public Endpoint Endpoint { get; }

    public Uri? ReplyUri { get; set; }

    public bool Latched { get; private set; }

    public bool IsDurable => true;

    public ValueTask EnqueueOutgoingAsync(Envelope envelope)
    {
        // The envelope would be persisted regardless
        if (Latched)
        {
            return ValueTask.CompletedTask;
        }

        _messageLogger.Sent(envelope);

        _receiver!.Enqueue(envelope);

        return ValueTask.CompletedTask;
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

    private async Task storeAndEnqueueAsync(Envelope envelope)
    {
        try
        {
            await _inbox.StoreIncomingAsync(envelope);
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
            _receiver!.Enqueue(envelope);
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