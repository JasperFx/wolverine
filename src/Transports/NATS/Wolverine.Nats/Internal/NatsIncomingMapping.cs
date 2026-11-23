using NATS.Client.Core;
using NATS.Client.JetStream;
using Wolverine.Runtime.Serialization;

namespace Wolverine.Nats.Internal;

/// <summary>
/// GH-5924. Turns a received NATS message into an envelope with the endpoint's <see cref="INatsEnvelopeMapper"/>,
/// default or custom, and then does the NATS work that is not mapping: the tenant from where the message arrived,
/// the JetStream metadata headers, and the stream-position id of a JetStream message without one. Keeping that out
/// of the mapper is what lets a custom mapper or CloudEvents interop keep it.
/// </summary>
internal sealed class NatsIncomingMapping
{
    private readonly NatsEndpoint _endpoint;
    private readonly IncomingTenancy _tenancy;
    private readonly bool _customMapper;

    public NatsIncomingMapping(NatsEndpoint endpoint, INatsEnvelopeMapper mapper, ITenantSubjectMapper? tenantMapper)
    {
        _endpoint = endpoint;
        Mapper = mapper;
        _tenancy = new IncomingTenancy(endpoint, tenantMapper);
        _customMapper = endpoint.UsesCustomEnvelopeMapper;
    }

    public INatsEnvelopeMapper Mapper { get; }

    /// <summary>
    /// Map a core NATS message, or null for a message Wolverine cannot identify (see <see cref="isRecognizable"/>)
    /// </summary>
    public NatsEnvelope? TryMap(NatsMsg<byte[]> incoming)
    {
        var envelope = new NatsEnvelope(incoming, null);
        Mapper.MapIncomingToEnvelope(envelope, incoming);
        _tenancy.Apply(envelope, incoming.Subject);

        return isRecognizable(envelope) ? envelope : null;
    }

    /// <summary>
    /// Map a JetStream message, or null for a message Wolverine cannot identify (see <see cref="isRecognizable"/>)
    /// </summary>
    public NatsEnvelope? TryMap(INatsJSMsg<byte[]> incoming, JetStreamMessageIdentity? identity)
    {
        var envelope = CreateEnvelope(incoming, identity);
        return isRecognizable(envelope) ? envelope : null;
    }

    /// <summary>
    /// Builds the envelope for a consumed JetStream message. With an <paramref name="identity" />, a message the
    /// mapping gave no id of its own -- it carries no Wolverine <c>id</c> header, and no mapping set one -- gets the
    /// id of its place in the stream, so a redelivery of it is the same envelope to the durable inbox.
    /// </summary>
    internal NatsEnvelope CreateEnvelope(INatsJSMsg<byte[]> incoming, JetStreamMessageIdentity? identity)
    {
        var envelope = new NatsEnvelope(null, incoming);

        // A fresh envelope's id is a new IdGenerator() value nobody else has seen, so the id still being that value
        // after mapping means neither the id header nor any other mapping supplied one
        var provisionalId = envelope.Id;

        // Ahead of the mapping, so a wire header of the same name still wins, as it always has
        if (incoming.Metadata is { } metadata)
        {
            envelope.Headers["nats-stream"] = metadata.Stream;
            envelope.Headers["nats-consumer"] = metadata.Consumer;
            envelope.Headers["nats-delivered"] = metadata.NumDelivered.ToString();
            envelope.Headers["nats-pending"] = metadata.NumPending.ToString();
            envelope.Headers["nats-stream-seq"] = metadata.Sequence.Stream.ToString();
            envelope.Headers["nats-consumer-seq"] = metadata.Sequence.Consumer.ToString();
        }

        // The message as it was delivered. One mapper serves core NATS and JetStream
        var message = new NatsMsg<byte[]>(incoming.Subject, incoming.ReplyTo, incoming.Size, incoming.Headers,
            incoming.Data!, incoming.Connection);

        Mapper.MapIncomingToEnvelope(envelope, message);
        _tenancy.Apply(envelope, incoming.Subject);

        // A missing or unreadable id header is mapped to Guid.Empty instead, which the durable receiver replaces
        // with a fresh IdGenerator() value just before the inbox insert. Neither is an id.
        if (identity != null && incoming.Metadata is { } position &&
            (envelope.Id == provisionalId || envelope.Id == Guid.Empty))
        {
            envelope.Id = identity.IdFor(position.Sequence.Stream);
        }

        return envelope;
    }

    // The built in mapping skips a message it cannot name a message type for, as it always did for a message
    // without a message-type header -- unless the endpoint's serializer reads the type out of the body, the way
    // CloudEvents does. A custom mapper owns that decision, so everything it maps is handed on.
    private bool isRecognizable(Envelope envelope)
    {
        if (_customMapper || !string.IsNullOrEmpty(envelope.MessageType))
        {
            return true;
        }

        return (envelope.Serializer ?? _endpoint.DefaultSerializer) is IUnwrapsMetadataMessageSerializer;
    }
}
