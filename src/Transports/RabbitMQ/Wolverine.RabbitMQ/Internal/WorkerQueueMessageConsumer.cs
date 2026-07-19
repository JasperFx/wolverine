using JasperFx.Blocks;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using Wolverine.Configuration;
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

    // GH-3492: durable endpoints coalesce prefetched deliveries into Envelope[] batches so the
    // inbox persists them with one multi-VALUES insert instead of gating on one INSERT round
    // trip per message. The 5ms window is a max-age (JasperFx >= 2.30.1), so a lone message
    // pays at most 5ms; a firehose batches at MaximumMessagesToReceive. Null for
    // Buffered/Inline endpoints, which keep the direct per-delivery path.
    private readonly BatchingChannel<Envelope>? _batching;

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

        if (listener.Queue.Mode == EndpointMode.Durable && listener.Queue.MaximumMessagesToReceive > 1)
        {
            var flush = new Block<Envelope[]>((batch, _) => deliverBatchAsync(batch));
            _batching = new BatchingChannel<Envelope>(TimeSpan.FromMilliseconds(5), flush,
                listener.Queue.MaximumMessagesToReceive);
        }
    }

    private async Task deliverBatchAsync(Envelope[] batch)
    {
        try
        {
            await _workerQueue.ReceivedAsync(_listener, batch);
        }
        catch (Exception e)
        {
            _logger.LogError(e,
                "Failure receiving a batch of {Count} incoming messages at {Address}, trying to 'Nack' them",
                batch.Length, _address);

            foreach (var envelope in batch.OfType<RabbitMqEnvelope>())
            {
                try
                {
                    await Channel.BasicNackAsync(envelope.DeliveryTag, false, true, _cancellation);
                }
                catch (Exception nackEx)
                {
                    _logger.LogError(nackEx, "Failed to Nack message {Id} at {Address}", envelope.Id, _address);
                }
            }
        }
    }

    public void Dispose()
    {
        _latched = true;
        // Push any accumulated-but-unflushed deliveries onward; anything genuinely in flight
        // is unacked and will be redelivered by the broker (and deduplicated by the inbox).
        _batching?.TriggerBatch();
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

            // MoveToErrorsAsync keys the envelope by Id; the mapper threw before
            // setting one, so synthesize a Guid to satisfy the dead-letter store contract.
            if (envelope.Id == Guid.Empty)
            {
                envelope.Id = Guid.NewGuid();
            }

            try
            {
                if (_workerQueue is ISupportDeadLetterQueue dlq)
                {
                    await dlq.MoveToErrorsAsync(envelope, e);
                    return;
                }
            }
            catch (Exception moveEx)
            {
                _logger.LogError(moveEx,
                    "Failed to move un-mappable RabbitMQ message {MessageId} to the dead-letter store; falling back to broker DLX",
                    properties.MessageId);
            }

            try
            {
                await Channel.BasicNackAsync(envelope.DeliveryTag, multiple: false, requeue: false, _cancellation);
            }
            catch (Exception nackEx)
            {
                _logger.LogError(nackEx,
                    "Failed to Nack un-mappable RabbitMQ message {MessageId}",
                    properties.MessageId);
            }

            return;
        }

        if (envelope.IsPing())
        {
            await Channel.BasicAckAsync(deliveryTag, false, _cancellation);
            return;
        }

        if (_batching != null)
        {
            // Durable micro-batching (GH-3492): hand off to the batching channel and return so
            // the dispatch loop can pull the next prefetched delivery; the flush block owns
            // failure handling + nacks from here.
            await _batching.PostAsync(envelope);
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