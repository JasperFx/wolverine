using Wolverine.Configuration;
using Wolverine.Transports.Local;

namespace Wolverine.Runtime.Serialization.Encryption;

public static class EncryptionConfigurationExtensions
{
    /// <summary>
    /// Force this subscriber/sender endpoint to use the encrypting serializer
    /// for all outgoing messages. The encrypting serializer must already be
    /// registered via <see cref="WolverineOptions.UseEncryption"/> or
    /// <see cref="WolverineOptions.RegisterEncryptionSerializer"/>.
    /// </summary>
    public static T Encrypted<T>(this T configuration) where T : ISubscriberConfiguration<T>
    {
        var rule = new EncryptOutgoingEndpointRule();
        return configuration.CustomizeOutgoing(rule.Modify);
    }

    /// <summary>
    /// Force this local queue to encrypt outgoing messages.
    /// </summary>
    public static LocalQueueConfiguration Encrypted(this LocalQueueConfiguration configuration)
    {
        var rule = new EncryptOutgoingEndpointRule();
        return configuration.CustomizeOutgoing(rule.Modify);
    }
}

/// <summary>
/// Per-endpoint outgoing rule that swaps each envelope to the encrypting serializer.
/// Caches the resolved serializer on first invocation to avoid a dictionary lookup
/// on every send.
/// </summary>
internal sealed class EncryptOutgoingEndpointRule : IEnvelopeRule
{
    private IMessageSerializer? _encryptingSerializer;

    public void Modify(Envelope envelope)
    {
        if (envelope.Sender is null)
        {
            throw new InvalidOperationException(
                "Envelope has not been routed; .Encrypted() rules must run after sender resolution.");
        }

        var s = _encryptingSerializer ??= envelope.Sender.Endpoint.Runtime?.Options
            .TryFindSerializer(EncryptionHeaders.EncryptedContentType)
            ?? throw new InvalidOperationException(
                "No encrypting serializer is registered. Call " +
                "WolverineOptions.UseEncryption(provider) or " +
                "WolverineOptions.RegisterEncryptionSerializer(provider) " +
                "before configuring an endpoint with .Encrypted().");

        // Mirror MessageRoute.cs:114-115 — swap Serializer and ContentType together.
        envelope.Serializer = s;
        envelope.ContentType = s.ContentType;
    }

    public override string ToString() =>
        $"Encrypt outgoing envelopes via {EncryptionHeaders.EncryptedContentType}";
}
