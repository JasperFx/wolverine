using NATS.Client.Core;
using Wolverine.Transports;

namespace Wolverine.Nats;

/// <summary>
/// Maps Wolverine envelopes to and from NATS messages. The built in <see cref="Internal.NatsEnvelopeMapper"/> writes
/// Wolverine's own headers; a custom implementation plugged in with <c>UseInterop(...)</c> on a listener or
/// subscriber is how a NATS endpoint talks to publishers and consumers that are not Wolverine.
///
/// <para>
/// One mapper covers both core NATS and JetStream. A message consumed from JetStream reaches
/// <see cref="IIncomingMapper{TIncoming}.MapIncomingToEnvelope"/> as the <see cref="NatsMsg{T}"/> it was delivered
/// as -- subject, reply subject, headers and data -- and its JetStream metadata (stream, consumer, sequences,
/// delivery count) is already on <see cref="Envelope.Headers"/> as the <c>nats-*</c> headers. Outgoing, the mapper
/// writes the <see cref="NatsHeaders"/>; the message body is <see cref="Envelope.Data"/>, written by the endpoint's
/// serializer.
/// </para>
/// </summary>
public interface INatsEnvelopeMapper : IEnvelopeMapper<NatsMsg<byte[]>, NatsHeaders>;
