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
        BrokerRole = "exchange";

        // GH-3270: give the sending endpoint a recognizable EndpointName so it doesn't fall through to the synthetic
        // Uri (blank/"unknown" in the endpoint-health snapshot). The default exchange routes by routing key — which is
        // the target queue name — so use the key alone; a named exchange combines its name with the routing key.
        EndpointName = string.IsNullOrEmpty(_exchange.DeclaredName)
            ? routingKey
            : $"{_exchange.DeclaredName}/{routingKey}";
    }

    public override ValueTask<IListener> BuildListenerAsync(IWolverineRuntime runtime, IReceiver receiver)
    {
        throw new NotSupportedException(
            $"The Rabbit MQ routing key '{_routingKey}' on exchange '{_exchange.ExchangeName}' ({Uri}) is a publish-only endpoint. A routing key cannot be listened to directly. Bind a queue to the exchange with that routing key and listen to the queue instead with ListenToRabbitQueue(queueName).");
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