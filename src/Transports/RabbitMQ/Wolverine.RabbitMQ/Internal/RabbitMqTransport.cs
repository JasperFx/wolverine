using JasperFx.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RabbitMQ.Client;
using Spectre.Console;
using Wolverine.Configuration;
using Wolverine.Runtime;
using Wolverine.Transports;

namespace Wolverine.RabbitMQ.Internal;

public partial class RabbitMqTransport : BrokerTransport<RabbitMqEndpoint>, IDisposable
{
    public const string ProtocolName = "rabbitmq";
    public const string ResponseEndpointName = "RabbitMqResponses";
    public const string DeadLetterQueueName = "wolverine-dead-letter-queue";
    public const string DeadLetterQueueHeader = "x-dead-letter-exchange";

    private ConnectionMonitor? _listenerConnection;
    private ConnectionMonitor? _sendingConnection;

    public RabbitMqTransport() : base(ProtocolName, "Rabbit MQ")
    {
        ConnectionFactory.AutomaticRecoveryEnabled = true;
        ConnectionFactory.DispatchConsumersAsync = true;

        Queues = new LightweightCache<string, RabbitMqQueue>(name => new RabbitMqQueue(name, this));
        Exchanges = new LightweightCache<string, RabbitMqExchange>(name => new RabbitMqExchange(name, this));

        Topics = new LightweightCache<Uri, RabbitMqTopicEndpoint>(uri =>
        {
            if (uri.Host != RabbitMqEndpoint.TopicSegment)
            {
                throw new ArgumentOutOfRangeException(nameof(uri));
            }

            var exchangeName = uri.Segments[1].TrimEnd('/');
            var exchange = Exchanges[exchangeName];
            exchange.ExchangeType = ExchangeType.Topic;
            return new RabbitMqTopicEndpoint(uri.Segments.Last(), exchange, this);
        });
    }

    public DeadLetterQueue DeadLetterQueue { get; } = new(DeadLetterQueueName);

    internal RabbitMqChannelCallback? Callback { get; private set; }

    internal ConnectionMonitor ListeningConnection => _listenerConnection ??= BuildConnection(ConnectionRole.Listening);
    internal ConnectionMonitor SendingConnection => _sendingConnection ??= BuildConnection(ConnectionRole.Sending);

    public ConnectionFactory ConnectionFactory { get; } = new(){ClientProvidedName = "Wolverine"};

    public IList<AmqpTcpEndpoint> AmqpTcpEndpoints { get; } = new List<AmqpTcpEndpoint>();

    public LightweightCache<Uri, RabbitMqTopicEndpoint> Topics { get; }
    public LightweightCache<string, RabbitMqExchange> Exchanges { get; }

    public LightweightCache<string, RabbitMqQueue> Queues { get; }

    internal bool DeclareRequestReplySystemQueue { get; set; } = true;
    internal bool UseSenderConnectionOnly { get; set; }
    internal bool UseListenerConnectionOnly { get; set; }

    public void Dispose()
    {
        try
        {
            _listenerConnection?.SafeDispose();
        }
        catch (ObjectDisposedException)
        {

        }

        try
        {
            _sendingConnection?.SafeDispose();
        }
        catch (ObjectDisposedException)
        {

        }

        Callback?.SafeDispose();

        foreach (var queue in Queues)
        {
            queue.SafeDispose();
        }
    }

    public override ValueTask ConnectAsync(IWolverineRuntime runtime)
    {
        Logger = runtime.LoggerFactory.CreateLogger<RabbitMqTransport>();
        Callback = new RabbitMqChannelCallback(Logger, runtime.DurabilitySettings.Cancellation);

        ConnectionFactory.DispatchConsumersAsync = true;

        if (_listenerConnection == null && !UseSenderConnectionOnly)
        {
            _listenerConnection = BuildConnection(ConnectionRole.Listening);
        }

        if (_sendingConnection == null && !UseListenerConnectionOnly)
        {
            _sendingConnection = BuildConnection(ConnectionRole.Sending);
        }

        return ValueTask.CompletedTask;
    }

