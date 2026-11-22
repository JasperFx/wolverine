using System.Buffers;
using System.Collections.Concurrent;
using DotPulsar;
using DotPulsar.Abstractions;
using DotPulsar.Extensions;
using Wolverine.Pulsar.Internal;
using Wolverine.Runtime;
using Wolverine.Transports.Sending;

namespace Wolverine.Pulsar;

public class PulsarSender : ISender, IAsyncDisposable
{
    private readonly CancellationToken _cancellation;
    private readonly IPulsarEnvelopeMapper _mapper;
    private readonly IProducer<ReadOnlySequence<byte>> _producer;
    private readonly Schemas.IPulsarMessageCodec? _codec;
    private readonly bool _nativeScheduledSendEnabled;

    // GH-3185: producer deduplication state.
    private const int SequenceCacheLimit = 100_000;
    private readonly bool _deduplicationEnabled;
    private readonly ConcurrentDictionary<Guid, ulong>? _sequenceIds;
    private long _sequence;

    // GH-5847: a named producer the broker keeps refusing is failed by the transport's PulsarProducerNameGuard.
    // DotPulsar leaves a send already waiting on a faulted producer waiting, so the sender watches for the fault.
    private readonly PulsarProducerNameGuard? _producerNames;
    private readonly string? _producerName;
    private readonly string _topic;
    private readonly TaskCompletionSource? _faulted;

    /// <summary>
    ///     GH-5812. Appended to the producer name of a listener's requeue producer under deduplication.
    /// </summary>
    internal const string RequeueProducerNameSuffix = "-requeue";

    public PulsarSender(IWolverineRuntime runtime, PulsarEndpoint endpoint, PulsarTransport transport,
        CancellationToken cancellation) : this(runtime, endpoint, transport, cancellation, null)
    {
    }

    /// <summary>
    ///     GH-5768. <paramref name="topic" /> overrides the endpoint's primary topic: a listener over several topics
    ///     (<c>Topics()</c> or <c>TopicsPattern()</c>) requeues a message to the topic it came from.
    ///
    ///     <para>GH-5812. <paramref name="forRequeue" /> marks the listener's requeue producer. Under deduplication
    ///     it takes a producer name of its own, so it can sit on the same topic as the application's publishing
    ///     producer in the same host: Pulsar refuses a second producer with a name already connected to a topic.</para>
    /// </summary>
    internal PulsarSender(IWolverineRuntime runtime, PulsarEndpoint endpoint, PulsarTransport transport,
        CancellationToken cancellation, string? topic, bool forRequeue = false)
    {
        var endpoint1 = endpoint;
        _cancellation = cancellation;
        _codec = endpoint.MessageCodec;
        _nativeScheduledSendEnabled = transport.NativeScheduledSendEnabled;

        // GH-3183: when an endpoint schema is configured, create the producer with it so the broker
        // registers the schema for the topic. The schema is a pass-through over Wolverine's bytes, so the
        // builder is still IProducerBuilder<ReadOnlySequence<byte>> and the send path is unchanged.
        var producerBuilder = (endpoint.Schema != null
                ? transport.Client!.NewProducer(endpoint.Schema)
                : transport.Client!.NewProducer())
            .Topic(topic ?? endpoint1.PulsarTopic());
        _topic = topic ?? endpoint1.PulsarTopic();

        if (endpoint.DeduplicationEnabled)
        {
            _deduplicationEnabled = true;

            // A stable producer name is required: the broker tracks the last sequence id per producer
            // name, so dedup only works if the name is stable across producer sessions.
            var producerName = ProducerNameFor(runtime.Options.ServiceName, endpoint, forRequeue);
            producerBuilder.ProducerName(producerName);
            _producerName = producerName;
            _producerNames = transport.ProducerNames;
            _producerNames.Register(producerName, _topic);

            // Seed the sequence from the clock so a fresh producer session never reuses an id from a prior
            // one (DotPulsar 5.1.2 exposes no broker LastSequenceId to resume from). Ticks dwarfs any
            // realistic per-session message count, so post-restart ids stay strictly above prior ones.
            _sequence = DateTimeOffset.UtcNow.Ticks;
            _sequenceIds = new ConcurrentDictionary<Guid, ulong>();
        }

        endpoint.ConfigureProducer?.Invoke(producerBuilder);
        _producer = producerBuilder.Create();

        if (_producerNames != null)
        {
            _faulted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _ = watchForFaultAsync(_faulted);
        }

        Destination = endpoint1.Uri;
        _mapper = endpoint.BuildMapper(runtime);
    }

