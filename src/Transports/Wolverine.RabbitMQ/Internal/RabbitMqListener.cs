using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using Wolverine.Configuration;
using Wolverine.Transports;

namespace Wolverine.RabbitMQ.Internal
{
    public class RabbitMqListener : RabbitMqConnectionAgent, IListener
    {
        private readonly ILogger _logger;
        private readonly string _routingKey;
        private readonly RabbitMqSender _sender;
        private IReceiver _receiver;
        private CancellationToken _cancellation = CancellationToken.None;
        private WorkerQueueMessageConsumer? _consumer;

        public RabbitMqListener(ILogger logger,
            RabbitMqEndpoint endpoint, RabbitMqTransport transport, IReceiver receiver) : base(transport.ListeningConnection, transport, endpoint, logger)
        {
            _logger = logger;
            Endpoint = endpoint;
            Address = endpoint.Uri;

            _routingKey = endpoint.RoutingKey ?? endpoint.QueueName ?? "";

            _sender = new RabbitMqSender(Endpoint, transport, RoutingMode.Static, logger);

            _cancellation.Register(teardownChannel);

            EnsureConnected();

            _receiver = receiver ?? throw new ArgumentNullException(nameof(receiver));
            _consumer = new WorkerQueueMessageConsumer(receiver, _logger, this, Endpoint, Address,
                _cancellation);

            Channel.BasicQos(Endpoint.PreFetchSize, Endpoint.PreFetchCount, false);

            Channel.BasicConsume(_consumer, _routingKey);
        }

        public void Stop()
        {
            foreach (var consumerTag in _consumer.ConsumerTags)
            {
                Channel.BasicCancelNoWait(consumerTag);
            }
        }

        public ValueTask StopAsync()
        {
            Stop();
            return ValueTask.CompletedTask;
        }

        public RabbitMqEndpoint Endpoint { get; }

        public override void Dispose()
        {
            _receiver?.Dispose();
            base.Dispose();
            _sender.Dispose();
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }

        public async Task<bool> TryRequeueAsync(Envelope envelope)
        {
            if (envelope is not RabbitMqEnvelope e)
            {
                return false;
            }

            await e.Listener.RequeueAsync(e);
            return true;
        }

        public Uri Address { get; }

        public ValueTask CompleteAsync(Envelope envelope)
        {
            return RabbitMqChannelCallback.Instance.CompleteAsync(envelope);
        }

        public ValueTask DeferAsync(Envelope envelope)
        {
            return RabbitMqChannelCallback.Instance.DeferAsync(envelope);
        }

        public ValueTask RequeueAsync(RabbitMqEnvelope envelope)
        {
            if (!envelope.Acknowledged)
            {
                Channel.BasicNack(envelope.DeliveryTag, false, false);
            }

            return _sender.SendAsync(envelope);
        }

        public void Complete(ulong deliveryTag)
        {
            Channel.BasicAck(deliveryTag, false);
        }
    }
}
