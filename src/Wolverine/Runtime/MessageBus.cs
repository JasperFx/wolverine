using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using JasperFx.Core;
using Lamar;
using Wolverine.Configuration;
using Wolverine.Persistence.Durability;
using Wolverine.Runtime.ResponseReply;
using Wolverine.Runtime.Scheduled;
using Wolverine.Transports;
using Wolverine.Util;

namespace Wolverine.Runtime;

internal class DestinationEndpoint : IDestinationEndpoint
{
    private readonly Endpoint _endpoint;
    private readonly MessageBus _parent;

    public DestinationEndpoint(Endpoint endpoint, MessageBus parent)
    {
        _endpoint = endpoint;
        _parent = parent;
    }

    public Uri Uri => _endpoint.Uri;
    public string EndpointName => _endpoint.EndpointName;

    public ValueTask SendAsync<T>(T message, DeliveryOptions? options = null)
    {
        if (message == null || message.GetType() == typeof(Uri))
        {
            throw new ArgumentNullException(nameof(message));
        }

        var route = _endpoint.RouteFor(message.GetType(), _parent.Runtime);
        var envelope = new Envelope(message, _endpoint.Agent!);
        if (options != null && options.ContentType.IsNotEmpty() && options.ContentType != envelope.ContentType)
        {
            envelope.Serializer = _parent.Runtime.Options.FindSerializer(options.ContentType);
        }
        
        foreach (var rule in route.Rules) rule.Modify(envelope);

        // Delivery options win
        options?.Override(envelope);

        // adjust for local, scheduled send
        if (envelope.IsScheduledForLater(DateTimeOffset.Now) && !_endpoint.Agent!.SupportsNativeScheduledSend)
        {
            var localDurableQueue =
                _parent.Runtime.Endpoints.GetOrBuildSendingAgent(TransportConstants.DurableLocalUri);
            envelope = envelope.ForScheduledSend(localDurableQueue);
        }
        
        _parent.TrackEnvelopeCorrelation(envelope);

        return _parent.PersistOrSendAsync(envelope);
    }

    public Task<Acknowledgement> InvokeAsync(object message, CancellationToken cancellation = default, TimeSpan? timeout = null)
    {
        if (message == null)
        {
            throw new ArgumentNullException(nameof(message));
        }

        var route = _endpoint.RouteFor(message.GetType(), _parent.Runtime);
        return route.InvokeAsync<Acknowledgement>(message, _parent, cancellation, timeout);
    }

    public Task<T> InvokeAsync<T>(object message, CancellationToken cancellation = default, TimeSpan? timeout = null) where T : class
    {
        _parent.Runtime.RegisterMessageType(typeof(T));
        
        if (message == null)
        {
            throw new ArgumentNullException(nameof(message));
        }

        var route = _endpoint.RouteFor(message.GetType(), _parent.Runtime);
        return route.InvokeAsync<T>(message, _parent, cancellation, timeout);
    }
}

public class MessageBus : IMessageBus
{
    protected readonly List<Envelope> _outstanding = new();
    
    [DefaultConstructor]
    public MessageBus(IWolverineRuntime runtime) : this(runtime, Activity.Current?.RootId ?? Guid.NewGuid().ToString())
    {
    }

    public MessageBus(IWolverineRuntime runtime, string? correlationId)
    {
        Runtime = runtime;
        Storage = runtime.Storage;
        CorrelationId = correlationId;
    }

    public IDestinationEndpoint EndpointFor(string endpointName)
    {
        if (endpointName == null)
        {
            throw new ArgumentNullException(nameof(endpointName));
        }

        var endpoint = Runtime.Endpoints.EndpointByName(endpointName);
        if (endpoint == null)
            throw new ArgumentOutOfRangeException(nameof(endpointName), $"Unknown endpoint with name '{endpointName}'");

        return new DestinationEndpoint(endpoint, this);
    }

    public IDestinationEndpoint EndpointFor(Uri uri)
    {
        if (uri == null)
        {
            throw new ArgumentNullException(nameof(uri));
        }

        var sender = Runtime.Endpoints.GetOrBuildSendingAgent(uri).Endpoint;
        return new DestinationEndpoint(sender, this);
    }

    public string? CorrelationId { get; set; }

    public IWolverineRuntime Runtime { get; }
    public IMessageStore Storage { get; }
    
        public IEnumerable<Envelope> Outstanding => _outstanding;

    public IEnvelopeTransaction? Transaction { get; protected set; }
    public Guid ConversationId { get; protected set; }


