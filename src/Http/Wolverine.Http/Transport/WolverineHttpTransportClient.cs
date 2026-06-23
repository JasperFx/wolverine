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
        var response = await client.PostAsync(client.BaseAddress, content);
        // Throw on a non-success status so the durable sender treats it as a failure and requeues,
        // rather than acknowledging success and deleting the outgoing envelope (#3173).
        response.EnsureSuccessStatusCode();
    }

    public async Task SendAsync(string uri, Envelope envelope, JsonSerializerOptions? options = null)
    {
        var client = clientFactory.CreateClient(uri);
        var content = new ByteArrayContent(EnvelopeSerializer.Serialize(envelope));
        content.Headers.ContentType = new MediaTypeHeaderValue(HttpTransport.EnvelopeContentType);
        var response = await client.PostAsync(client.BaseAddress, content);
        response.EnsureSuccessStatusCode();
    }

    public async Task<InlineHttpReply> InvokeAsync(string uri, Envelope envelope, JsonSerializerOptions? options = null)
    {
        var client = clientFactory.CreateClient(uri);
        var content = new ByteArrayContent(EnvelopeSerializer.Serialize(envelope));
        content.Headers.ContentType = new MediaTypeHeaderValue(HttpTransport.EnvelopeContentType);
        var response = await client.PostAsync(client.BaseAddress, content);
        var body = await response.Content.ReadAsByteArrayAsync();
        return new InlineHttpReply((int)response.StatusCode, body);
    }
}

public class WolverineHttpTransportClientCloudEvents(IHttpClientFactory clientFactory) : IWolverineHttpTransportClient
{
    public async Task SendBatchAsync(string uri, OutgoingMessageBatch batch)
    {
        var client = clientFactory.CreateClient(uri);
        var content = new ByteArrayContent(EnvelopeSerializer.Serialize(batch.Messages));
        content.Headers.ContentType = new MediaTypeHeaderValue(HttpTransport.EnvelopeBatchContentType);
        var response = await client.PostAsync(client.BaseAddress, content);
        // Throw on a non-success status so the durable sender treats it as a failure and requeues
        // rather than deleting the outgoing envelope (#3173).
        response.EnsureSuccessStatusCode();
    }

    public async Task SendAsync(string uri, Envelope envelope, JsonSerializerOptions? options = null)
    {
        var client = clientFactory.CreateClient(uri);
        var ce = new CloudEventsEnvelope(envelope);
        var content = new StringContent(JsonSerializer.Serialize(ce, options));
        content.Headers.ContentType = new MediaTypeHeaderValue(HttpTransport.CloudEventsContentType);
        await client.PostAsync(client.BaseAddress, content);
    }

    public async Task<InlineHttpReply> InvokeAsync(string uri, Envelope envelope, JsonSerializerOptions? options = null)
    {
        // The reply is always a binary Wolverine envelope regardless of the request encoding, so the
        // inline request/reply send uses the binary envelope format both ways (see GH-2966).
        var client = clientFactory.CreateClient(uri);
        var content = new ByteArrayContent(EnvelopeSerializer.Serialize(envelope));
        content.Headers.ContentType = new MediaTypeHeaderValue(HttpTransport.EnvelopeContentType);
        var response = await client.PostAsync(client.BaseAddress, content);
        var body = await response.Content.ReadAsByteArrayAsync();
        return new InlineHttpReply((int)response.StatusCode, body);
    }
}