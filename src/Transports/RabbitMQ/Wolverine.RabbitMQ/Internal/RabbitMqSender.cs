using JasperFx.Core;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using Wolverine.Configuration;
using Wolverine.Runtime;
using Wolverine.Runtime.Routing;
using Wolverine.Transports.Sending;

namespace Wolverine.RabbitMQ.Internal;

internal class RabbitMqSender : RabbitMqChannelAgent, ISender
{
    private readonly RabbitMqEndpoint _endpoint;
    private readonly CachedString _exchangeName;
    private readonly bool _isDurable;
    private readonly string _key;
    private readonly IRabbitMqEnvelopeMapper _mapper;
    private readonly Func<Envelope, CachedString> _toRoutingKey;

    public RabbitMqSender(RabbitMqEndpoint endpoint, RabbitMqTransport transport,
        RoutingMode routingType, IWolverineRuntime runtime) : base(
        transport.SendingConnection, runtime.LoggerFactory.CreateLogger<RabbitMqSender>())
    {
        Destination = endpoint.Uri;

        _isDurable = endpoint.Mode == EndpointMode.Durable;

        _exchangeName = new CachedString(endpoint.ExchangeName);
        _key = endpoint.RoutingKey();

        _toRoutingKey = routingType == RoutingMode.Static ? _ => new CachedString(_key) : x =>
        {
            if (x.TopicName.IsEmpty() && x.Message == null)
            {
                try
                {
                    runtime.Pipeline.TryDeserializeEnvelope(x, out var _);
                }
                catch (Exception e)
                {
                    Logger.LogError(e, "Error trying to deserialize an envelope in order to determine the topic name");
                }
            }
            
            return new CachedString(TopicRouting.DetermineTopicName(x));
        };

        _mapper = endpoint.BuildMapper(runtime);
        _endpoint = endpoint;
    }

    public bool SupportsNativeScheduledSend => false;
    public Uri Destination { get; }

    public async ValueTask SendAsync(Envelope envelope)
    {
        await EnsureInitiated();
        if (Channel == null)
        {
            throw new InvalidOperationException("Channel has not been started for this sender");
        }

        if (State == AgentState.Disconnected)
        {
            throw new InvalidOperationException($"The RabbitMQ agent for {Destination} is disconnected");
        }

        await _endpoint.InitializeAsync(Logger);

        var props = new BasicProperties
        {
            Persistent = _isDurable,
            Headers = new Dictionary<string, object?>()
        };

        _mapper.MapEnvelopeToOutgoing(envelope, props);

        var routingKey = _toRoutingKey(envelope);
        await Channel.BasicPublishAsync(_exchangeName, routingKey, false, props, envelope.Data);
    }

    public override string ToString()
    {
        return $"RabbitMqSender: {Destination}";
    }

    public async Task<bool> PingAsync()
    {
        if (State == AgentState.Connected)
        {
            return true;
        }

        await EnsureInitiated();

        if (State == AgentState.Connected)
        {
            return true;
        }

        return false;
    }
}