using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using Wolverine.Transports;

namespace Wolverine.RabbitMQ.Internal
{
    internal class WorkerQueueMessageConsumer : DefaultBasicConsumer, IDisposable
    {
        private readonly Uri _address;
        private readonly CancellationToken _cancellation;
        private readonly RabbitMqEndpoint _endpoint;
        private readonly ILogger _logger;
        private readonly IReceiver _workerQueue;
        private readonly RabbitMqListener _listener;
        private bool _latched;

        public WorkerQueueMessageConsumer(IReceiver workerQueue, ILogger logger,
            RabbitMqListener listener,
            RabbitMqEndpoint endpoint, Uri address, CancellationToken cancellation)
        {
            _workerQueue = workerQueue;
            _logger = logger;
            _listener = listener;
            _endpoint = endpoint;
            _address = address;
            _cancellation = cancellation;
        }

        public void Dispose()
        {
            _latched = true;
        }

        public override void HandleBasicDeliver(string consumerTag, ulong deliveryTag, bool redelivered,
            string exchange, string routingKey,
            IBasicProperties properties, ReadOnlyMemory<byte> body)
        {
            if (_latched || _cancellation.IsCancellationRequested)
            {
                _listener.Channel.BasicReject(deliveryTag, true);
                return;
            }

            var envelope = new RabbitMqEnvelope(_listener, deliveryTag);
            try
            {
                envelope.Data = body.ToArray(); // TODO -- use byte sequence instead!
                _endpoint.MapIncomingToEnvelope(envelope, properties);
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Error trying to map an incoming RabbitMQ message to an Envelope");
                Model.BasicAck(envelope.DeliveryTag, false);

                return;
            }

            if (envelope.IsPing())
            {
                Model.BasicAck(deliveryTag, false);
                return;
            }

#pragma warning disable VSTHRD110
            _workerQueue.ReceivedAsync(_listener, envelope).AsTask().ContinueWith(t =>
#pragma warning restore VSTHRD110
            {
                if (t.IsFaulted)
                {
                    _logger.LogError(t.Exception, "Failure to receive an incoming message with {Id}", envelope.Id);
                    Model.BasicNack(deliveryTag, false, true);
                }
            }, _cancellation, TaskContinuationOptions.None, TaskScheduler.Default);
        }
    }
}
