using System.Diagnostics;
using JasperFx.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RabbitMQ.Client;
using Spectre.Console;
using Wolverine.Configuration;
using Wolverine.Runtime;
using Wolverine.Transports;
using Wolverine.Transports.Sending;

namespace Wolverine.RabbitMQ.Internal;

public partial class RabbitMqTransport : BrokerTransport<RabbitMqEndpoint>, IAsyncDisposable
{
    public const string ProtocolName = "rabbitmq";
    public const string ResponseEndpointName = "RabbitMqResponses";
    public const string DeadLetterQueueName = "wolverine-dead-letter-queue";
    public const string DeadLetterQueueHeader = "x-dead-letter-exchange";
    public const string QueueTypeHeader = "x-queue-type";

    private ConnectionMonitor? _listenerConnection;
    private ConnectionMonitor? _sendingConnection;

    public RabbitMqTransport(string protocol) : base(protocol, "Rabbit MQ")
    {
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

    public RabbitMqTransport() : this(ProtocolName)
    {
        
    }

    public override Uri ResourceUri
    {
        get
        {
            if (ConnectionFactory == null) return new Uri("rabbitmq://");

            var resourceUri = new Uri($"rabbitmq://{ConnectionFactory.HostName}");

            if (ConnectionFactory.VirtualHost.IsNotEmpty())
            {
                resourceUri = new Uri(resourceUri, ConnectionFactory.VirtualHost);
            }
            
            return resourceUri;
        }
    }

    internal LightweightCache<string, RabbitMqTenant> Tenants { get; } = new();

    private void configureDefaults(ConnectionFactory factory)
    {
        factory.AutomaticRecoveryEnabled = true;
        factory.ClientProvidedName ??= "Wolverine";
    }

    public DeadLetterQueue DeadLetterQueue { get; } = new(DeadLetterQueueName);

    internal RabbitMqChannelCallback? Callback { get; private set; }

    internal ConnectionMonitor ListeningConnection => _listenerConnection ?? throw new InvalidOperationException("The listening connection has not been created yet or is disabled!");
    internal ConnectionMonitor SendingConnection => _sendingConnection ?? throw new InvalidOperationException("The sending connection has not been created yet or is disabled!");

    public ConnectionFactory? ConnectionFactory { get; private set; }

    internal void ConfigureFactory(Action<ConnectionFactory> configure)
    {
        var factory = new ConnectionFactory
        {
            ClientProvidedName = "Wolverine"
        };
        
        configure(factory);
        
        configureDefaults(factory);

        ConnectionFactory = factory;
    }

    public IList<AmqpTcpEndpoint> AmqpTcpEndpoints { get; } = new List<AmqpTcpEndpoint>();

    public LightweightCache<Uri, RabbitMqTopicEndpoint> Topics { get; }
    public LightweightCache<string, RabbitMqExchange> Exchanges { get; }

    public LightweightCache<string, RabbitMqQueue> Queues { get; }

    internal bool DeclareRequestReplySystemQueue { get; set; } = true;
    internal bool UseSenderConnectionOnly { get; set; }
    internal bool UseListenerConnectionOnly { get; set; }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if(_listenerConnection is not null)
                await _listenerConnection.DisposeAsync();
        }
        catch (ObjectDisposedException)
        {

        }

        try
        {
            if(_sendingConnection is not null)
                await _sendingConnection.DisposeAsync();
        }
        catch (ObjectDisposedException)
        {

        }

        Callback?.SafeDispose();

        foreach (var queue in Queues)
        {
            await queue.DisposeAsync();
        }

