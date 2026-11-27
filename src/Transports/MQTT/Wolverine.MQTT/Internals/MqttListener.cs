using System.Collections.Concurrent;
using System.Text;
using JasperFx.Blocks;
using JasperFx.Core;
using Microsoft.Extensions.Logging;
using MQTTnet;
using MQTTnet.Extensions.ManagedClient;
using Wolverine.Runtime;
using Wolverine.Transports;

namespace Wolverine.MQTT.Internals;

internal class MqttListener : IListener, IReportConnectionState
{
    private readonly MqttTransport _broker;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly RetryBlock<MqttEnvelope> _complete;

    // GH-5770. Cancelled by StopAsync (and DisposeAsync): from then on a deferred message is handed back rather
    // than offered to the receiver again. Never disposed, because a pending redelivery may still read it.
    private readonly CancellationTokenSource _stopping = new();

    // GH-5770. Deferred messages this listener handed back after it stopped. They are still unacknowledged, so
    // the broker counts them as in flight on this connection: the listener that replaces this one on the same
    // connection takes them over (TakeOverHandedBackMessages), and if none does, the broker redelivers them when
    // a persistent session resumes.
    private readonly ConcurrentQueue<MqttEnvelope> _handedBack = new();
    private readonly ILogger _logger;
    private readonly IReceiver _receiver;
    private readonly MqttTopic _topic;

    // Broker-per-tenant (GH-3307): the managed client this listener consumes from — the default/shared client
    // for the untenanted listener, or a tenant's own dedicated client. ConnectionState reports on this specific
    // connection so a dropped tenant connection is surfaced independently of the default one.
    private readonly IManagedMqttClient _client;

    // GH-3231: surface the managed MQTT client's connection state so external monitors can detect a listener whose
    // broker connection has dropped (and not resubscribed) while it still reports Accepting.
    public TransportConnectionState ConnectionState =>
        _client.IsConnected ? TransportConnectionState.Connected : TransportConnectionState.Disconnected;

    public MqttListener(MqttTransport broker, ILogger logger, MqttTopic topic, IReceiver receiver,
        IManagedMqttClient client)
    {
        _broker = broker;
        _logger = logger;
        _topic = topic;
        _receiver = receiver;
        _client = client;
        Address = topic.Uri;

        TopicName = topic.ListeningTopic;

        _complete = new RetryBlock<MqttEnvelope>(async (e, _) =>
        {
            // MQTTnet throws on a second acknowledgement, and a message deferred after BufferedInMemory acked it
            // on receipt comes back through receipt and is completed again
            if (e.IsAcked) return;

            await e.Args.AcknowledgeAsync(_cancellation.Token);
            e.IsAcked = true;
        }, _logger, _cancellation.Token);
    }

    /// <summary>
    ///     GH-5770. How long a deferred message waits before it is offered to the receiver again. This bounds how
    ///     fast a receiver that keeps deferring can spin.
    /// </summary>
    internal static TimeSpan DeferredRedeliveryDelay { get; set; } = TimeSpan.FromSeconds(1);

    public string TopicName { get; }

    public Uri Address { get; }

    // #5980. A scheduled retry this listener cannot schedule natively goes to the durable inbox, and the original
    // delivery is then acknowledged, or the broker redelivers it and the handler runs twice
    public bool RequiresAckAfterPersistedRetry => true;

    internal bool IsStopped => _stopping.IsCancellationRequested;

    public async ValueTask StopAsync()
    {
        await _stopping.CancelAsync();
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

    /// <summary>
    ///     GH-5770. A deferred message is NOT acknowledged. MQTT has no way to hand a delivery back to the broker
    ///     on a live connection, so a running listener offers it to the receiver again, on its own task after
    ///     <see cref="DeferredRedeliveryDelay" />, and it is acknowledged only once something completes it. A stopped
    ///     listener hands it back instead (see <see cref="_handedBack" />).
    ///
    ///     <para>This used to PUBACK the message and then feed it straight back into the receiver inline. Once
    ///     acknowledged the broker never redelivers it, so a re-feed that failed, or a process exit, lost it; and a
    ///     latched receiver deferred the re-feed again, recursing for as long as the listener object lived.</para>
    /// </summary>
    public ValueTask DeferAsync(Envelope envelope)
    {
        if (envelope is MqttEnvelope e)
        {
            defer(e);
        }

        return ValueTask.CompletedTask;
    }

    private void defer(MqttEnvelope envelope)
    {
        if (IsStopped)
        {
            handBack(envelope);
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(DeferredRedeliveryDelay, _stopping.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                handBack(envelope);
                return;
            }

            try
            {
                await _receiver.ReceivedAsync(this, envelope).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Error redelivering deferred MQTT message {MessageId} at {Uri}", envelope.Id,
                    Address);

                // Still unacknowledged, so it is still ours to deliver
                defer(envelope);
            }
        }, CancellationToken.None);
    }

    private void handBack(MqttEnvelope envelope)
    {
        _handedBack.Enqueue(envelope);

        if (_logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug(
                "Handing back deferred MQTT message {MessageId} at {Uri} unacknowledged because the listener has stopped",
                envelope.Id, Address);
        }
    }

    /// <summary>
    ///     GH-5770. Called on the listener that replaces <paramref name="previous" /> on the same connection, for
    ///     instance when a paused listener restarts. The broker will not redeliver the messages the previous
    ///     listener handed back while this connection stays up, so this listener offers them to its receiver.
    /// </summary>
    internal void TakeOverHandedBackMessages(MqttListener previous)
    {
        while (previous._handedBack.TryDequeue(out var envelope))
        {
            defer(envelope);
        }
    }

    /// <summary>
    ///     GH-5790. Messages that reached this connection before this listener registered, held unacknowledged by
    ///     the transport. Received in order, on their own task so registration does not wait on them.
    /// </summary>
    internal void ReceiveHeldMessages(IReadOnlyList<MqttApplicationMessageReceivedEventArgs> held)
    {
        _ = Task.Run(async () =>
        {
            foreach (var args in held)
            {
                try
                {
                    await ReceiveAsync(args).ConfigureAwait(false);
                }
                catch (Exception e)
                {
                    _logger.LogError(e, "Error receiving a held MQTT message for topic {TopicName}",
                        args.ApplicationMessage.Topic);
                }
            }
        }, CancellationToken.None);
    }

    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync();
        await _cancellation.CancelAsync();
        _cancellation.Dispose();
        _complete.SafeDispose();
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
                    ? Encoding.UTF8.GetString(args.ApplicationMessage.CorrelationData)
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

        // GH-5770. A stopped listener stays registered for its topic until a new one replaces it, and its
        // receiver is latched or gone, so keep the message unacknowledged for that replacement
        if (IsStopped)
        {
            handBack(envelope);
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
            defer(envelope);
        }
    }
}
