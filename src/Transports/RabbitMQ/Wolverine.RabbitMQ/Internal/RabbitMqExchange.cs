using JasperFx.Core;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using Wolverine.Configuration;
using Wolverine.Runtime;
using Wolverine.Transports;

namespace Wolverine.RabbitMQ.Internal;

public class RabbitMqExchange : RabbitMqEndpoint, IRabbitMqExchange
{
    private readonly RabbitMqTransport _parent;

    private bool _initialized;

    internal RabbitMqExchange(string name, RabbitMqTransport parent)
        : base(new Uri($"{parent.Protocol}://{ExchangeSegment}/{name}"), EndpointRole.Application,
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

    /// <summary>
    /// All active topic endpoints by name
    /// </summary>
    public LightweightCache<string, RabbitMqTopicEndpoint> Topics { get; }
    
    /// <summary>
    /// All active routing keys
    /// </summary>
    public LightweightCache<string, RabbitMqRouting> Routings { get; }

    public override bool AutoStartSendingAgent()
    {
        return base.AutoStartSendingAgent() || ExchangeType == ExchangeType.Topic;
    }

    public bool DisableAutoProvision { get; set; }

    public bool HasDeclared { get; private set; }

    public string DeclaredName { get; }

    public string Name { get; }

    public bool IsDurable { get; set; } = true;

    public ExchangeType ExchangeType { get; set; } = ExchangeType.Fanout;
    public bool AutoDelete { get; set; } = false;

    public IDictionary<string, object> Arguments { get; } = new Dictionary<string, object>();
    
    // this is meh
    public string? DirectRoutingKey { get; set; }


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

        if (_parent.AutoProvision && !DisableAutoProvision)
        {
            await _parent.WithAdminChannelAsync(model => DeclareAsync(model, logger));
        }

        _initialized = true;
    }

    internal override string RoutingKey()
    {
        if (ExchangeType == ExchangeType.Direct)
        {
            return DirectRoutingKey ?? throw new InvalidOperationException("No default routing key has been configured for this direct exchange");
        }

        return string.Empty;
    }

    internal async Task DeclareAsync(IChannel channel, ILogger logger)
    {
        if (HasDeclared || DeclaredName == string.Empty)
        {
            return;
        }

        var exchangeTypeName = ExchangeType.ToString().ToLower();
        await channel.ExchangeDeclareAsync(DeclaredName, exchangeTypeName, IsDurable, AutoDelete, Arguments);
        logger.LogInformation(
            "Declared Rabbit Mq exchange '{Name}', type = {Type}, IsDurable = {IsDurable}, AutoDelete={AutoDelete}",
            DeclaredName, exchangeTypeName, IsDurable, AutoDelete);

        HasDeclared = true;
    }

    public override async ValueTask<bool> CheckAsync()
    {
        var exchangeName = Name.ToLower();
        try
        {
            await _parent.WithAdminChannelAsync(channel => channel.ExchangeDeclarePassiveAsync(exchangeName));
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public override async ValueTask TeardownAsync(ILogger logger)
    {
        await _parent.WithAdminChannelAsync(async channel =>
        {
            if (DeclaredName == string.Empty)
            {
            }
            else
            {
                await channel.ExchangeDeleteAsync(DeclaredName);
            }
        });

    }

    public override async ValueTask SetupAsync(ILogger logger)
    {
        await _parent.WithAdminChannelAsync(channel => DeclareAsync(channel, logger));
    }
}

