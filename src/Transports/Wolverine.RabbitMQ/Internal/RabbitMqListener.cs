using System;
using System.Threading;
using System.Threading.Tasks;
using Baseline;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using Wolverine.Configuration;
using Wolverine.Runtime;
using Wolverine.Transports;

namespace Wolverine.RabbitMQ.Internal
{
    internal class RabbitMqListener : RabbitMqConnectionAgent, IListener
    {
        private readonly ILogger _logger;
        private readonly string _routingKey;
        private readonly RabbitMqSender _sender;
        private IReceiver _receiver;
        private CancellationToken _cancellation = CancellationToken.None;
        private WorkerQueueMessageConsumer? _consumer;

        public RabbitMqListener(IWolverineRuntime runtime,
            RabbitMqQueue queue, RabbitMqTransport transport, IReceiver receiver) : base(transport.ListeningConnection, queue, runtime.Logger)
        {
            _logger = runtime.Logger;
            Queue = queue;
            Address = queue.Uri;

            _routingKey = queue.QueueName;

            _sender = new RabbitMqSender(Queue, transport, RoutingMode.Static, runtime);

            _cancellation.Register(teardownChannel);

            EnsureConnected();

            if (queue.AutoDelete || transport.AutoProvision)
            {
                queue.Declare(Channel, runtime.Logger);
            }

            var mapper = queue.BuildMapper(runtime);
            
            _receiver = receiver ?? throw new ArgumentNullException(nameof(receiver));
            _consumer = new WorkerQueueMessageConsumer(receiver, _logger, this, mapper, Address,
                _cancellation);

            Channel.BasicQos(Queue.PreFetchSize, Queue.PreFetchCount, false);

            Channel.BasicConsume(_consumer, _routingKey);
        }

        public void Stop()
        {
            if (_consumer == null) return;
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

        public RabbitMqQueue Queue { get; }

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

            await e.RabbitMqListener.RequeueAsync(e);
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
            Channel.BasicAck(deliveryTag, true);
        }
    }
}
