using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using JasperFx.Core;
using Microsoft.Extensions.Logging;
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

    private IConnection? _listenerConnection;
    private IConnection? _sendingConnection;

    public RabbitMqTransport() : base(ProtocolName, "Rabbit MQ")
    {
        ConnectionFactory.AutomaticRecoveryEnabled = true;
        Queues = new(name => new RabbitMqQueue(name, this));

        Exchanges = new(name => new RabbitMqExchange(name, this));

        Topics = new(uri =>
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

    internal RabbitMqChannelCallback Callback { get; private set; }

    internal IConnection ListeningConnection => _listenerConnection ??= BuildConnection();
    internal IConnection SendingConnection => _sendingConnection ??= BuildConnection();

    public ConnectionFactory ConnectionFactory { get; } = new();

    public IList<AmqpTcpEndpoint> AmqpTcpEndpoints { get; } = new List<AmqpTcpEndpoint>();

    public LightweightCache<Uri, RabbitMqTopicEndpoint> Topics { get; }
    public LightweightCache<string, RabbitMqExchange> Exchanges { get; }

    public LightweightCache<string, RabbitMqQueue> Queues { get; }

    public void Dispose()
    {
        _listenerConnection?.Close();
        _listenerConnection?.SafeDispose();

        _sendingConnection?.Close();
        _sendingConnection?.SafeDispose();

        Callback?.SafeDispose();
    }


    public override ValueTask ConnectAsync(IWolverineRuntime runtime)
    {
        var logger = runtime.LoggerFactory.CreateLogger<RabbitMqTransport>();   
        Callback = new RabbitMqChannelCallback(logger, runtime.DurabilitySettings.Cancellation);

        ConnectionFactory.DispatchConsumersAsync = true;

        // TODO -- log the connection
        if (_listenerConnection == null)
        {
            _listenerConnection = BuildConnection();
            listenToEvents("Listener", _listenerConnection, logger);
        }

        if (_sendingConnection == null)
        {
            _sendingConnection = BuildConnection();
            listenToEvents("Sender", _listenerConnection, logger);
        }

        return ValueTask.CompletedTask;
    }

    private void listenToEvents(string connectionName, IConnection connection, ILogger logger)
    {
        connection.CallbackException += (sender, args) =>
        {
            logger.LogError(args.Exception, "Rabbit Mq connection callback exception on {Name} connection", connectionName);
        };

        connection.ConnectionBlocked += (sender, args) =>
        {
            logger.LogInformation("Rabbit Mq {Name} connection was blocked with reason {Reason}", connectionName, args.Reason);
        };

        connection.ConnectionShutdown += (sender, args) =>
        {
            logger.LogInformation("Rabbit Mq connection {Name} was shutdown with Cause {Cause}, Initiator {Initiator}, ClassId {ClassId}, MethodId {MethodId}, ReplyCode {ReplyCode}, and ReplyText {ReplyText}"
                , connectionName, args.Cause, args.Initiator, args.ClassId, args.MethodId, args.ReplyCode, args.ReplyText);
        };

        connection.ConnectionUnblocked += (sender, args) =>
        {
            logger.LogInformation("Rabbit Mq connection {Name} was un-blocked");
        };
        
    }

    protected override IEnumerable<RabbitMqEndpoint> endpoints()
    {
        foreach (var exchange in Exchanges)
        {
            yield return exchange;

            foreach (var topic in exchange.Topics) yield return topic;
        }

        foreach (var queue in Queues) yield return queue;
    }

    protected override RabbitMqEndpoint findEndpointByUri(Uri uri)
    {
        var type = uri.Host;

        var name = uri.Segments.Last();
        switch (type)
        {
            case RabbitMqEndpoint.QueueSegment:
                return Queues[name];

            case RabbitMqEndpoint.ExchangeSegment:
                return Exchanges[name];

            case RabbitMqEndpoint.TopicSegment:
                return Topics[uri];

            default:
                throw new ArgumentOutOfRangeException(nameof(uri), $"Invalid Rabbit MQ object type '{type}'");
        }
    }

    protected override void tryBuildResponseQueueEndpoint(IWolverineRuntime runtime)
    {
        var queueName = $"wolverine.response.{runtime.DurabilitySettings.UniqueNodeId}";

        var queue = new RabbitMqQueue(queueName, this, EndpointRole.System)
        {
            AutoDelete = true,
            IsDurable = false,
            IsListener = true,
            IsUsedForReplies = true,
            ListenerCount = 5,
            EndpointName = ResponseEndpointName
        };

        Queues[queueName] = queue;
    }

    internal IConnection BuildConnection()
    {
        return AmqpTcpEndpoints.Any()
            ? ConnectionFactory.CreateConnection(AmqpTcpEndpoints)
            : ConnectionFactory.CreateConnection();
    }

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