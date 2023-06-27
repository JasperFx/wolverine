using Microsoft.Extensions.Logging;
using Wolverine.Configuration;
using Wolverine.Runtime;
using Wolverine.Transports;

namespace Wolverine.RabbitMQ.Internal;

public class RabbitMqRouting : RabbitMqEndpoint
{
    private readonly RabbitMqExchange _exchange;
    private readonly string _routingKey;

    public static Uri ToUri(RabbitMqExchange exchange, string routingKey)
    {
        return new Uri($"{exchange.Uri}/routing/{routingKey}");
    }
    
    public RabbitMqRouting(RabbitMqExchange exchange, string routingKey, RabbitMqTransport parent) : base(ToUri(exchange, routingKey), EndpointRole.Application, parent)
    {
        _exchange = exchange;
        _routingKey = routingKey;

        ExchangeName = _exchange.ExchangeName;
    }

    public override ValueTask<IListener> BuildListenerAsync(IWolverineRuntime runtime, IReceiver receiver)
    {
        throw new NotSupportedException();
    }

    public override ValueTask<bool> CheckAsync()
    {
        return _exchange.CheckAsync();
    }

    public override ValueTask TeardownAsync(ILogger logger)
    {
        return ValueTask.CompletedTask;
    }

    public override ValueTask SetupAsync(ILogger logger)
    {
        return _exchange.SetupAsync(logger);
    }

    internal override string RoutingKey()
    {
        return _routingKey;
    }
}