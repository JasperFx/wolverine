using System.Net;
using System.Net.Http.Headers;
using JasperFx.Core;
using NSubstitute;
using Shouldly;
using Wolverine.Http.Transport;
using Wolverine.Runtime.Serialization;
using Wolverine.Transports;
using Xunit;

namespace Wolverine.Http.Tests.Transport;

public class WolverineHttpTransportClientTests : IDisposable
{
    private readonly IHttpClientFactory _clientFactory;
    private readonly MockHttpMessageHandler _handler;
    private readonly HttpClient _httpClient;
    private readonly WolverineHttpTransportClient _client;

    public WolverineHttpTransportClientTests()
    {
        _clientFactory = Substitute.For<IHttpClientFactory>();
        _handler = new MockHttpMessageHandler();
        _httpClient = new HttpClient(_handler);
        _client = new WolverineHttpTransportClient(_clientFactory);
    }

    [Fact]
    public async Task send_envelope_async()
    {
        var uri = "https://localhost:5001/messages";
        _clientFactory.CreateClient(uri).Returns(_httpClient);
        _httpClient.BaseAddress = new Uri("https://localhost:5001/messages");

        var envelope = new Envelope
        {
            Data = new byte[] { 1, 2, 3 },
            Destination = new Uri(uri)
        };

        await _client.SendAsync(uri, envelope);

        _handler.LastRequest.ShouldNotBeNull();
        _handler.LastRequest.Method.ShouldBe(HttpMethod.Post);
        _handler.LastRequest.RequestUri!.ToString().ShouldBe(uri);
        _handler.LastRequest.Content!.Headers.ContentType!.MediaType.ShouldBe(HttpTransport.EnvelopeContentType);

        var expectedData = EnvelopeSerializer.Serialize(envelope);
        _handler.LastContent.ShouldBe(expectedData);
    }

    [Fact]
    public async Task send_batch_async()
    {
        var uri = "https://localhost:5001/messages/batch";
        _httpClient.BaseAddress = new Uri("https://target-url");
        _clientFactory.CreateClient(uri).Returns(_httpClient);
        
        var envelopes = new[]
        {
            new Envelope { Data = new byte[] { 1 } },
            new Envelope { Data = new byte[] { 2 } }
        };

        var batch = new OutgoingMessageBatch(new Uri(uri), envelopes);

        await _client.SendBatchAsync(uri, batch);

        _handler.LastRequest.ShouldNotBeNull();
        _handler.LastRequest.Method.ShouldBe(HttpMethod.Post);
        _handler.LastRequest.RequestUri!.ToString().ShouldBe("https://target-url/");
        _handler.LastRequest.Content!.Headers.ContentType!.MediaType.ShouldBe(HttpTransport.EnvelopeBatchContentType);

        var expectedData = EnvelopeSerializer.Serialize(envelopes);
        _handler.LastContent.ShouldBe(expectedData);
    }

    /// <summary>
    /// GH-3690. The destination is only knowable at runtime in plenty of topologies — a monitoring
    /// console sending commands back to a service whose control URL it learned from that service's own
    /// registration cannot pre-register a named client for it. CreateClient hands back a default client
    /// with a null BaseAddress for an unknown name, which used to post to null and throw
    /// "An invalid request URI was provided…". The outbound URI is absolute; send to it.
    /// </summary>
    [Fact]
    public async Task posts_to_the_outbound_uri_when_no_named_client_is_registered()
    {
        var uri = "https://localhost:7045/_wolverine/invoke";

        // Both lookups miss, exactly as they do for an unconfigured destination: an HttpClient with no
        // BaseAddress. The transport must still find the address, because the uri IS the address.
        _clientFactory.CreateClient(uri).Returns(new HttpClient(_handler));
        _clientFactory.CreateClient(HttpTransport.HttpClientName).Returns(_httpClient);

        await _client.SendAsync(uri, new Envelope { Data = new byte[] { 1, 2, 3 }, Destination = new Uri(uri) });

        _handler.LastRequest.ShouldNotBeNull();
        _handler.LastRequest.RequestUri!.ToString().ShouldBe(uri);
    }

    /// <summary>
    /// With no per-destination client configured the send falls through to the transport's own named
    /// client, so a timeout / handler / proxy set once on <see cref="HttpTransport.HttpClientName"/>
    /// applies to every HTTP-transport send instead of the application's default HttpClient settings
    /// leaking in.
    /// </summary>
    [Fact]
    public async Task falls_back_to_the_transport_owned_named_client()
    {
        var uri = "https://localhost:7045/_wolverine/invoke";

        using var transportHandler = new MockHttpMessageHandler();
        using var transportClient = new HttpClient(transportHandler);

        _clientFactory.CreateClient(uri).Returns(new HttpClient(_handler));
        _clientFactory.CreateClient(HttpTransport.HttpClientName).Returns(transportClient);

        await _client.SendAsync(uri, new Envelope { Data = new byte[] { 9 }, Destination = new Uri(uri) });

        // The transport's client carried the send; the per-destination miss did not.
        transportHandler.LastRequest.ShouldNotBeNull();
        _handler.LastRequest.ShouldBeNull();
    }

    /// <summary>
    /// A named client registered for the destination still wins, including the deliberate "keyed by one
    /// URL, posting to another" pattern (an https key chosen so Wolverine resolves the HTTP transport,
    /// with a plain-http BaseAddress for the real local address). Covered for batches by
    /// <see cref="send_batch_async"/>; asserted here for the single-envelope path too, since GH-3690
    /// changed how the address is resolved on both.
    /// </summary>
    [Fact]
    public async Task a_registered_named_client_still_overrides_the_outbound_uri()
    {
        var uri = "https://localhost:7045/_wolverine/invoke";
        _httpClient.BaseAddress = new Uri("http://localhost:5291/_wolverine/invoke");
        _clientFactory.CreateClient(uri).Returns(_httpClient);

        await _client.SendAsync(uri, new Envelope { Data = new byte[] { 4 }, Destination = new Uri(uri) });

        _handler.LastRequest.ShouldNotBeNull();
        _handler.LastRequest.RequestUri!.ToString().ShouldBe("http://localhost:5291/_wolverine/invoke");
    }

    public void Dispose()
    {
        _httpClient.Dispose();
        _handler.Dispose();
    }
}

public class MockHttpMessageHandler : HttpMessageHandler
{
    public HttpRequestMessage? LastRequest { get; private set; }
    public byte[] LastContent { get; private set; } = null!;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        LastRequest = request;
        if (request.Content != null)
        {
            LastContent = await request.Content.ReadAsByteArrayAsync(cancellationToken);
        }
        return new HttpResponseMessage(HttpStatusCode.OK);
    }
}
