using System.Net.Http.Headers;
using System.Text.Json;
using Wolverine.Runtime.Interop;
using Wolverine.Runtime.Serialization;
using Wolverine.Transports;

namespace Wolverine.Http.Transport;

/// <summary>
/// Resolves the <see cref="HttpClient"/> the HTTP transport sends an envelope over, and the address it
/// sends to.
/// </summary>
/// <remarks>
/// <para>
/// The <c>uri</c> passed to each method is the endpoint's <see cref="HttpEndpoint.OutboundUri"/> — an
/// absolute destination URL. It doubles as an <see cref="IHttpClientFactory"/> client name so an
/// application can customise per-destination transport behaviour (a client certificate, an auth handler,
/// a proxy, or a different network address than the one the URI names) by registering a named client
/// keyed by that exact string.
/// </para>
/// <para>
/// That customisation is <b>optional</b>. <see cref="IHttpClientFactory.CreateClient"/> never fails on an
/// unknown name — it returns a default client whose <c>BaseAddress</c> is <c>null</c> — so before
/// GH-3690 an unregistered destination posted to a null address and threw
/// <c>"An invalid request URI was provided. Either the request URI must be an absolute URI or
/// BaseAddress must be set."</c>. That is the whole failure: the transport knew the absolute URL and
/// declined to use it. It bit hardest where a destination is only learned at runtime and therefore
/// cannot be pre-registered — a monitoring console sending commands back to a service whose control URL
/// it learns from that service's own registration.
/// </para>
/// <para>
/// Resolution order, most specific first: a named client registered for this destination, otherwise the
/// transport's own isolated client (<see cref="HttpTransport.HttpClientName"/>, registered by
/// <c>AddWolverineHttp()</c>) so transport traffic carries transport configuration rather than whatever
/// the application happened to put on its default client. The address is the resolved client's
/// <c>BaseAddress</c> when it has one — preserving the deliberate "named by one URL, posting to another"
/// pattern — and the absolute outbound URI otherwise.
/// </para>
/// </remarks>
internal static class HttpTransportClientResolution
{
    internal static HttpClient ClientFor(IHttpClientFactory clientFactory, string uri)
    {
        var client = clientFactory.CreateClient(uri);
        return client.BaseAddress is null
            ? clientFactory.CreateClient(HttpTransport.HttpClientName)
            : client;
    }

    internal static Uri AddressFor(HttpClient client, string uri) => client.BaseAddress ?? new Uri(uri);
}

public class WolverineHttpTransportClient(IHttpClientFactory clientFactory) : IWolverineHttpTransportClient
{
    public async Task SendBatchAsync(string uri, OutgoingMessageBatch batch)
    {
        var client = HttpTransportClientResolution.ClientFor(clientFactory, uri);
        var content = new ByteArrayContent(EnvelopeSerializer.Serialize(batch.Messages));
        content.Headers.ContentType = new MediaTypeHeaderValue(HttpTransport.EnvelopeBatchContentType);
        var response = await client.PostAsync(HttpTransportClientResolution.AddressFor(client, uri), content);
        // Throw on a non-success status so the durable sender treats it as a failure and requeues,
        // rather than acknowledging success and deleting the outgoing envelope (#3173).
        response.EnsureSuccessStatusCode();
    }

    public async Task SendAsync(string uri, Envelope envelope, JsonSerializerOptions? options = null)
    {
        var client = HttpTransportClientResolution.ClientFor(clientFactory, uri);
        var content = new ByteArrayContent(EnvelopeSerializer.Serialize(envelope));
        content.Headers.ContentType = new MediaTypeHeaderValue(HttpTransport.EnvelopeContentType);
        var response = await client.PostAsync(HttpTransportClientResolution.AddressFor(client, uri), content);
        response.EnsureSuccessStatusCode();
    }

    public async Task<InlineHttpReply> InvokeAsync(string uri, Envelope envelope, JsonSerializerOptions? options = null)
    {
        var client = HttpTransportClientResolution.ClientFor(clientFactory, uri);
        var content = new ByteArrayContent(EnvelopeSerializer.Serialize(envelope));
        content.Headers.ContentType = new MediaTypeHeaderValue(HttpTransport.EnvelopeContentType);
        var response = await client.PostAsync(HttpTransportClientResolution.AddressFor(client, uri), content);
        var body = await response.Content.ReadAsByteArrayAsync();
        return new InlineHttpReply((int)response.StatusCode, body);
    }
}

public class WolverineHttpTransportClientCloudEvents(IHttpClientFactory clientFactory) : IWolverineHttpTransportClient
{
    public async Task SendBatchAsync(string uri, OutgoingMessageBatch batch)
    {
        var client = HttpTransportClientResolution.ClientFor(clientFactory, uri);
        var content = new ByteArrayContent(EnvelopeSerializer.Serialize(batch.Messages));
        content.Headers.ContentType = new MediaTypeHeaderValue(HttpTransport.EnvelopeBatchContentType);
        var response = await client.PostAsync(HttpTransportClientResolution.AddressFor(client, uri), content);
        // Throw on a non-success status so the durable sender treats it as a failure and requeues
        // rather than deleting the outgoing envelope (#3173).
        response.EnsureSuccessStatusCode();
    }

    public async Task SendAsync(string uri, Envelope envelope, JsonSerializerOptions? options = null)
    {
        var client = HttpTransportClientResolution.ClientFor(clientFactory, uri);
        var ce = new CloudEventsEnvelope(envelope);
        var content = new StringContent(JsonSerializer.Serialize(ce, options));
        content.Headers.ContentType = new MediaTypeHeaderValue(HttpTransport.CloudEventsContentType);
        await client.PostAsync(HttpTransportClientResolution.AddressFor(client, uri), content);
    }

    public async Task<InlineHttpReply> InvokeAsync(string uri, Envelope envelope, JsonSerializerOptions? options = null)
    {
        // The reply is always a binary Wolverine envelope regardless of the request encoding, so the
        // inline request/reply send uses the binary envelope format both ways (see GH-2966).
        var client = HttpTransportClientResolution.ClientFor(clientFactory, uri);
        var content = new ByteArrayContent(EnvelopeSerializer.Serialize(envelope));
        content.Headers.ContentType = new MediaTypeHeaderValue(HttpTransport.EnvelopeContentType);
        var response = await client.PostAsync(HttpTransportClientResolution.AddressFor(client, uri), content);
        var body = await response.Content.ReadAsByteArrayAsync();
        return new InlineHttpReply((int)response.StatusCode, body);
    }
}