        foreach (var tenant in Tenants)
        {
            await tenant.Transport.DisposeAsync();
        }
    }

    public override async ValueTask ConnectAsync(IWolverineRuntime runtime)
    {
        Logger = runtime.LoggerFactory.CreateLogger<RabbitMqTransport>();
        Callback = new RabbitMqChannelCallback(Logger, runtime.DurabilitySettings.Cancellation);

        ConnectionFactory ??= runtime.Services.GetService<IConnectionFactory>() as ConnectionFactory ??
                              new ConnectionFactory { HostName = "localhost" };
        
        configureDefaults(ConnectionFactory);
        
        if (_listenerConnection == null && !UseSenderConnectionOnly)
        {
            _listenerConnection = BuildConnection(ConnectionRole.Listening);
            await _listenerConnection.ConnectAsync();
        }

        if (_sendingConnection == null && !UseListenerConnectionOnly)
        {
            _sendingConnection = BuildConnection(ConnectionRole.Sending);
            await _sendingConnection.ConnectAsync();
        }

        foreach (var tenant in Tenants)
        {
            await tenant.ConnectAsync(this, runtime);
        }
    }

    internal async Task<IConnection> CreateConnectionAsync()
    {
        // TODO -- consider adding retries on this?
        if (ConnectionFactory == null)
            throw new InvalidOperationException("Rabbit MQ transport has not been initialized");
        
        using var activity = WolverineTracing.ActivitySource.StartActivity("rabbitmq connect", ActivityKind.Client);
        
        activity?.AddTag("server.address", ConnectionFactory.HostName);
        activity?.AddTag("server.port", ConnectionFactory.Port);
        activity?.AddTag("messaging.system", "rabbitmq");
        activity?.AddTag("messaging.operation", "connect");
        
        var connection = AmqpTcpEndpoints.Any()
            ? await ConnectionFactory.CreateConnectionAsync(AmqpTcpEndpoints)
            : await ConnectionFactory.CreateConnectionAsync();

        return connection;
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
            // We switched back to using a Guid to disambiguate Wolverine nodes
            // that might be connecting to the same broker but within different apps
            var queueName = $"wolverine.response.{Guid.NewGuid()}";

            var queue = new RabbitMqQueue(queueName, this, EndpointRole.System)
            {
                AutoDelete = true,
                IsDurable = false,
                IsListener = true,
                IsUsedForReplies = true,
                ListenerCount = 1,
                EndpointName = ResponseEndpointName,
                QueueType = QueueType.classic // This is important, quorum queues cannot be auto-delete
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

            dlq.BindExchange(dlqExchange.Name, deadLetterQueue.BindingName);
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
        return Queues.SelectMany(x => x.Bindings());
    }

    public override IEnumerable<PropertyColumn> DiagnosticColumns()
    {
        yield return new PropertyColumn("Queue Name", "name");
        yield return new PropertyColumn("Message Count", "count", Justify.Right);
    }

    private Task<IChannel> createAdminChannelAsync()
    {
        if (_listenerConnection != null) return _listenerConnection.CreateChannelAsync();
        if (_sendingConnection != null) return _sendingConnection.CreateChannelAsync();
        throw new InvalidOperationException("Rabbit MQ Transport has not been initialized");
    }

    public async Task WithAdminChannelAsync(Func<IChannel, Task> operation)
    {
        await using var channel = await createAdminChannelAsync();
        await operation(channel);
        await channel.CloseAsync();

        foreach (var tenant in Tenants)
        {
            await tenant.Transport.WithAdminChannelAsync(operation);
        }
    }

    public ISender BuildSender(RabbitMqEndpoint endpoint, RoutingMode routingType, IWolverineRuntime runtime)
    {
        var rabbitMqSender = new RabbitMqSender(endpoint, this, routingType, runtime);
        
        if (Tenants.Any() && endpoint.TenancyBehavior == TenancyBehavior.TenantAware)
        {
            var tenantedSender = new TenantedSender(endpoint.Uri, TenantedIdBehavior, rabbitMqSender);
            foreach (var tenant in Tenants)
            {
                var sender = new RabbitMqSender(endpoint, tenant.Transport, routingType, runtime);
                tenantedSender.RegisterSender(tenant.TenantId, sender);
            }

            return tenantedSender;
        }
        
        return rabbitMqSender;
    }

    public async ValueTask<IListener> BuildListenerAsync(IWolverineRuntime runtime, IReceiver receiver, RabbitMqQueue queue)
    {
        var listener = await buildListener(runtime, receiver, queue);

        if (queue.CustomListenerId != null && listener is ISupportMultipleConsumers multipleConsumersOnSameQueueListener)
        {
            multipleConsumersOnSameQueueListener.ConsumerId = queue.CustomListenerId;
        }

        if (Tenants.Any() && queue.TenancyBehavior == TenancyBehavior.TenantAware)
        {
            var compound = new CompoundListener(queue.Uri);
            compound.Inner.Add(listener);

            foreach (var tenant in Tenants)
            {
                var rule = new TenantIdRule(tenant.TenantId);
                var wrapped = new ReceiverWithRules(receiver, [rule]);
                var tenantListener = await tenant.Transport.buildListener(runtime, wrapped, queue);
                compound.Inner.Add(tenantListener);
            }

            return compound;
        }

        return listener;
    }

    private async Task<IListener> buildListener(IWolverineRuntime runtime, IReceiver receiver, RabbitMqQueue queue)
    {
        var singleListener = new RabbitMqListener(runtime, queue, this, receiver);
        await singleListener.CreateAsync();
        return singleListener;
    }
}