    /// <summary>
    ///     The stable producer name of a deduplicating producer: the name passed to <c>EnableDeduplication()</c>, or
    ///     <c>{ServiceName}-{topic name}</c>.
    ///
    ///     <para>GH-5812: the listener's requeue producer and a publishing producer for the same topic used to take
    ///     the same name, and the broker refused whichever connected second (ProducerBusy). The requeue producer's
    ///     own name also gives it its own sequence space, which suits copies: each one already takes a fresh
    ///     sequence id (GH-5768).</para>
    /// </summary>
    internal static string ProducerNameFor(string serviceName, PulsarEndpoint endpoint, bool forRequeue)
    {
        var producerName = endpoint.ProducerName ?? $"{serviceName}-{endpoint.TopicName}";

        // GH-5847: Pulsar allows one connected producer per name on a topic, so with ProducerNamePerInstance()
        // every running instance of the service gets a name of its own
        if (endpoint.Parent.ProducerNameInstanceId is { } instanceId)
        {
            producerName += "-" + instanceId;
        }

        if (forRequeue)
        {
            producerName += RequeueProducerNameSuffix;
        }

        return producerName;
    }

    public ValueTask DisposeAsync()
    {
        return _producer.DisposeAsync();
    }

    // GH-3470: scheduled/delayed sends are honored natively by stamping Pulsar's deliver-at
    // metadata in SendAsync. Can be turned off transport-wide (e.g. a broker with
    // delayedDeliveryEnabled=false) via DisableNativeScheduledSend(), which makes Wolverine
    // fall back to its own durable scheduled-message storage instead.
    public bool SupportsNativeScheduledSend => _nativeScheduledSendEnabled;
    public Uri Destination { get; }

    public async Task<bool> PingAsync()
    {
        var envelope = Envelope.ForPing(Destination);
        try
        {
            await SendAsync(envelope);
        }
        catch
        {
            return false;
        }

        return true;
    }

    public async ValueTask SendAsync(Envelope envelope)
    {
        var message = buildMetadata(envelope);

        if (_deduplicationEnabled && _sequenceIds != null)
        {
            // Reuse the same sequence id for repeat sends of the same envelope (e.g. outbox resends) so
            // the broker discards the duplicate; a brand-new envelope gets the next monotonic id.
            if (_sequenceIds.Count > SequenceCacheLimit)
            {
                _sequenceIds.Clear();
            }

            message.SequenceId = _sequenceIds.GetOrAdd(envelope.Id, _ => (ulong)Interlocked.Increment(ref _sequence));
        }

        await sendAsync(envelope, message);
    }

    /// <summary>
    ///     GH-5768. Put a copy of a received message back on this sender's topic (a requeue). The copy keeps the
    ///     key and ordering key it was received with, so a keyed message from another producer stays on its
    ///     partition and its Key_Shared consumer.
    ///
    ///     <para>Under deduplication the copy never reuses the envelope's cached sequence id. A message requeued
    ///     twice is the same envelope id both times, and Pulsar drops every message whose sequence id is not above
    ///     the highest one it has seen from the producer -- the second copy was dropped as a duplicate while its
    ///     original had already been acked. Each attempt takes the next id; a retried attempt whose first send did
    ///     land is at worst a duplicate, where a reused id after any later send would be a loss.</para>
    /// </summary>
    internal async ValueTask SendCopyAsync(Envelope envelope, IMessage<ReadOnlySequence<byte>> received)
    {
        var message = buildMetadata(envelope);
        CopyKeys(received, message);

        if (_deduplicationEnabled)
        {
            message.SequenceId = (ulong)Interlocked.Increment(ref _sequence);
        }

        await sendAsync(envelope, message);
    }

    /// <summary>
    ///     GH-5768. Carry the key and ordering key a message was received with onto a copy of it, unless a custom
    ///     mapper already set its own.
    /// </summary>
    internal static void CopyKeys(IMessage<ReadOnlySequence<byte>> received, MessageMetadata outgoing)
    {
        if (received.HasKey && string.IsNullOrEmpty(outgoing.Key))
        {
            if (received.HasBase64EncodedKey)
            {
                outgoing.KeyBytes = received.KeyBytes;
            }
            else
            {
                outgoing.Key = received.Key;
            }
        }

        if (received.HasOrderingKey && outgoing.OrderingKey is not { Length: > 0 })
        {
            outgoing.OrderingKey = received.OrderingKey;
        }
    }

