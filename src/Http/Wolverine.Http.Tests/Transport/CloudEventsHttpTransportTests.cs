using System.Net;
using System.Text;
using System.Text.Json;
using NSubstitute;
using Shouldly;
using Wolverine.Http.Transport;
using Wolverine.Runtime.Interop;
using Xunit;

namespace Wolverine.Http.Tests.Transport;

public class CloudEventsHttpTransportTests
{
    private readonly IHttpClientFactory _clientFactory;
    private readonly MockHttpMessageHandler _handler;
    private readonly HttpClient _httpClient;
    private readonly WolverineHttpTransportClientCloudEvents _client;

    public CloudEventsHttpTransportTests()
    {
        _clientFactory = Substitute.For<IHttpClientFactory>();
        _handler = new MockHttpMessageHandler();
        _httpClient = new HttpClient(_handler);
        _client = new WolverineHttpTransportClientCloudEvents(_clientFactory);
    }

    [Fact]
    public async Task send_envelope_as_cloud_event()
    {
        var uri = "https://localhost:5001/cloudevents";
        _httpClient.BaseAddress = new Uri(uri);
        _clientFactory.CreateClient(uri).Returns(_httpClient);

        var message = new TestMessage { Name = "test" };
        var envelope = new Envelope
        {
            Id = Guid.NewGuid(),
            TenantId =  "greattenantId",
            MessageType = "TestMessage",
            Message = message,
            Data = Encoding.UTF8.GetBytes("{\"name\":\"test\"}"),
            ContentType = "application/json"
        };

        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        await _client.SendAsync(uri, envelope, options);

        _handler.LastRequest.ShouldNotBeNull();
        _handler.LastRequest.Method.ShouldBe(HttpMethod.Post);
        _handler.LastRequest.Content.Headers.ContentType.MediaType.ShouldBe(HttpTransport.CloudEventsContentType);

        // Verify the body contains a CloudEvents formatted JSON
        var content = Encoding.UTF8.GetString(_handler.LastContent);
        content.ShouldContain("specversion");
        content.ShouldContain("type");
        content.ShouldContain("source");
        content.ShouldContain("id");
        content.ShouldContain("tenantid");
    }

    [Fact]
    public async Task cloud_events_envelope_contains_correct_properties()
    {
        var uri = "https://localhost:5001/cloudevents";
        _httpClient.BaseAddress = new Uri(uri);
        _clientFactory.CreateClient(uri).Returns(_httpClient);

        var envelopeId = Guid.NewGuid();
        var message = new CloudEventsTestCommand { Value = 42 };
        var envelope = new Envelope
        {
            Id = envelopeId,
            TenantId = "greattenantId",
            MessageType = "MyApp.TestCommand",
            Message = message,
            Data = Encoding.UTF8.GetBytes("{\"value\":42}"),
            ContentType = "application/json",
            Source = "test-service"
        };

        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        await _client.SendAsync(uri, envelope, options);

        var content = Encoding.UTF8.GetString(_handler.LastContent);
        var ce = JsonSerializer.Deserialize<CloudEventsEnvelope>(content, options);

        ce.ShouldNotBeNull();
        ce.SpecVersion.ShouldBe("1.0");
        ce.Type.ShouldBe("Wolverine.Http.Tests.Transport.CloudEventsTestCommand");
        ce.Source.ShouldBe("test-service");
        ce.Id.ToString().ShouldBe(envelopeId.ToString());
        ce.TenantId.ShouldBe("greattenantId");
        ce.DataContentType.ShouldStartWith("application/json");
    }

    [Fact]
    public async Task send_batch_with_cloud_events_uses_envelope_batch_content_type()
    {
        var uri = "https://localhost:5001/batch";
        _httpClient.BaseAddress = new Uri("https://target");
        _clientFactory.CreateClient(uri).Returns(_httpClient);

        var envelopes = new[]
        {
            new Envelope { Data = new byte[] { 1 } },
            new Envelope { Data = new byte[] { 2 } }
        };

        var batch = new Wolverine.Transports.OutgoingMessageBatch(new Uri(uri), envelopes);

        await _client.SendBatchAsync(uri, batch);

        _handler.LastRequest.ShouldNotBeNull();
        // Note: CloudEvents client uses EnvelopeBatchContentType for batches
        _handler.LastRequest.Content.Headers.ContentType.MediaType.ShouldBe(HttpTransport.EnvelopeBatchContentType);
    }

    [Fact]
    public async Task cloud_events_with_null_options_uses_default()
    {
        var uri = "https://localhost:5001/cloudevents";
        _httpClient.BaseAddress = new Uri(uri);
        _clientFactory.CreateClient(uri).Returns(_httpClient);

        var message = new TestMessage { Name = "test" };
        var envelope = new Envelope
        {
            Id = Guid.NewGuid(),
            MessageType = "TestMessage",
            Message = message,
            Data = Encoding.UTF8.GetBytes("{\"name\":\"test\"}")
        };

        // Should not throw with null options
        await _client.SendAsync(uri, envelope, null);

        _handler.LastRequest.ShouldNotBeNull();
        _handler.LastRequest.Content.Headers.ContentType.MediaType.ShouldBe(HttpTransport.CloudEventsContentType);
    }
}

public class TestMessage
{
    public string Name { get; set; }
}

public class CloudEventsTestCommand
{
    public int Value { get; set; }
}