    public Task InvokeAsync(object message, CancellationToken cancellation = default)
    {
        return Runtime.Pipeline.InvokeNowAsync(new Envelope(message)
        {
            ReplyUri = TransportConstants.RepliesUri,
            CorrelationId = CorrelationId,
            ConversationId = ConversationId,
            Source = Runtime.Advanced.ServiceName
        }, cancellation);
    }

    public async Task<T?> InvokeAsync<T>(object message, CancellationToken cancellation = default)
    {
        if (message == null)
        {
            throw new ArgumentNullException(nameof(message));
        }

        var envelope = new Envelope(message)
        {
            ReplyUri = TransportConstants.RepliesUri,
            ReplyRequested = typeof(T).ToMessageTypeName(),
            ResponseType = typeof(T),
            CorrelationId = CorrelationId,
            ConversationId = ConversationId,
            Source = Runtime.Advanced.ServiceName
        };

        await Runtime.Pipeline.InvokeNowAsync(envelope, cancellation);

        if (envelope.Response == null)
        {
            return default;
        }

        return (T)envelope.Response;
    }

    public async Task<Guid> ScheduleAsync<T>(T message, DateTimeOffset executionTime)
    {
        if (message == null)
        {
            throw new ArgumentNullException(nameof(message));
        }

        // TODO -- there's quite a bit of duplication here. Change that!
        var envelope = new Envelope(message)
        {
            ScheduledTime = executionTime,
            Destination = TransportConstants.DurableLocalUri,
            CorrelationId = CorrelationId,
            ConversationId = ConversationId,
            Source = Runtime.Advanced.ServiceName
        };

        // TODO -- memoize this.
        var endpoint = Runtime.Endpoints.EndpointFor(TransportConstants.DurableLocalUri);

        var writer = endpoint!.DefaultSerializer;
        envelope.Data = writer!.Write(envelope);
        envelope.ContentType = writer.ContentType;

        envelope.Status = EnvelopeStatus.Scheduled;
        envelope.OwnerId = TransportConstants.AnyNode;

        await ScheduleEnvelopeAsync(envelope);

        return envelope.Id;
    }

    public Task<Guid> ScheduleAsync<T>(T message, TimeSpan delay)
    {
        return ScheduleAsync(message, DateTimeOffset.UtcNow.Add(delay));
    }

    internal Task ScheduleEnvelopeAsync(Envelope envelope)
    {
        if (envelope.Message == null)
        {
            throw new ArgumentOutOfRangeException(nameof(envelope), "Envelope.Message is required");
        }

        if (!envelope.ScheduledTime.HasValue)
        {
            throw new ArgumentOutOfRangeException(nameof(envelope), "No value for ExecutionTime");
        }


        envelope.OwnerId = TransportConstants.AnyNode;
        envelope.Status = EnvelopeStatus.Scheduled;

        if (Transaction != null)
        {
            return Transaction.ScheduleJobAsync(envelope);
        }

        if (Storage is NullMessageStore)
        {
            Runtime.ScheduleLocalExecutionInMemory(envelope.ScheduledTime.Value, envelope);
            return Task.CompletedTask;
        }

        return Storage.ScheduleJobAsync(envelope);
    }

    internal async ValueTask PersistOrSendAsync(Envelope envelope)
    {
        if (envelope.Sender is null)
        {
            throw new InvalidOperationException("Envelope has not been routed");
        }

        if (Transaction is not null)
        {
            _outstanding.Fill(envelope);

            await envelope.PersistAsync(Transaction);

            return;
        }

        await envelope.StoreAndForwardAsync();
    }

    public void EnlistInOutbox(IEnvelopeTransaction transaction)
    {
        Transaction = transaction;
    }

    public Task EnlistInOutboxAsync(IEnvelopeTransaction transaction)
    {
        var original = Transaction;
        Transaction = transaction;

        return original == null
            ? Task.CompletedTask
            : original.CopyToAsync(transaction);
    }

    public IReadOnlyList<Envelope> PreviewSubscriptions(object message)
    {
        return Runtime.RoutingFor(message.GetType()).RouteForSend(message, null);

    }

    public ValueTask SendAsync<T>(T message, DeliveryOptions? options = null)
    {
        if (message == null)
        {
            throw new ArgumentNullException(nameof(message));
        }

        // Cannot trust the T here. Can be "object"
        var outgoing = Runtime.RoutingFor(message.GetType()).RouteForSend(message, options);
        trackEnvelopeCorrelation(outgoing);

        return PersistOrSendAsync(outgoing);
    }

