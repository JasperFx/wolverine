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
    private readonly RoutingMode _routingType;
    private readonly IWolverineRuntime _runtime;
    private readonly CachedString _exchangeName;
    private readonly bool _isDurable;
    private readonly CachedString _routingKey;
    private readonly IRabbitMqEnvelopeMapper _mapper;

    public RabbitMqSender(RabbitMqEndpoint endpoint, RabbitMqTransport transport,
        RoutingMode routingType, IWolverineRuntime runtime) : base(
        transport.SendingConnection, runtime.LoggerFactory.CreateLogger<RabbitMqSender>())
    {
        Destination = endpoint.Uri;

        _isDurable = endpoint.Mode == EndpointMode.Durable;

        _exchangeName = new CachedString(endpoint.ExchangeName);
        
        if (routingType == RoutingMode.Static)
        {
            _routingKey = new CachedString(endpoint.RoutingKey());
        }

        _mapper = endpoint.BuildMapper(runtime);
        _endpoint = endpoint;
        _routingType = routingType;
        _runtime = runtime;
    }

    private async ValueTask<CachedString> ToRoutingKeyAsync(Envelope envelope)
    {
        if(_routingKey != null)
        {
            return _routingKey;
        }   

        if (envelope.TopicName.IsEmpty() && envelope.Message == null)
        {
            try
            {
                await _runtime.Pipeline.TryDeserializeEnvelope(envelope);
            }
            catch (Exception e)
            {
                Logger.LogError(e, "Error trying to deserialize an envelope in order to determine the topic name");
            }
        }
        return new CachedString(TopicRouting.DetermineTopicName(envelope));       
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

        var routingKey = await ToRoutingKeyAsync(envelope);
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