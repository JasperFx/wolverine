using Microsoft.Extensions.Logging;
using Wolverine.Configuration;
using Wolverine.RabbitMQ.Internal;
using Wolverine.Runtime;
using Wolverine.Transports;

namespace Wolverine.RabbitMQ;

public class RabbitMqTopicEndpoint : RabbitMqEndpoint
{
    public RabbitMqTopicEndpoint(string topicName, RabbitMqExchange exchange, RabbitMqTransport parent) : base(
        new Uri($"{parent.Protocol}://topic/{exchange.Name}/{topicName}"), EndpointRole.Application, parent)
    {
        EndpointName = TopicName = topicName;
        Exchange = exchange;

        ExchangeName = Exchange.Name;
    }

    public RabbitMqExchange Exchange { get; }

    public string TopicName { get; }

    public override ValueTask<bool> CheckAsync()
    {
        return Exchange.CheckAsync();
    }

    public override ValueTask TeardownAsync(ILogger logger)
    {
        return ValueTask.CompletedTask;
    }

    public override ValueTask SetupAsync(ILogger logger)
    {
        return Exchange.SetupAsync(logger);
    }

    public override IDictionary<string, object> DescribeProperties()
    {
        var dict = base.DescribeProperties();
        dict.Add("Topic", TopicName);

        return dict;
    }

    public override ValueTask<IListener> BuildListenerAsync(IWolverineRuntime runtime, IReceiver receiver)
    {
        throw new NotSupportedException();
    }

    internal override string RoutingKey()
    {
        return TopicName;
    }

    public override ValueTask InitializeAsync(ILogger logger)
    {
        return Exchange.InitializeAsync(logger);
    }
}