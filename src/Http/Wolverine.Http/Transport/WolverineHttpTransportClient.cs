using System.Net.Http.Headers;
using Wolverine.Runtime.Serialization;
using Wolverine.Transports;

namespace Wolverine.Http.Transport;

public class WolverineHttpTransportClient(IHttpClientFactory clientFactory) : IWolverineHttpTransportClient
{
    public async Task SendBatchAsync(string uri, OutgoingMessageBatch batch)
    {
        var client = clientFactory.CreateClient(uri);
        var content = new ByteArrayContent(EnvelopeSerializer.Serialize(batch.Messages));
        content.Headers.ContentType = new MediaTypeHeaderValue(HttpTransport.EnvelopeBatchContentType);
        await client.PostAsync(client.BaseAddress, content);
    }
    
    public async Task SendAsync(string uri, Envelope envelope)
    {
        var client = clientFactory.CreateClient(uri);
        var content = new ByteArrayContent(EnvelopeSerializer.Serialize(envelope));
        content.Headers.ContentType = new MediaTypeHeaderValue(HttpTransport.EnvelopeContentType);
        await client.PostAsync(client.BaseAddress, content);
    }
}