    public ValueTask PublishAsync<T>(T message, DeliveryOptions? options = null)
    {
        if (message == null)
        {
            throw new ArgumentNullException(nameof(message));
        }

        // TODO -- eliminate this. Only happening for logging at this point. Check same in Send.
        var envelope = new Envelope(message);

        // You can't trust the T here.
        var outgoing = Runtime.RoutingFor(message.GetType()).RouteForPublish(message, options);
        trackEnvelopeCorrelation(outgoing);

        if (outgoing.Any())
        {
            return PersistOrSendAsync(outgoing);
        }

        Runtime.MessageLogger.NoRoutesFor(envelope);
        return ValueTask.CompletedTask;
    }


    public ValueTask SendToTopicAsync(string topicName, object message, DeliveryOptions? options = null)
    {
        if (message == null)
        {
            throw new ArgumentNullException(nameof(message));
        }

        var outgoing = Runtime.RoutingFor(message.GetType()).RouteToTopic(message, topicName, options);
        return PersistOrSendAsync(outgoing);
    }

    public ValueTask SendToEndpointAsync(string endpointName, object message, DeliveryOptions? options = null)
    {
        if (endpointName == null)
        {
            throw new ArgumentNullException(nameof(endpointName));
        }

        if (message == null)
        {
            throw new ArgumentNullException(nameof(message));
        }

        var outgoing = Runtime.RoutingFor(message.GetType())
            .RouteToEndpointByName(message, endpointName, options);

        return PersistOrSendAsync(outgoing);
    }

    /// <summary>
    ///     Send a message that should be executed at the given time
    /// </summary>
    /// <param name="message"></param>
    /// <param name="time"></param>
    /// <param name="options"></param>
    /// <typeparam name="T"></typeparam>
    public ValueTask SchedulePublishAsync<T>(T message, DateTimeOffset time, DeliveryOptions? options = null)
    {
        options ??= new DeliveryOptions();
        options.ScheduledTime = time;

        return PublishAsync(message, options);
    }

    /// <summary>
    ///     Send a message that should be executed after the given delay
    /// </summary>
    /// <param name="message"></param>
    /// <param name="delay"></param>
    /// <param name="options"></param>
    /// <typeparam name="T"></typeparam>
    public ValueTask SchedulePublishAsync<T>(T message, TimeSpan delay, DeliveryOptions? options = null)
    {
        if (message == null)
        {
            throw new ArgumentNullException(nameof(message));
        }

        options ??= new DeliveryOptions();
        options.ScheduleDelay = delay;
        return PublishAsync(message, options);
    }

    public Task<Acknowledgement> SendAndWaitAsync(object message, CancellationToken cancellation = default,
        TimeSpan? timeout = null)
    {
        if (message == null)
        {
            throw new ArgumentNullException(nameof(message));
        }

        return Runtime.RoutingFor(message.GetType()).FindSingleRouteForSending()
            .InvokeAsync<Acknowledgement>(message, this, cancellation, timeout);
    }

    public Task<T> RequestAsync<T>(object message, CancellationToken cancellation = default,
        TimeSpan? timeout = null) where T : class
    {
        if (message == null)
        {
            throw new ArgumentNullException(nameof(message));
        }

        // KEEP THIS IN MESSAGE PUBLISHER
        Runtime.RegisterMessageType(typeof(T));

        return Runtime.RoutingFor(message.GetType()).FindSingleRouteForSending()
            .InvokeAsync<T>(message, this, cancellation, timeout);
    }

    private void trackEnvelopeCorrelation(Envelope[] outgoing)
    {
        foreach (var outbound in outgoing) TrackEnvelopeCorrelation(outbound);
    }

    internal virtual void TrackEnvelopeCorrelation(Envelope outbound)
    {
        outbound.Source = Runtime.Advanced.ServiceName;
        outbound.CorrelationId = CorrelationId;
        outbound.ConversationId = outbound.Id; // the message chain originates here
    }

    internal async ValueTask PersistOrSendAsync(params Envelope[] outgoing)
    {
        if (Transaction != null)
        {
            // This filtering is done to only persist envelopes where 
            // the sender is currently latched
            var envelopes = outgoing.Where(isDurable).ToArray();
            foreach (var envelope in envelopes.Where(x => x.Sender is { Latched: true }))
                envelope.OwnerId = TransportConstants.AnyNode;

            await Transaction.PersistAsync(envelopes);

            _outstanding.Fill(outgoing);
        }
        else
        {
            foreach (var outgoingEnvelope in outgoing) await outgoingEnvelope.StoreAndForwardAsync();
        }
    }

    private bool isDurable(Envelope envelope)
    {
        return envelope.Sender?.IsDurable ?? Runtime.Endpoints.GetOrBuildSendingAgent(envelope.Destination!).IsDurable;
    }
}