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
    private readonly List<RabbitMqExchangeBinding> _exchangeBindings = [];

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
    
    internal bool HasExchangeBindings => _exchangeBindings.Count > 0;

    /// <summary>
    /// Bind a source exchange to this exchange (this exchange is the destination).
    /// Messages published to the source exchange will be routed to this exchange.
    /// </summary>
    /// <param name="sourceExchangeName">The exchange that receives published messages</param>
    /// <param name="bindingKey">Optional routing/binding key</param>
    /// <param name="arguments">Optional binding arguments</param>
    /// <returns></returns>
    public RabbitMqExchangeBinding BindExchange(string sourceExchangeName, string? bindingKey = null, Dictionary<string, object>? arguments = null)
    {
        if (sourceExchangeName == null)
        {
            throw new ArgumentNullException(nameof(sourceExchangeName));
        }

        var existing = _exchangeBindings.FirstOrDefault(x => x.SourceExchangeName == sourceExchangeName && x.BindingKey == bindingKey);
        if (existing != null) return existing;

        // Ensure the source exchange exists so resource setup works correctly
        _parent.Exchanges.FillDefault(sourceExchangeName);

        var binding = new RabbitMqExchangeBinding(sourceExchangeName, Name, bindingKey);
        if (arguments is not null)
        {
            foreach (var argument in arguments)
            {
                binding.Arguments.Add(argument);
            }
        }
        _exchangeBindings.Add(binding);
        return binding;
    }

    public IEnumerable<RabbitMqExchangeBinding> ExchangeBindings()
    {
        return _exchangeBindings;
    }

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
        if (DeclaredName == string.Empty)
        {
            return;
        }

        var exchangeTypeName = ExchangeType.ToString().ToLower();
        await channel.ExchangeDeclareAsync(DeclaredName, exchangeTypeName, IsDurable, AutoDelete, Arguments);
        logger.LogInformation(
            "Declared Rabbit Mq exchange '{Name}', type = {Type}, IsDurable = {IsDurable}, AutoDelete={AutoDelete}",
            DeclaredName, exchangeTypeName, IsDurable, AutoDelete);

        HasDeclared = true;

        foreach (var binding in _exchangeBindings)
        {
            await binding.DeclareAsync(channel, logger);
        }
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
            foreach (var binding in _exchangeBindings)
            {
                await binding.TeardownAsync(channel);
            }

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

