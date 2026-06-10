using JasperFx.Core;
using Microsoft.Extensions.Logging;
using Wolverine.Configuration;
using Wolverine.Runtime;
using Wolverine.Transports;
using Wolverine.Transports.Sending;

namespace Wolverine.RabbitMQ.Internal;

public abstract partial class RabbitMqEndpoint : Endpoint<IRabbitMqEnvelopeMapper, RabbitMqEnvelopeMapper>, IBrokerEndpoint, IAsyncDisposable
{
    public const string QueueSegment = "queue";
    public const string ExchangeSegment = "exchange";
    public const string TopicSegment = "topic";
    private readonly RabbitMqTransport _parent;

    internal RabbitMqEndpoint(Uri uri, EndpointRole role, RabbitMqTransport parent) : base(uri, role)
    {
        _parent = parent;

        Mode = EndpointMode.Inline;
    }

    public string ExchangeName { get; protected set; } = string.Empty;

    /// <summary>
    /// When <c>true</c>, Wolverine treats this queue or exchange as owned by an external system: it
    /// will not declare (create) it during startup or delete it during <c>resources teardown</c>, even
    /// when <c>AutoProvision()</c> is enabled on the parent transport. Bindings owned by an
    /// externally-owned queue/exchange are likewise left untouched. Use this when the calling identity
    /// lacks the <c>configure</c>/<c>delete</c> permissions for the resource. Default is <c>false</c>.
    /// See https://github.com/JasperFx/wolverine/issues/3064.
    /// </summary>
    public bool IsExternallyOwned { get; set; }

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

    protected override RabbitMqEnvelopeMapper buildMapper(IWolverineRuntime runtime)
    {
        return new RabbitMqEnvelopeMapper(this, runtime);
    }
}