using JasperFx.Core;
using Microsoft.AspNetCore.Http;
using Wolverine.Runtime.Serialization;

namespace Wolverine.Http.Transport.Push;

/// <summary>
///     GH-5016. Reads a request body that is a whole Wolverine envelope in its binary
///     <see cref="HttpTransport.EnvelopeContentType" /> form, as Wolverine's own HTTP transport sends it. A request
///     with any other content type, or a body that is not a valid envelope, is not a message
/// </summary>
public class WolverineEnvelopePushMapper : IPushEnvelopeMapper
{
    public async ValueTask<Envelope?> MapAsync(HttpContext context, CancellationToken cancellation)
    {
        if (context.Request.ContentType != HttpTransport.EnvelopeContentType) return null;

        var data = await context.Request.Body.ReadAllBytesAsync();
        try
        {
            return EnvelopeSerializer.Deserialize(data);
        }
        catch (Exception)
        {
            // InvalidEnvelopeException for most damage, but a truncated body can fail in other ways too
            return null;
        }
    }
}
