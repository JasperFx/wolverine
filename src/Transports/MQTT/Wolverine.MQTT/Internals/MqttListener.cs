using System.Text;
using JasperFx.Blocks;
using JasperFx.Core;
using Microsoft.Extensions.Logging;
using MQTTnet.Client;
using Wolverine.Runtime;
using Wolverine.Transports;

namespace Wolverine.MQTT.Internals;

internal class MqttListener : IListener
{
    private readonly MqttTransport _broker;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly RetryBlock<MqttEnvelope> _complete;
    private readonly RetryBlock<MqttEnvelope> _defer;
    private readonly ILogger _logger;
    private readonly IReceiver _receiver;
    private readonly MqttTopic _topic;


    public MqttListener(MqttTransport broker, ILogger logger, MqttTopic topic, IReceiver receiver)
    {
        _broker = broker;
        _logger = logger;
        _topic = topic;
        _receiver = receiver;
        Address = topic.Uri;

        TopicName = topic.ListeningTopic;

        _complete = new RetryBlock<MqttEnvelope>(
            async (e, _) => { await e.Args.AcknowledgeAsync(_cancellation.Token); }, _logger, _cancellation.Token);

        _defer = new RetryBlock<MqttEnvelope>(async (envelope, _) =>
        {
            if (!envelope.IsAcked)
            {
                await envelope.Args.AcknowledgeAsync(_cancellation.Token);
                envelope.IsAcked = true;
            }

            // Really just an inline retry
            await _receiver.ReceivedAsync(this, envelope);
        }, logger, _cancellation.Token);
    }

    public string TopicName { get; }

    public Uri Address { get; }

    public ValueTask StopAsync()
    {
        return ValueTask.CompletedTask;
    }

    public IHandlerPipeline? Pipeline => _receiver.Pipeline;

    public ValueTask CompleteAsync(Envelope envelope)
    {
        if (envelope is MqttEnvelope e)
        {
            return new ValueTask(_complete.PostAsync(e));
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask DeferAsync(Envelope envelope)
    {
        if (envelope is MqttEnvelope e)
        {
            return new ValueTask(_defer.PostAsync(e));
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        _cancellation.Cancel();
        _complete.SafeDispose();
        _defer.SafeDispose();

        return ValueTask.CompletedTask;
    }

    public async Task ReceiveAsync(MqttApplicationMessageReceivedEventArgs args)
    {
        // Wolverine should handle this regardless
        args.AutoAcknowledge = false;

        var envelope = new MqttEnvelope(_topic, args);

        try
        {
            _topic.EnvelopeMapper.MapIncomingToEnvelope(envelope, args.ApplicationMessage);
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error trying to map an incoming MQTT message {MessageId} to an Envelope",
                args.ApplicationMessage.CorrelationData != null
                    ? Encoding.Default.GetString(args.ApplicationMessage.CorrelationData)
                    : "(none)");

            // MoveToErrorsAsync keys the envelope by Id; the mapper threw before
            // setting one, so synthesize a Guid to satisfy the dead-letter store contract.
            // MqttEnvelope already populates Data and Destination in its constructor.
            if (envelope.Id == Guid.Empty)
            {
                envelope.Id = Guid.NewGuid();
            }

            var dlq = _receiver as ISupportDeadLetterQueue;
            if (dlq is not null)
            {
                try
                {
                    await dlq.MoveToErrorsAsync(envelope, e);
                }
                catch (Exception moveEx)
                {
                    _logger.LogError(moveEx,
                        "Failed to move un-mappable MQTT message {MessageId} to the dead-letter store; falling back to ack to avoid poison redelivery",
                        envelope.Id);
                }
            }

            // Always PUBACK. If MoveToErrorsAsync succeeded, the dead-letter store has
            // the record. If not (no durable inbox, or it threw), acking is still the
            // best available option because MQTT has no broker DLQ and leaving the
            // message unacked would cause a poison-redelivery loop.
            try
            {
                await _complete.PostAsync(envelope);
            }
            catch (Exception ackEx)
            {
                _logger.LogError(ackEx,
                    "Failed to ack un-mappable MQTT message {MessageId}",
                    envelope.Id);
            }
            return;
        }

        if (envelope.IsPing())
        {
            await _complete.PostAsync(envelope);
            return;
        }

        try
        {
            await _receiver.ReceivedAsync(this, envelope);
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Failure to receive an incoming message with {Id}, trying to 'Nack' the message",
                envelope.Id);
            try
            {
                await _defer.PostAsync(envelope);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failure trying to Nack a previously failed message {Id}", envelope.Id);
            }
        }
    }
}