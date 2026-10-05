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

        // The batch endpoint returns as soon as the envelopes are handed to the local queue, so the
        // tracked session has to be told what to wait for or it completes with no activity at all
        // before the handler pipeline has picked anything up. GH-3714.
        var (tracked, result) = await TrackedHttpCall(s =>
        {
            s.Post.ByteArray(data).ToUrl("/_wolverine/batch/one").ContentType(HttpTransport.EnvelopeBatchContentType);
        }, t => t
            .WaitForExecutionOf<HttpMessage1>()
            .WaitForExecutionOf<HttpMessage2>()
            .WaitForExecutionOf<HttpMessage3>());

        tracked.Executed.SingleMessage<HttpMessage1>().Name.ShouldBe("one");
        tracked.Executed.SingleMessage<HttpMessage2>().Name.ShouldBe("two");
        tracked.Executed.SingleMessage<HttpMessage3>().Name.ShouldBe("three");

        foreach (var envelope in tracked.Received.Envelopes())
        {
            // GH-4807: the local queue that actually received the batch, not the request path that
            // delivered it. See received_batch_is_stamped_with_an_address_inbox_recovery_can_resolve.
            envelope.Destination.ShouldBe("local://one/".ToUri());
        }
    }

    [Fact]
    public async Task received_batch_is_stamped_with_an_address_inbox_recovery_can_resolve()
    {
        // GH-4807. Envelope.Destination is what the durable inbox persists as received_at, and inbox
        // recovery resolves an ownerless row (a replayed dead letter, or the rows of a node that died
        // mid-handler) by asking the endpoints for a listener circuit at that address. The batch endpoint
        // used to stamp a synthesized "http://localhost/_wolverine/batch/one", which nothing is ever
        // registered under -- the HTTP transport receives by push and registers no listener at all -- so
        // every such row was skipped on every durability pass, silently, and stayed Incoming forever.
        var serializer = new SystemTextJsonSerializer(new JsonSerializerOptions());
        var data = EnvelopeSerializer.Serialize(new Envelope[]
        {
            new(new HttpMessage1("Egwene")) { Serializer = serializer }
        });

        var (tracked, _) = await TrackedHttpCall(s =>
        {
            s.Post.ByteArray(data).ToUrl("/_wolverine/batch/one").ContentType(HttpTransport.EnvelopeBatchContentType);
        }, t => t.WaitForExecutionOf<HttpMessage1>());

        var destination = tracked.Received.Envelopes().Select(x => x.Destination).Distinct().Single();
        destination.ShouldBe("local://one/".ToUri());

        var endpoints = Host.GetRuntime().Endpoints;

        // The property that actually matters: recovery can find somewhere to send these messages back to
        var circuit = endpoints.FindListenerCircuit(destination!);
        circuit.ShouldNotBeNull();

        // ...and the address this used to stamp instead could not be resolved to anything
        endpoints.FindListeningAgent("http://localhost/_wolverine/batch/one".ToUri()).ShouldBeNull();
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