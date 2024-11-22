using JasperFx.Core;
using Microsoft.Extensions.Logging;
using Wolverine.Configuration;
using Wolverine.Runtime;
using Wolverine.Transports;
using Wolverine.Transports.Sending;

namespace Wolverine.RabbitMQ.Internal;

public abstract partial class RabbitMqEndpoint : Endpoint, IBrokerEndpoint, IAsyncDisposable
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

    /// <summary>
    /// When set, overrides the built in envelope mapping with a custom
    /// implementation
    /// </summary>
    public IRabbitMqEnvelopeMapper? EnvelopeMapper { get; set; }

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
        return ResolveSender(runtime);
    }

    private ISender? _sender;

    internal ISender ResolveSender(IWolverineRuntime runtime)
    {
        _sender ??= _parent.BuildSender(this, RoutingType, runtime);
        return _sender;
    }

    public async ValueTask DisposeAsync()
    {
        if(_sender is IAsyncDisposable ad)
        {
            await ad.DisposeAsync();
        }
    }

    internal IRabbitMqEnvelopeMapper BuildMapper(IWolverineRuntime runtime)
    {
        if (EnvelopeMapper != null) return EnvelopeMapper;

        var mapper = new RabbitMqEnvelopeMapper(this, runtime);
        _customizeMapping?.Invoke(mapper);
        if (MessageType != null)
        {
            mapper.ReceivesMessage(MessageType);
        }

        return mapper;
    }
}