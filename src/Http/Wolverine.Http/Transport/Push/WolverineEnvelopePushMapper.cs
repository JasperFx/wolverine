using JasperFx.Core;
using Microsoft.AspNetCore.Http;
using Wolverine.Runtime.Serialization;
using Wolverine.Util;

namespace Wolverine.Http.Transport.Push;

/// <summary>
///     GH-5016. Reads a request body that is a whole Wolverine envelope in its binary
///     <see cref="HttpTransport.EnvelopeContentType" /> form, or a batch of them in the
///     <see cref="HttpTransport.EnvelopeBatchContentType" /> form, as Wolverine's own HTTP transport sends them. A
///     request with any other content type, a body that is not valid, or any envelope of a message type that is not
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

    public async ValueTask<IReadOnlyList<Envelope>> MapAsync(HttpContext context, CancellationToken cancellation)
    {
        var contentType = context.Request.ContentType;
        if (contentType != HttpTransport.EnvelopeContentType && contentType != HttpTransport.EnvelopeBatchContentType)
        {
            return [];
        }

        var data = await context.Request.Body.ReadAllBytesAsync();

        Envelope[] envelopes;
        try
        {
            envelopes = contentType == HttpTransport.EnvelopeBatchContentType
                ? EnvelopeSerializer.ReadMany(data)
                : [EnvelopeSerializer.Deserialize(data)];
        }
        catch (Exception)
        {
            // InvalidEnvelopeException for most damage, but a truncated body can fail in other ways too
            return [];
        }

        // One envelope the caller may not send rejects the whole request rather than handling part of it
        if (envelopes.Length == 0 ||
            envelopes.Any(x => x.MessageType == null || !_allowedMessageTypes.Contains(x.MessageType)))
        {
            return [];
        }

        foreach (var envelope in envelopes)
        {
            // Nothing the caller says about where replies, acks or the message itself should go is trusted
            envelope.ReplyUri = null;
            envelope.ReplyRequested = null;
            envelope.AckRequested = false;
            envelope.Destination = null;
        }

        return envelopes;
    }
}
