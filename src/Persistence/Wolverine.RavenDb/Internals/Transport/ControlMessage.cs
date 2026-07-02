namespace Wolverine.RavenDb.Internals.Transport;

/// <summary>
/// RavenDB document representing a single Wolverine control message targeted at a
/// specific node. Stored in the ControlMessages collection and polled by the
/// receiving node's <see cref="RavenDbControlListener"/>.
/// </summary>
internal class ControlMessage
{
    public ControlMessage()
    {
    }

    public ControlMessage(Envelope envelope, Guid nodeId, byte[] body, DateTimeOffset expires)
    {
        Id = IdFor(envelope.Id);
        NodeId = nodeId;
        MessageType = envelope.MessageType!;
        Body = body;
        Expires = expires;
        Posted = DateTimeOffset.UtcNow;
    }

    public string Id { get; set; } = string.Empty;
    public Guid NodeId { get; set; }
    public string MessageType { get; set; } = string.Empty;
    public byte[] Body { get; set; } = [];
    public DateTimeOffset Expires { get; set; }
    public DateTimeOffset Posted { get; set; }

    public static string IdFor(Guid envelopeId) => $"ControlMessages/{envelopeId}";
}
