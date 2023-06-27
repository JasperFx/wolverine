using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using Wolverine.Configuration;
using Wolverine.Runtime;
using Wolverine.Runtime.Routing;
using Wolverine.Transports;
using Wolverine.Transports.Sending;

namespace Wolverine.RabbitMQ.Internal;

internal class RabbitMqSender : RabbitMqConnectionAgent, ISender
{
    private readonly string _exchangeName;
    private readonly bool _isDurable;
    private readonly string _key;
    private readonly IEnvelopeMapper<IBasicProperties, IBasicProperties> _mapper;
    private readonly RabbitMqEndpoint _endpoint;
    private readonly Func<Envelope, string> _toRoutingKey;

    public RabbitMqSender(RabbitMqEndpoint endpoint, RabbitMqTransport transport,
        RoutingMode routingType, IWolverineRuntime runtime) : base(
        transport.SendingConnection, runtime.LoggerFactory.CreateLogger<RabbitMqSender>())
    {
        Destination = endpoint.Uri;

        _isDurable = endpoint.Mode == EndpointMode.Durable;

        _exchangeName = endpoint.ExchangeName;
        _key = endpoint.RoutingKey();

        _toRoutingKey = routingType == RoutingMode.Static ? _ => _key : TopicRouting.DetermineTopicName;

        _mapper = endpoint.BuildMapper(runtime);
        _endpoint = endpoint;

        EnsureConnected();
    }

    public bool SupportsNativeScheduledSend => false;
    public Uri Destination { get; }

    public async ValueTask SendAsync(Envelope envelope)
    {
        if (Channel == null)
        {
            throw new InvalidOperationException("Channel has not been started for this sender");
        }

        await _endpoint.InitializeAsync(Logger);

        if (State == AgentState.Disconnected)
        {
            throw new InvalidOperationException($"The RabbitMQ agent for {Destination} is disconnected");
        }

        var props = Channel.CreateBasicProperties();
        props.Persistent = _isDurable;
        props.Headers = new Dictionary<string, object>();

        _mapper.MapEnvelopeToOutgoing(envelope, props);

        var routingKey = _toRoutingKey(envelope);
        Channel.BasicPublish(_exchangeName, routingKey, props, envelope.Data);
    }

    public Task<bool> PingAsync()
    {
        lock (Locker)
        {
            if (State == AgentState.Connected)
            {
                return Task.FromResult(true);
            }

            startNewChannel();

            if (Channel!.IsOpen)
            {
                return Task.FromResult(true);
            }

            teardownChannel();
            return Task.FromResult(false);
        }
    }
}