using System.Net.Http.Headers;
using Wolverine.Runtime.Serialization;
using Wolverine.Transports;

namespace Wolverine.Http.Transport;

internal class WolverineHttpTransportClient : HttpClient
{
    public async Task SendBatchAsync(string uri, OutgoingMessageBatch batch)
    {
        var content = new ByteArrayContent(EnvelopeSerializer.Serialize(batch.Messages));
        content.Headers.ContentType = new MediaTypeHeaderValue(HttpTransportExecutor.EnvelopeBatchContentType);
        await PostAsync(uri, content);
    }
}