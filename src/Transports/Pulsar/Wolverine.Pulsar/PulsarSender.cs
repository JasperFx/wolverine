using System.Buffers;
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