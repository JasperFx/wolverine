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
    private readonly RabbitMqTransport _transport;
    private readonly IWolverineRuntime _runtime;
    private readonly CachedString _exchangeName;
    private readonly bool _isDurable;
    private readonly CachedString _routingKey = null!;
    private readonly IRabbitMqEnvelopeMapper _mapper;
    private readonly bool _confirmEveryPublish;

    /// <param name="confirmEveryPublish">
    /// GH-5781. When true, this sender's channel always has publisher confirms with tracking, and a publish to a
    /// queue is mandatory, so <see cref="SendAsync" /> only returns once the broker has the message and throws if the
    /// broker refused it or could not route it. Used only for requeue copies; ordinary senders leave it false.
    /// </param>
    public RabbitMqSender(RabbitMqEndpoint endpoint, RabbitMqTransport transport,
        RoutingMode routingType, IWolverineRuntime runtime, bool confirmEveryPublish = false) : base(
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
        _transport = transport;
        _runtime = runtime;
        _confirmEveryPublish = confirmEveryPublish;
    }

    protected override bool RequirePublisherConfirms => _confirmEveryPublish;

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
        // GH-5016: a queue that does not exist makes a mandatory publish fail rather than be confirmed and dropped,
        // so its outbox row stays. An exchange with no bindings still drops a message, as it does in any other mode
        // GH-5781: so does a requeue copy, whose original is acked only once the copy is on the broker
        var mandatory = (_transport.RequirePublisherConfirms || _confirmEveryPublish) && _endpoint is RabbitMqQueue;
        await Channel.BasicPublishAsync(_exchangeName, routingKey, mandatory, props, envelope.Data);
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