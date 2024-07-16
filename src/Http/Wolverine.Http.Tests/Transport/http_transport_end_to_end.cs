using System.Text;
using System.Text.Json;
using Alba;
using JasperFx.Core;
using Shouldly;
using Wolverine.Http.Transport;
using Wolverine.Runtime.Serialization;
using Wolverine.Tracking;
using Wolverine.Util;
using WolverineWebApi;

namespace Wolverine.Http.Tests.Transport;

public class http_transport_end_to_end : IntegrationContext
{
    public http_transport_end_to_end(AppFixture fixture) : base(fixture)
    {
    }

    [Fact]
    public async Task publish_multiple_messages()
    {
        var serializer = new SystemTextJsonSerializer(new JsonSerializerOptions());
        var envelopes = new Envelope[]
        {
            new(new HttpMessage1("one")) { Serializer = serializer },
            new(new HttpMessage2("two")) { Serializer = serializer },
            new(new HttpMessage3("three")) { Serializer = serializer }
        };

        var data = EnvelopeSerializer.Serialize(envelopes);

        var (tracked, result) = await TrackedHttpCall(s =>
        {
            s.Post.ByteArray(data).ToUrl("/_wolverine/batch/one").ContentType(HttpTransport.EnvelopesContentType);
        });

        tracked.Executed.SingleMessage<HttpMessage1>().Name.ShouldBe("one");
        tracked.Executed.SingleMessage<HttpMessage2>().Name.ShouldBe("two");
        tracked.Executed.SingleMessage<HttpMessage3>().Name.ShouldBe("three");

        foreach (var envelope in tracked.Received.Envelopes())
        {
            envelope.Destination.ShouldBe("http://localhost/_wolverine/batch/one".ToUri());
        }
    }

    [Fact]
    public async Task invoke_one_with_no_response()
    {
        var serializer = new SystemTextJsonSerializer(new JsonSerializerOptions());
        Envelope envelope = new(new HttpMessage1("Mat Cauthon"))
        {
            Serializer = serializer,
            ContentType = serializer.ContentType
        };

        var data = EnvelopeSerializer.Serialize(envelope);
        
        var (tracked, result) = await TrackedHttpCall(s =>
        {
            s.Post.ByteArray(data).ToUrl("/_wolverine/invoke").ContentType(HttpTransport.EnvelopeContentType);
        });
        
        tracked.Executed.SingleMessage<HttpMessage1>().Name.ShouldBe("Mat Cauthon");
    }

    [Fact]
    public async Task invoke_one_with_expected_response()
    {
        var serializer = Host.GetRuntime().Options.DefaultSerializer;
        Envelope envelope = new(new CustomRequest("Perrin Aybara"))
        {
            Serializer = serializer,
            ReplyRequested = typeof(CustomResponse).ToMessageTypeName(),
            ContentType = "application/json"
        };
        
        var data = EnvelopeSerializer.Serialize(envelope);
        
        var (tracked, result) = await TrackedHttpCall(s =>
        {
            s.Post.ByteArray(data).ToUrl("/_wolverine/invoke").ContentType(HttpTransport.EnvelopeContentType);
            s.ContentTypeShouldBe(HttpTransport.EnvelopeContentType);
        });

        var resultData = await result.Context.Response.Body.ReadAllBytesAsync();
        
        var received = EnvelopeSerializer.Deserialize(resultData);

        received.Message = serializer.ReadFromData(typeof(CustomResponse), received);
        
        received.Message.ShouldBeOfType<CustomResponse>().Name.ShouldBe("Perrin Aybara");
    }
}