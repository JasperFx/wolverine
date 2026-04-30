using JasperFx.Core.Reflection;
using Wolverine.Runtime.Serialization;

namespace Wolverine.Runtime.Serialization.Encryption;

/// <summary>
/// Outgoing-envelope rule that swaps the serializer to an encrypting variant
/// for messages assignable to <typeparamref name="T"/>. Captures the
/// encrypting serializer at construction time so the rule needs no runtime
/// lookup at modify time.
/// </summary>
internal sealed class EncryptMessageTypeRule<T> : IEnvelopeRule
{
    private readonly IMessageSerializer _encryptingSerializer;

    public EncryptMessageTypeRule(IMessageSerializer encryptingSerializer)
    {
        _encryptingSerializer = encryptingSerializer
            ?? throw new ArgumentNullException(nameof(encryptingSerializer));
    }

    public void Modify(Envelope envelope)
    {
        if (envelope.Message is null) return;
        if (!envelope.Message.GetType().CanBeCastTo<T>()) return;

        // Mirror MessageRoute.cs:114-115 — swap Serializer and ContentType together.
        // ContentType alone is not enough; envelope.Serializer.Write(envelope) is what
        // actually produces the wire bytes.
        envelope.Serializer = _encryptingSerializer;
        envelope.ContentType = _encryptingSerializer.ContentType;
    }

    public override string ToString() =>
        $"Encrypt messages assignable to {typeof(T).FullNameInCode()}";
}
