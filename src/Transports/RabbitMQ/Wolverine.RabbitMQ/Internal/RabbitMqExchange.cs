using JasperFx.Core;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using Wolverine.Configuration;
using Wolverine.Runtime;
using Wolverine.Transports;

namespace Wolverine.RabbitMQ.Internal;

public class RabbitMqExchange : RabbitMqEndpoint, IRabbitMqExchange
{
    private readonly List<RabbitMqBinding> _bindings = [];
    private readonly RabbitMqTransport _parent;

    private bool _initialized;

    internal RabbitMqExchange(string name, RabbitMqTransport parent)
        : base(new Uri($"{RabbitMqTransport.ProtocolName}://{ExchangeSegment}/{name}"), EndpointRole.Application,
            parent)
    {
        _parent = parent;
        Name = name;
        DeclaredName = name == TransportConstants.Default ? "" : Name;
        ExchangeName = name;

        EndpointName = name;

        Topics = new(topic => new RabbitMqTopicEndpoint(topic, this, _parent));
        Routings = new LightweightCache<string, RabbitMqRouting>(key => new RabbitMqRouting(this, key, _parent));
    }

    internal LightweightCache<string, RabbitMqTopicEndpoint> Topics { get; }
    internal LightweightCache<string, RabbitMqRouting> Routings { get; }

    public override bool AutoStartSendingAgent()
    {
        return base.AutoStartSendingAgent() || ExchangeType == ExchangeType.Topic;
    }

    public bool HasDeclared { get; private set; }

    public string DeclaredName { get; }

    public string Name { get; }

    public bool IsDurable { get; set; } = true;

    public ExchangeType ExchangeType { get; set; } = ExchangeType.Fanout;


    public bool AutoDelete { get; set; } = false;

    public IDictionary<string, object> Arguments { get; } = new Dictionary<string, object>();

    public RabbitMqBinding BindQueue(string queueName, string? bindingKey = null)
    {
        if (queueName == null)
        {
            throw new ArgumentNullException(nameof(queueName));
        }

        var existing = _bindings.FirstOrDefault(x => x.Queue.QueueName == queueName && x.BindingKey == bindingKey);
        if (existing != null) return existing;

        var queue = _parent.Queues[queueName];

        var binding = new RabbitMqBinding(Name, queue, bindingKey);
        _bindings.Add(binding);
        return binding;
    }

    /// <summary>
    ///     Declare a Rabbit MQ binding with the supplied topic pattern to
    ///     the queue
    /// </summary>
    /// <param name="topicPattern"></param>
    /// <param name="bindingName"></param>
    /// <exception cref="NotImplementedException"></exception>
    public TopicBinding BindTopic(string topicPattern)
    {
        return new TopicBinding(this, topicPattern);
    }

    public override ValueTask<IListener> BuildListenerAsync(IWolverineRuntime runtime, IReceiver receiver)
    {
        throw new NotSupportedException();
    }

    public override async ValueTask InitializeAsync(ILogger logger)
    {
        if (_initialized)
        {
            return;
        }

        if (_parent.AutoProvision)
        {
            using var model = await _parent.CreateAdminChannelAsync();
            await DeclareAsync(model, logger);
        }

        _initialized = true;
    }

    internal override string RoutingKey()
    {
        if (ExchangeType == ExchangeType.Direct)
        {
            if (_bindings.Count == 1)
            {
                return _bindings.Single().BindingKey;
            }

            throw new NotSupportedException("Direct exchanges with more than one binding are not yet supported by Wolverine");
        }

        return string.Empty;
    }

    internal async Task DeclareAsync(IChannel channel, ILogger logger)
    {
        if (DeclaredName == string.Empty)
        {
            return;
        }

        if (HasDeclared)
        {
            return;
        }

        var exchangeTypeName = ExchangeType.ToString().ToLower();
        await channel.ExchangeDeclareAsync(DeclaredName, exchangeTypeName, IsDurable, AutoDelete, Arguments);
        logger.LogInformation(
            "Declared Rabbit Mq exchange '{Name}', type = {Type}, IsDurable = {IsDurable}, AutoDelete={AutoDelete}",
            DeclaredName, exchangeTypeName, IsDurable, AutoDelete);

        foreach (var binding in _bindings) await binding.DeclareAsync(channel, logger);

        HasDeclared = true;
    }

    public override async ValueTask<bool> CheckAsync()
    {
        using var channel = await _parent.CreateAdminChannelAsync();
        var exchangeName = Name.ToLower();
        try
        {
            await channel.ExchangeDeclarePassiveAsync(exchangeName);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public override async ValueTask TeardownAsync(ILogger logger)
    {
        using var channel = await _parent.CreateAdminChannelAsync();
        if (DeclaredName == string.Empty)
        {
        }
        else
        {
            foreach (var binding in _bindings)
            {
                logger.LogInformation("Removing binding {Key} from exchange {Exchange} to queue {Queue}",
                    binding.BindingKey, binding.ExchangeName, binding.Queue);
                await binding.TeardownAsync(channel);
            }

            await channel.ExchangeDeleteAsync(DeclaredName);
        }
    }

    public override async ValueTask SetupAsync(ILogger logger)
    {
        using var channel = await _parent.CreateAdminChannelAsync();
        await DeclareAsync(channel, logger);
    }

    public IEnumerable<RabbitMqBinding> Bindings()
    {
        return _bindings;
    }
}

public class TopicBinding
{
    private readonly RabbitMqExchange _exchange;
    private readonly string _topicPattern;

    public TopicBinding(RabbitMqExchange exchange, string topicPattern)
    {
        _exchange = exchange;
        _topicPattern = topicPattern;
    }

    /// <summary>
    ///     Create a binding of the topic pattern previously specified to a Rabbit Mq queue
    /// </summary>
    /// <param name="queueName">The name of the Rabbit Mq queue</param>
    /// <param name="configureQueue">Optionally configure </param>
    public void ToQueue(string queueName, Action<IRabbitMqQueue>? configureQueue = null)
    {
        var binding = _exchange.BindQueue(queueName, _topicPattern);
        configureQueue?.Invoke(binding.Queue);
    }
}