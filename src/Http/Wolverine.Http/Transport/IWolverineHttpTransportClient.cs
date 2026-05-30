using System.Text.Json;
using Wolverine.Transports;

namespace Wolverine.Http.Transport;

public interface IWolverineHttpTransportClient
{
    Task SendBatchAsync(string uri, OutgoingMessageBatch batch);
    Task SendAsync(string uri, Envelope envelope, JsonSerializerOptions serializerOptions);

    /// <summary>
    /// GH-2966 inline request/reply: POST a single request envelope and read the raw response
    /// (status + body) back. The body is read regardless of status so a 500 carrying an
    /// envelope-shaped error reply can be surfaced to the caller. Returns an empty body when the
    /// response has none.
    /// </summary>
    Task<InlineHttpReply> InvokeAsync(string uri, Envelope envelope, JsonSerializerOptions serializerOptions);
}

/// <summary>The raw HTTP response of an inline request/reply send (GH-2966).</summary>
public record InlineHttpReply(int StatusCode, byte[] Body);