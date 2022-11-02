using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Baseline;
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
    private readonly IMessageLogger _messageLogger;
    private readonly IEnvelopePersistence _persistence;
    private readonly IMessageSerializer _serializer;
    private readonly AdvancedSettings _settings;
    private DurableReceiver? _receiver;
    private readonly ILogger _logger;
    private Restarter? _restarter;
    private readonly WolverineRuntime _runtime;
    private readonly RetryBlock<Envelope> _storeAndEnqueue;

    public DurableLocalQueue(Endpoint endpoint, WolverineRuntime runtime)
    {
        _settings = runtime.Advanced;
        _persistence = runtime.Persistence;
        _messageLogger = runtime.MessageLogger;
        _serializer = endpoint.DefaultSerializer ??
                      throw new ArgumentOutOfRangeException(nameof(endpoint),
                          "No default serializer for this Endpoint");
        Destination = endpoint.Uri;

        _runtime = runtime;

        Endpoint = endpoint;
        ReplyUri = TransportConstants.RepliesUri;

        _logger = runtime.Logger;

        if (endpoint.CircuitBreakerOptions != null)
        {
            CircuitBreaker = new CircuitBreaker(endpoint.CircuitBreakerOptions, this);
            Pipeline = new HandlerPipeline(runtime, new CircuitBreakerTrackedExecutorFactory(CircuitBreaker, runtime));
        }
        else
        {
            Pipeline = runtime.Pipeline;
        }

        _receiver = new DurableReceiver(endpoint, runtime, Pipeline);

        _storeAndEnqueue = new RetryBlock<Envelope>((e, _) => storeAndEnqueueAsync(e), _logger, _runtime.Cancellation);
    }

    public IHandlerPipeline Pipeline { get; }

    public CircuitBreaker? CircuitBreaker { get; }

    public Uri Destination { get; }

    public Endpoint Endpoint { get; }

    int IListenerCircuit.QueueCount => _receiver?.QueueCount ?? 0;

    void IListenerCircuit.EnqueueDirectly(IEnumerable<Envelope> envelopes)
    {
        if (_receiver == null) return;
        
        foreach (var envelope in envelopes)
        {
            _receiver.Enqueue(envelope);
        }
    }

    public Uri? ReplyUri { get; set; }

    public bool Latched { get; private set; }

    public bool IsDurable => true;

    public ValueTask EnqueueOutgoingAsync(Envelope envelope)
    {
        // The envelope would be persisted regardless
        if (Latched) return ValueTask.CompletedTask;
        
        _messageLogger.Sent(envelope);

        _receiver!.Enqueue(envelope);

        return ValueTask.CompletedTask;
    }

    public async ValueTask StoreAndForwardAsync(Envelope envelope)
    {
        // Try this first, let everything fail if it fails, don't want to log
        writeMessageData(envelope);
        
        _messageLogger.Sent(envelope);

        // TODO -- have to watch this one. Law of demeter violation
        envelope.Status = envelope.IsScheduledForLater(DateTimeOffset.UtcNow)
            ? EnvelopeStatus.Scheduled
            : EnvelopeStatus.Incoming;

        envelope.OwnerId = envelope.Status == EnvelopeStatus.Incoming
            ? _settings.UniqueNodeId
            : TransportConstants.AnyNode;

        if (Latched)
        {
            envelope.OwnerId = TransportConstants.AnyNode;
        }

        await _storeAndEnqueue.PostAsync(envelope);
    }

    private async Task storeAndEnqueueAsync(Envelope envelope)
    {
        try
        {
            await _persistence.StoreIncomingAsync(envelope);
        }
        catch (DuplicateIncomingEnvelopeException e)
        {
            _logger.LogError(e, "Duplicate incoming envelope detected");
            return;
        }

        if (Latched) return;

        if (envelope.Status == EnvelopeStatus.Incoming)
        {
            _receiver!.Enqueue(envelope);
        }
    }

    public bool SupportsNativeScheduledSend => true;


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

    public void Dispose()
    {
        _receiver?.SafeDispose();
        CircuitBreaker?.SafeDispose();
        _receiver?.SafeDispose();
        _storeAndEnqueue.SafeDispose();
    }

    public async ValueTask PauseAsync(TimeSpan pauseTime)
    {
        Latched = true;

        if (_receiver != null)
        {
            try
            {
                await _receiver.DrainAsync();
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Error trying to drain in flight messages for {Uri}", Destination);
            }
        }

        _receiver = null;

        CircuitBreaker?.Reset();
        
        _runtime.ListenerTracker.Publish(new ListenerState(Endpoint.Uri, Endpoint.EndpointName, ListeningStatus.Stopped));

        _logger.LogInformation("Pausing message listening at {Uri}", Endpoint.Uri);

        _restarter = new Restarter(this, pauseTime);

    }

    public ValueTask StartAsync()
    {
        _receiver = new DurableReceiver(Endpoint, _runtime, Pipeline);
        Latched = false;
        _runtime.ListenerTracker.Publish(new ListenerState(_receiver.Uri, Endpoint.EndpointName, ListeningStatus.Accepting));
        _restarter?.Dispose();
        _restarter = null;
        return ValueTask.CompletedTask;
    }

    ListeningStatus IListenerCircuit.Status => Latched ? ListeningStatus.TooBusy : ListeningStatus.Accepting;

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
        await _storeAndEnqueue.DrainAsync();
        await _receiver!.DrainAsync();
    }

    void ILocalQueue.Enqueue(Envelope envelope)
    {
        _receiver?.Enqueue(envelope);
    }

    int ILocalQueue.QueueCount => _receiver?.QueueCount ?? 0;
}
