using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using Wolverine.Transports;

namespace Wolverine.RabbitMQ.Internal;

internal class WorkerQueueMessageConsumer : AsyncDefaultBasicConsumer, IDisposable
{
    private readonly Uri _address;
    private readonly CancellationToken _cancellation;
    private readonly RabbitMqListener _listener;
    private readonly ILogger _logger;
    private readonly IRabbitMqEnvelopeMapper _mapper;
    private readonly IReceiver _workerQueue;
    private bool _latched;

    public WorkerQueueMessageConsumer(IChannel channel, IReceiver workerQueue, ILogger logger,
        RabbitMqListener listener,
        IRabbitMqEnvelopeMapper mapper, Uri address, CancellationToken cancellation) : base(channel)
    {
        _workerQueue = workerQueue;
        _logger = logger;
        _listener = listener;
        _mapper = mapper;
        _address = address;
        _cancellation = cancellation;
    }

    public void Dispose()
    {
        _latched = true;
    }

    //TODO do something with the token passed in here
    public override async Task HandleBasicDeliverAsync(string consumerTag, ulong deliveryTag, bool redelivered, string exchange,
        string routingKey, IReadOnlyBasicProperties properties, ReadOnlyMemory<byte> body,
        CancellationToken cancellationToken = new())
    {
        if (_latched || _cancellation.IsCancellationRequested || !_listener.IsConnected)
        {
            await _listener.Channel!.BasicRejectAsync(deliveryTag, true, _cancellation);
            return;
        }

        var envelope = new RabbitMqEnvelope(_listener, deliveryTag);

        try
        {
            envelope.Data = body.ToArray();
            _mapper.MapIncomingToEnvelope(envelope, properties);
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error trying to map an incoming RabbitMQ message {MessageId} to an Envelope", properties.MessageId);
            await Channel.BasicAckAsync(envelope.DeliveryTag, false, _cancellation);

            return;
        }

        if (envelope.IsPing())
        {
            await Channel.BasicAckAsync(deliveryTag, false, _cancellation);
            return;
        }

        try
        {
            await _workerQueue.ReceivedAsync(_listener, envelope);
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Failure to receive an incoming message with {Id}, trying to 'Nack' the message", envelope.Id);
            try
            {
                await Channel.BasicNackAsync(deliveryTag, false, true, _cancellation);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failure trying to Nack a previously failed message {Id}", envelope.Id);
            }
        }
    }
}