    protected override IEnumerable<RabbitMqEndpoint> endpoints()
    {
        foreach (var exchange in Exchanges)
        {
            yield return exchange;

            foreach (var topic in exchange.Topics) yield return topic;

            foreach (var routing in exchange.Routings)
            {
                yield return routing;
            }
        }

        foreach (var queue in Queues) yield return queue;
    }

    protected override RabbitMqEndpoint findEndpointByUri(Uri uri)
    {
        var type = uri.Host;

        var name = uri.Segments[1].TrimEnd('/');
        switch (type)
        {
            case RabbitMqEndpoint.QueueSegment:
                return Queues[name];

            case RabbitMqEndpoint.ExchangeSegment:
                var exchange = Exchanges[name];

                if (uri.Segments.Any(x => x.EqualsIgnoreCase("routing/")))
                {
                    return exchange.Routings[uri.Segments.Last()];
                }

                return exchange;

            case RabbitMqEndpoint.TopicSegment:
                return Topics[uri];

            default:
                throw new ArgumentOutOfRangeException(nameof(uri), $"Invalid Rabbit MQ object type '{type}'");
        }
    }

    protected override void tryBuildSystemEndpoints(IWolverineRuntime runtime)
    {
        if (DeclareRequestReplySystemQueue)
        {
            var queueName = $"wolverine.response.{runtime.DurabilitySettings.AssignedNodeNumber}";

            var queue = new RabbitMqQueue(queueName, this, EndpointRole.System)
            {
                AutoDelete = true,
                IsDurable = false,
                IsListener = true,
                IsUsedForReplies = true,
                ListenerCount = 1,
                EndpointName = ResponseEndpointName
            };

            Queues[queueName] = queue;
        }

        // Have to do this early to get everything together for the dead letter queues
        foreach (var rabbitMqQueue in Queues)
        {
            rabbitMqQueue.Compile(runtime);
        }

        foreach (var deadLetterQueue in enabledDeadLetterQueues().Distinct().ToArray())
        {
            var dlq = Queues[deadLetterQueue.QueueName];
            deadLetterQueue.ConfigureQueue?.Invoke(dlq);

            var dlqExchange = Exchanges[deadLetterQueue.ExchangeName];
            deadLetterQueue.ConfigureExchange?.Invoke(dlqExchange);

            dlqExchange.BindQueue(deadLetterQueue.QueueName, deadLetterQueue.BindingName);
        }
    }

    private IEnumerable<DeadLetterQueue> enabledDeadLetterQueues()
    {
        if (DeadLetterQueue.Mode != DeadLetterQueueMode.WolverineStorage)
        {
            yield return DeadLetterQueue;
        }

        foreach (var queue in Queues)
        {
            if (queue.IsDurable && queue.Role == EndpointRole.Application && queue.DeadLetterQueue != null && queue.DeadLetterQueue.Mode != DeadLetterQueueMode.WolverineStorage)
            {
                yield return queue.DeadLetterQueue;
            }
        }
    }

    internal ConnectionMonitor BuildConnection(ConnectionRole role)
    {
        return new ConnectionMonitor(this, role);
    }

    public ILogger<RabbitMqTransport> Logger { get; private set; } = NullLogger<RabbitMqTransport>.Instance;
    
    /// <summary>
    /// Opt into making Wolverine "auto ping" new listeners by trying to send a fake Wolverine "ping" message
    /// This *might* assist in Wolverine auto-starting rabbit mq connections that have failed on the Rabbit MQ side
    /// </summary>
    public bool AutoPingListeners { get; set; } = false;

    public RabbitMqQueue EndpointForQueue(string queueName)
    {
        return Queues[queueName];
    }

    public RabbitMqExchange EndpointForExchange(string exchangeName)
    {
        return Exchanges[exchangeName];
    }

    public IEnumerable<RabbitMqBinding> Bindings()
    {
        return Exchanges.SelectMany(x => x.Bindings());
    }

    public override IEnumerable<PropertyColumn> DiagnosticColumns()
    {
        yield return new PropertyColumn("Queue Name", "name");
        yield return new PropertyColumn("Message Count", "count", Justify.Right);
    }
}