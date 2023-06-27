using JasperFx.Core;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using Wolverine.Configuration;
using Wolverine.Runtime;
using Wolverine.Transports;
using Wolverine.Transports.Sending;

namespace Wolverine.RabbitMQ.Internal;

public abstract partial class RabbitMqEndpoint : Endpoint, IBrokerEndpoint
{
    public const string QueueSegment = "queue";
    public const string ExchangeSegment = "exchange";
    public const string TopicSegment = "topic";
    private readonly RabbitMqTransport _parent;

    private Action<RabbitMqEnvelopeMapper> _customizeMapping = m => { };

    internal RabbitMqEndpoint(Uri uri, EndpointRole role, RabbitMqTransport parent) : base(uri, role)
    {
        _parent = parent;

        Mode = EndpointMode.Inline;
    }

    public string ExchangeName { get; protected set; } = string.Empty;

    public abstract ValueTask<bool> CheckAsync();
    public abstract ValueTask TeardownAsync(ILogger logger);
    public abstract ValueTask SetupAsync(ILogger logger);

    internal abstract string RoutingKey();

    public override IDictionary<string, object> DescribeProperties()
    {
        var dict = base.DescribeProperties();

        if (ExchangeName.IsNotEmpty())
        {
            dict.Add(nameof(ExchangeName), ExchangeName);
        }

        return dict;
    }

    protected override ISender CreateSender(IWolverineRuntime runtime)
    {
        return new RabbitMqSender(this, _parent, RoutingType, runtime);
    }

    internal IEnvelopeMapper<IBasicProperties, IBasicProperties> BuildMapper(IWolverineRuntime runtime)
    {
        var mapper = new RabbitMqEnvelopeMapper(this, runtime);
        _customizeMapping?.Invoke(mapper);
        if (MessageType != null)
        {
            mapper.ReceivesMessage(MessageType);
        }

        return mapper;
    }
}