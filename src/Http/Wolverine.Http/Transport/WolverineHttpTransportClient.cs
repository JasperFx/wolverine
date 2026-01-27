using System.Net.Http.Headers;
using System.Text.Json;
using Wolverine.Runtime.Interop;
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
    
    public async Task SendAsync(string uri, Envelope envelope, JsonSerializerOptions options = null)
    {
        var client = clientFactory.CreateClient(uri);
        var content = new ByteArrayContent(EnvelopeSerializer.Serialize(envelope));
        content.Headers.ContentType = new MediaTypeHeaderValue(HttpTransport.EnvelopeContentType);
        await client.PostAsync(client.BaseAddress, content);
    }
}

public class WolverineHttpTransportClientCloudEvents(IHttpClientFactory clientFactory) : IWolverineHttpTransportClient
{
    public async Task SendBatchAsync(string uri, OutgoingMessageBatch batch)
    {
        var client = clientFactory.CreateClient(uri);
        var content = new ByteArrayContent(EnvelopeSerializer.Serialize(batch.Messages));
        content.Headers.ContentType = new MediaTypeHeaderValue(HttpTransport.EnvelopeBatchContentType);
        await client.PostAsync(client.BaseAddress, content);
    }
    
    public async Task SendAsync(string uri, Envelope envelope, JsonSerializerOptions options = null)
    {
        var client = clientFactory.CreateClient(uri);
        var ce = new CloudEventsEnvelope(envelope);
        var content = new StringContent(JsonSerializer.Serialize(ce, options));
        content.Headers.ContentType = new MediaTypeHeaderValue(HttpTransport.CloudEventsContentType);
        await client.PostAsync(client.BaseAddress, content);
    }
}