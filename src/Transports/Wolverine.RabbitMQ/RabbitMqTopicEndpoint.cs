using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using Wolverine.Configuration;
using Wolverine.RabbitMQ.Internal;
using Wolverine.Runtime;
using Wolverine.Transports;

namespace Wolverine.RabbitMQ;

internal class RabbitMqTopicEndpoint : RabbitMqEndpoint
{
    private readonly string _topicName;
    private readonly RabbitMqExchange _exchange;

    public RabbitMqTopicEndpoint(string topicName, RabbitMqExchange exchange, RabbitMqTransport parent) : base(new Uri($"rabbitmq://{exchange.Name}/{topicName}"), EndpointRole.Application, parent)
    {
        _topicName = topicName;
        _exchange = exchange;

        ExchangeName = _exchange.Name;
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

    public override IDictionary<string, object> DescribeProperties()
    {
        var dict = base.DescribeProperties();
        dict.Add("Topic", _topicName);

        return dict;
    }

    public override ValueTask<IListener> BuildListenerAsync(IWolverineRuntime runtime, IReceiver receiver)
    {
        throw new NotSupportedException();
    }

    internal override string RoutingKey() => _topicName;

    public override ValueTask InitializeAsync(ILogger logger)
    {
        return _exchange.InitializeAsync(logger);
    }
}