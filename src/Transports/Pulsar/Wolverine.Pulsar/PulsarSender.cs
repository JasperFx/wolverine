using System.Buffers;
using System.Collections.Concurrent;
using DotPulsar;
using DotPulsar.Abstractions;
using DotPulsar.Extensions;
using Wolverine.Runtime;
using Wolverine.Transports.Sending;

namespace Wolverine.Pulsar;

public class PulsarSender : ISender, IAsyncDisposable
{
    private readonly CancellationToken _cancellation;
    private readonly IPulsarEnvelopeMapper _mapper;
    private readonly IProducer<ReadOnlySequence<byte>> _producer;
    private readonly Schemas.IPulsarMessageCodec? _codec;

    // GH-3185: producer deduplication state.
    private const int SequenceCacheLimit = 100_000;
    private readonly bool _deduplicationEnabled;
    private readonly ConcurrentDictionary<Guid, ulong>? _sequenceIds;
    private long _sequence;

    public PulsarSender(IWolverineRuntime runtime, PulsarEndpoint endpoint, PulsarTransport transport,
        CancellationToken cancellation)
    {
        var endpoint1 = endpoint;
        _cancellation = cancellation;
        _codec = endpoint.MessageCodec;

        // GH-3183: when an endpoint schema is configured, create the producer with it so the broker
        // registers the schema for the topic. The schema is a pass-through over Wolverine's bytes, so the
        // builder is still IProducerBuilder<ReadOnlySequence<byte>> and the send path is unchanged.
        var producerBuilder = (endpoint.Schema != null
                ? transport.Client!.NewProducer(endpoint.Schema)
                : transport.Client!.NewProducer())
            .Topic(endpoint1.PulsarTopic());

        if (endpoint.DeduplicationEnabled)
        {
            _deduplicationEnabled = true;

            // A stable producer name is required: the broker tracks the last sequence id per producer
            // name, so dedup only works if the name is stable across producer sessions.
            producerBuilder.ProducerName(endpoint.ProducerName ?? $"{runtime.Options.ServiceName}-{endpoint1.TopicName}");

            // Seed the sequence from the clock so a fresh producer session never reuses an id from a prior
            // one (DotPulsar 5.1.2 exposes no broker LastSequenceId to resume from). Ticks dwarfs any
            // realistic per-session message count, so post-restart ids stay strictly above prior ones.
            _sequence = DateTimeOffset.UtcNow.Ticks;
            _sequenceIds = new ConcurrentDictionary<Guid, ulong>();
        }

        endpoint.ConfigureProducer?.Invoke(producerBuilder);
        _producer = producerBuilder.Create();

        Destination = endpoint1.Uri;
        _mapper = endpoint.BuildMapper(runtime);
    }

    public ValueTask DisposeAsync()
    {
        return _producer.DisposeAsync();
    }

    public bool SupportsNativeScheduledSend => true;
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
        var message = new MessageMetadata();

        _mapper.MapEnvelopeToOutgoing(envelope, message);

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

        // GH-3213: for a schema that owns the encoding (Avro), encode the message object directly so the
        // body is genuine schema-encoded bytes. Non-codec messages (e.g. a ping) on the same endpoint fall
        // back to Wolverine's serialized body.
        if (_codec != null && envelope.Message != null && _codec.MessageType.IsInstanceOfType(envelope.Message))
        {
            await _producer.Send(message, _codec.Encode(envelope.Message), _cancellation);
            return;
        }

        await _producer.Send(message, new ReadOnlySequence<byte>(envelope.Data!), _cancellation);
    }
}