using System.Text;
using System.Text.Json;
using Alba;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using Shouldly;
using Wolverine.Http.Transport;
using Wolverine.Runtime.Interop;
using Wolverine.Runtime.Serialization;
using Wolverine.Tracking;
using Wolverine.Util;
using Xunit;

namespace Wolverine.Http.Tests.Transport;

public class HttpTransportExecutorTests : IntegrationContext
{
    public HttpTransportExecutorTests(AppFixture fixture) : base(fixture)
    {
    }

    [Fact]
    public async Task execute_batch_returns_415_for_wrong_content_type()
    {
        var result = await Scenario(s =>
        {
            s.Post.ByteArray(new byte[] { 1, 2, 3 }).ToUrl("/_wolverine/batch/test")
                .ContentType("application/json");
            s.StatusCodeShouldBe(415);
        });
    }

    [Fact]
    public async Task execute_batch_returns_415_for_missing_content_type()
    {
        var result = await Scenario(s =>
        {
            s.Post.ByteArray(new byte[] { 1, 2, 3 }).ToUrl("/_wolverine/batch/test");
            s.StatusCodeShouldBe(415);
        });
    }

    [Fact]
    public async Task execute_batch_returns_500_for_invalid_envelope_data()
    {
        var result = await Scenario(s =>
        {
            s.Post.ByteArray(new byte[] { 0xFF, 0xFF, 0xFF }).ToUrl("/_wolverine/batch/test")
                .ContentType(HttpTransport.EnvelopeBatchContentType);
            s.StatusCodeShouldBe(500);
        });
    }

    [Fact]
    public async Task invoke_returns_415_for_wrong_content_type()
    {
        var result = await Scenario(s =>
        {
            s.Post.ByteArray(new byte[] { 1, 2, 3 }).ToUrl("/_wolverine/invoke")
                .ContentType("application/xml");
            s.StatusCodeShouldBe(415);
        });
    }

    [Fact]
    public async Task invoke_returns_415_for_missing_content_type()
    {
        var result = await Scenario(s =>
        {
            s.Post.ByteArray(new byte[] { 1, 2, 3 }).ToUrl("/_wolverine/invoke");
            s.StatusCodeShouldBe(415);
        });
    }

    [Fact]
    public async Task invoke_accepts_binary_envelope_content_type()
    {
        var serializer = new SystemTextJsonSerializer(new JsonSerializerOptions());
        var envelope = new Envelope(new HttpMessage1("test"))
        {
            Serializer = serializer,
            ContentType = serializer.ContentType
        };

        var data = EnvelopeSerializer.Serialize(envelope);

        var (tracked, result) = await TrackedHttpCall(s =>
        {
            s.Post.ByteArray(data).ToUrl("/_wolverine/invoke")
                .ContentType(HttpTransport.EnvelopeContentType);
            s.StatusCodeShouldBeOk();
        });

        tracked.Executed.SingleMessage<HttpMessage1>().Name.ShouldBe("test");
    }

    [Fact]
    public async Task invoke_accepts_cloud_events_content_type()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        var messageData = JsonSerializer.Serialize(new { name = "cloud-test" }, options);
        var cloudEvent = new
        {
            specversion = "1.0",
            type = typeof(HttpMessage1).ToMessageTypeName(),
            source = "test-source",
            id = Guid.NewGuid().ToString(),
            datacontenttype = "application/json",
            data = JsonSerializer.Deserialize<object>(messageData)
        };

        var json = JsonSerializer.Serialize(cloudEvent, options);

        var (tracked, result) = await TrackedHttpCall(s =>
        {
            s.Post.Text(json).ToUrl("/_wolverine/invoke")
                .ContentType("application/cloudevents+json");
            s.StatusCodeShouldBeOk();
        });

        tracked.Executed.SingleMessage<HttpMessage1>().Name.ShouldBe("cloud-test");
    }

    [Fact]
    public async Task invoke_returns_400_for_unknown_message_type()
    {
        var envelope = new Envelope
        {
            MessageType = "NonExistent.MessageType",
            Data = new byte[] { 1, 2, 3 },
            ContentType = "application/json"
        };

        var data = EnvelopeSerializer.Serialize(envelope);

        var result = await Scenario(s =>
        {
            s.Post.ByteArray(data).ToUrl("/_wolverine/invoke")
                .ContentType(HttpTransport.EnvelopeContentType);
            s.StatusCodeShouldBe(400);
        });
    }

    [Fact]
    public async Task invoke_with_response_returns_binary_envelope()
    {
        var serializer = Host.GetRuntime().Options.DefaultSerializer;
        var envelope = new Envelope(new WolverineWebApi.CustomRequest("response-test"))
        {
            Serializer = serializer,
            ReplyRequested = typeof(WolverineWebApi.CustomResponse).ToMessageTypeName(),
            ContentType = "application/json"
        };

        var data = EnvelopeSerializer.Serialize(envelope);

        var (tracked, result) = await TrackedHttpCall(s =>
        {
            s.Post.ByteArray(data).ToUrl("/_wolverine/invoke")
                .ContentType(HttpTransport.EnvelopeContentType);
            s.ContentTypeShouldBe(HttpTransport.EnvelopeContentType);
        });

        var responseData = await result.Context.Response.Body.ReadAllBytesAsync();
        var responseEnvelope = EnvelopeSerializer.Deserialize(responseData);
        
        responseEnvelope.Message = serializer.ReadFromData(typeof(WolverineWebApi.CustomResponse), responseEnvelope);
        responseEnvelope.Message.ShouldBeOfType<WolverineWebApi.CustomResponse>()
            .Name.ShouldBe("response-test");
    }

    [Fact]
    public async Task invoke_returns_400_for_invalid_reply_requested_type()
    {
        var serializer = Host.GetRuntime().Options.DefaultSerializer;
        var envelope = new Envelope(new HttpMessage1("test"))
        {
            Serializer = serializer,
            ReplyRequested = "NonExistent.ResponseType",
            ContentType = "application/json"
        };

        var data = EnvelopeSerializer.Serialize(envelope);

        var result = await Scenario(s =>
        {
            s.Post.ByteArray(data).ToUrl("/_wolverine/invoke")
                .ContentType(HttpTransport.EnvelopeContentType);
            s.StatusCodeShouldBe(400);
        });
    }

    [Fact]
    public async Task batch_with_multiple_queues_routes_to_correct_queue()
    {
        var serializer = new SystemTextJsonSerializer(new JsonSerializerOptions());
        var envelopes = new Envelope[]
        {
            new(new HttpMessage1("queue-test")) { Serializer = serializer }
        };

        var data = EnvelopeSerializer.Serialize(envelopes);

        var (tracked, result) = await TrackedHttpCall(s =>
        {
            s.Post.ByteArray(data).ToUrl("/_wolverine/batch/custom-queue")
                .ContentType(HttpTransport.EnvelopeBatchContentType);
            s.StatusCodeShouldBeOk();
        });

        tracked.Executed.SingleMessage<HttpMessage1>().Name.ShouldBe("queue-test");
    }
}

public record HttpMessage1(string Name);
public static class HttpMessage1Handler
{
    public static void Handle(HttpMessage1 message)
    {
        // Just receive it
    }
}

public record HttpMessage2(string Name);
public static class HttpMessage2Handler
{
    public static void Handle(HttpMessage2 message)
    {
        // Just receive it
    }
}

public record HttpMessage3(string Name);
public static class HttpMessage3Handler
{
    public static void Handle(HttpMessage3 message)
    {
        // Just receive it
    }
}

