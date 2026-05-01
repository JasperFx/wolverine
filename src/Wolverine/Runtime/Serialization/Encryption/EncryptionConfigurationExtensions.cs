namespace Wolverine.Runtime.Serialization.Encryption;

/// <summary>
/// Per-endpoint outgoing rule that swaps each envelope to the encrypting serializer.
/// The encrypting serializer is resolved at endpoint compile time by the
/// <c>Encrypted()</c> instance method on <see cref="Configuration.SubscriberConfiguration{T, TEndpoint}"/>
/// and injected into this rule via the constructor.
/// </summary>
internal sealed class EncryptOutgoingEndpointRule : IEnvelopeRule
{
    private readonly IMessageSerializer _encryptingSerializer;

    public EncryptOutgoingEndpointRule(IMessageSerializer encryptingSerializer)
    {
        _encryptingSerializer = encryptingSerializer ?? throw new ArgumentNullException(nameof(encryptingSerializer));
    }

    public void Modify(Envelope envelope)
    {
        // Mirror MessageRoute.cs:114-115 — swap Serializer and ContentType together.
        envelope.Serializer = _encryptingSerializer;
        envelope.ContentType = _encryptingSerializer.ContentType;
    }

    public override string ToString() =>
        $"Encrypt outgoing envelopes via {EncryptionHeaders.EncryptedContentType}";
}
