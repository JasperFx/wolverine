using JasperFx.Core;
using Microsoft.AspNetCore.Http;
using Wolverine.Runtime.Serialization;
using Wolverine.Util;

namespace Wolverine.Http.Transport.Push;

/// <summary>
///     GH-5016. Reads a request body that is a whole Wolverine envelope in its binary
///     <see cref="HttpTransport.EnvelopeContentType" /> form, as Wolverine's own HTTP transport sends it. A request
///     with any other content type, a body that is not a valid envelope, or an envelope of a message type that is not
///     on the allowed list is not a message
/// </summary>
/// <remarks>
///     GH-5037. The caller writes the whole envelope, so only the message types given to the constructor are
///     accepted, and the reply, acknowledgement and destination headers it carries are dropped rather than trusted
/// </remarks>
public class WolverineEnvelopePushMapper : IPushEnvelopeMapper
{
    private readonly HashSet<string> _allowedMessageTypes;

    /// <param name="allowedMessageTypes">The message types this endpoint may receive. At least one is required</param>
    public WolverineEnvelopePushMapper(params Type[] allowedMessageTypes)
    {
        ArgumentNullException.ThrowIfNull(allowedMessageTypes);
        if (allowedMessageTypes.Length == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(allowedMessageTypes),
                "Name the message types this push receiver may receive");
        }

        AllowedMessageTypes = allowedMessageTypes;
        _allowedMessageTypes = allowedMessageTypes.Select(x => x.ToMessageTypeName()).ToHashSet();
    }

    /// <summary>
    ///     The message types this mapper accepts
    /// </summary>
    public IReadOnlyList<Type> AllowedMessageTypes { get; }

    public async ValueTask<Envelope?> MapAsync(HttpContext context, CancellationToken cancellation)
    {
        if (context.Request.ContentType != HttpTransport.EnvelopeContentType) return null;

        var data = await context.Request.Body.ReadAllBytesAsync();

        Envelope envelope;
        try
        {
            envelope = EnvelopeSerializer.Deserialize(data);
        }
        catch (Exception)
        {
            // InvalidEnvelopeException for most damage, but a truncated body can fail in other ways too
            return null;
        }

        if (envelope.MessageType == null || !_allowedMessageTypes.Contains(envelope.MessageType)) return null;

        // Nothing the caller says about where replies, acks or the message itself should go is trusted
        envelope.ReplyUri = null;
        envelope.ReplyRequested = null;
        envelope.AckRequested = false;
        envelope.Destination = null;

        return envelope;
    }
}
