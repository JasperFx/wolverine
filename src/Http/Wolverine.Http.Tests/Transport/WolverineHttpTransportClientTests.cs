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

public class WolverineHttpTransportClientTests
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
        _handler.LastRequest.RequestUri.ToString().ShouldBe(uri);
        _handler.LastRequest.Content.Headers.ContentType.MediaType.ShouldBe(HttpTransport.EnvelopeContentType);

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
        _handler.LastRequest.RequestUri.ToString().ShouldBe("https://target-url/");
        _handler.LastRequest.Content.Headers.ContentType.MediaType.ShouldBe(HttpTransport.EnvelopeBatchContentType);

        var expectedData = EnvelopeSerializer.Serialize(envelopes);
        _handler.LastContent.ShouldBe(expectedData);
    }
}

public class MockHttpMessageHandler : HttpMessageHandler
{
    public HttpRequestMessage LastRequest { get; private set; }
    public byte[] LastContent { get; private set; }

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