    private MessageMetadata buildMetadata(Envelope envelope)
    {
        var message = new MessageMetadata();

        _mapper.MapEnvelopeToOutgoing(envelope, message);

        if (_nativeScheduledSendEnabled)
        {
            ApplyScheduledDelivery(envelope, message);
        }

        return message;
    }

    private async Task watchForFaultAsync(TaskCompletionSource faulted)
    {
        try
        {
            // Completes on a final state too, so a closed producer ends the watch
            var changed = await _producer.StateChangedTo(ProducerState.Faulted, _cancellation);
            if (changed?.ProducerState == ProducerState.Faulted)
            {
                faulted.TrySetResult();
            }
        }
        catch (Exception)
        {
            // Shutting down
        }
    }

    private async ValueTask sendAsync(Envelope envelope, MessageMetadata message)
    {
        if (_faulted == null)
        {
            await sendToProducerAsync(envelope, message, _cancellation);
            return;
        }

        if (_faulted.Task.IsCompleted)
        {
            throw producerFailed(null);
        }

        // GH-5847: a send waiting on a producer that is then failed would wait forever
        using var sending = CancellationTokenSource.CreateLinkedTokenSource(_cancellation);
        var send = sendToProducerAsync(envelope, message, sending.Token).AsTask();
        if (await Task.WhenAny(send, _faulted.Task) != send)
        {
            await sending.CancelAsync();
            throw producerFailed(null);
        }

        try
        {
            await send;
        }
        catch (Exception e) when (_producerNames!.ErrorFor(_producerName!, _topic) is not null)
        {
            throw producerFailed(e);
        }
    }

    private Exception producerFailed(Exception? inner)
    {
        // A new exception per failed send, so concurrent throws never share one instance
        if (_producerNames?.ErrorFor(_producerName!, _topic) is { } refused)
        {
            return new PulsarProducerNameInUseException(refused.ProducerName, refused.Topic, refused.RefusedFor,
                inner ?? refused.InnerException);
        }

        return new InvalidOperationException(
            $"The Pulsar producer '{_producerName}' for topic '{_topic}' has faulted and cannot send", inner);
    }

    private async ValueTask sendToProducerAsync(Envelope envelope, MessageMetadata message,
        CancellationToken cancellation)
    {
        // GH-3213: for a schema that owns the encoding (Avro), encode the message object directly so the
        // body is genuine schema-encoded bytes. Non-codec messages (e.g. a ping) on the same endpoint fall
        // back to Wolverine's serialized body.
        if (_codec != null && envelope.Message != null && _codec.MessageType.IsInstanceOfType(envelope.Message))
        {
            await _producer.Send(message, _codec.Encode(envelope.Message), cancellation);
            return;
        }

        await _producer.Send(message, new ReadOnlySequence<byte>(envelope.Data!), cancellation);
    }

    /// <summary>
    ///     GH-3470: map <see cref="Envelope.ScheduledTime"/> onto Pulsar's broker-native delayed
    ///     delivery metadata (DeliverAt) so the broker itself holds the message until it is due,
    ///     instead of publishing immediately and relying on the receiving node to delay execution.
    ///     Runs after the envelope mapper so a custom <see cref="IPulsarEnvelopeMapper"/> that has
    ///     already stamped its own deliver-at metadata is left alone. Note that the Pulsar broker
    ///     only honors deliver-at for Shared / KeyShared subscriptions on the consuming side —
    ///     Exclusive / Failover consumers see the message immediately, in which case Wolverine
    ///     listeners still fall back to holding the envelope locally until its scheduled time via
    ///     the round-tripped scheduled-time header.
    /// </summary>
    internal static void ApplyScheduledDelivery(Envelope envelope, MessageMetadata outgoing)
    {
        // Respect deliver-at metadata a custom interop mapper may have stamped itself
        if (outgoing.DeliverAtTime != 0)
        {
            return;
        }

        if (envelope.ScheduledTime.HasValue)
        {
            outgoing.DeliverAtTimeAsDateTimeOffset = envelope.ScheduledTime.Value.ToUniversalTime();
        }
    }
}