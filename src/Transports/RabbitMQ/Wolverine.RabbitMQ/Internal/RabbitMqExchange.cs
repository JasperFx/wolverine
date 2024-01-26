using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using JasperFx.Core;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using Wolverine.Configuration;
using Wolverine.Runtime;
using Wolverine.Transports;

namespace Wolverine.RabbitMQ.Internal;

public class RabbitMqExchange : RabbitMqEndpoint, IRabbitMqExchange
{
    private readonly List<RabbitMqBinding> _bindings = new();
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

    public override ValueTask InitializeAsync(ILogger logger)
    {
        if (_initialized)
        {
            return ValueTask.CompletedTask;
        }

        if (_parent.AutoProvision)
        {
            using var model = _parent.ListeningConnection.CreateModel();
            Declare(model, logger);
        }

        _initialized = true;

        return ValueTask.CompletedTask;
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

    internal void Declare(IModel channel, ILogger logger)
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
        channel.ExchangeDeclare(DeclaredName, exchangeTypeName, IsDurable, AutoDelete, Arguments);
        logger.LogInformation(
            "Declared Rabbit Mq exchange '{Name}', type = {Type}, IsDurable = {IsDurable}, AutoDelete={AutoDelete}",
            DeclaredName, exchangeTypeName, IsDurable, AutoDelete);

        foreach (var binding in _bindings) binding.Declare(channel, logger);

        HasDeclared = true;
    }

    public override ValueTask<bool> CheckAsync()
    {
        using var channel = _parent.ListeningConnection.CreateModel();
        var exchangeName = Name.ToLower();
        try
        {
            channel.ExchangeDeclarePassive(exchangeName);
            return ValueTask.FromResult(true);
        }
        catch (Exception)
        {
            return ValueTask.FromResult(false);
        }
    }

    public override ValueTask TeardownAsync(ILogger logger)
    {
        using var channel = _parent.ListeningConnection.CreateModel();
        if (DeclaredName == string.Empty)
        {
        }
        else
        {
            foreach (var binding in _bindings)
            {
                logger.LogInformation("Removing binding {Key} from exchange {Exchange} to queue {Queue}",
                    binding.BindingKey, binding.ExchangeName, binding.Queue);
                binding.Teardown(channel);
            }

            channel.ExchangeDelete(DeclaredName);
        }

        return ValueTask.CompletedTask;
    }

    public override ValueTask SetupAsync(ILogger logger)
    {
        using var channel = _parent.ListeningConnection.CreateModel();
        Declare(channel, logger);

        return ValueTask.CompletedTask;
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