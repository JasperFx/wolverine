using System.Text.Json;
using System.Text.Json.Nodes;
using Wolverine.Runtime.Handlers;
using Wolverine.Runtime.Interop;
using Xunit;

namespace CoreTests.Runtime.Interop;

// GH-5080: lower case Envelope.Headers ride along as CloudEvents extension attributes
public class CloudEvents_custom_headers
{
    private readonly HandlerGraph theHandlers = new();
    private readonly CloudEventsMapper theMapper;

    public CloudEvents_custom_headers()
    {
        theHandlers.RegisterMessageType(typeof(ApproveOrder), "com.dapr.event.sent");
        theMapper = new CloudEventsMapper(theHandlers,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
    }

    private static Envelope outgoing()
    {
        var envelope = new Envelope(new ApproveOrder(1))
        {
            MessageType = "com.dapr.event.sent",
            Source = "checkout",
            SentAt = DateTimeOffset.UtcNow
        };

        envelope.Headers["x-user"] = "alice";
        envelope.Headers["x-country"] = "ca";

        return envelope;
    }

    private JsonObject write(Envelope envelope) => JsonNode.Parse(theMapper.WriteToString(envelope))!.AsObject();

    [Fact]
    public void writes_lower_case_headers_as_extension_attributes()
    {
        var json = write(outgoing());

        json["x-user"]!.GetValue<string>().ShouldBe("alice");
        json["x-country"]!.GetValue<string>().ShouldBe("ca");
    }

    [Fact]
    public void writes_the_same_attributes_to_bytes()
    {
        var json = JsonNode.Parse(theMapper.WriteToBytes(outgoing()))!.AsObject();

        json["x-user"]!.GetValue<string>().ShouldBe("alice");
        json["x-country"]!.GetValue<string>().ShouldBe("ca");
    }

    [Fact]
    public void does_not_write_a_header_with_upper_case_letters()
    {
        var envelope = outgoing();
        envelope.Headers["X-Tenant"] = "t1";

        write(envelope).ContainsKey("X-Tenant").ShouldBeFalse();
    }

    [Fact]
    public void a_header_never_overwrites_a_cloud_events_attribute()
    {
        var envelope = outgoing();
        envelope.Headers["source"] = "spoofed";
        envelope.Headers["type"] = "spoofed";

        var json = write(envelope);

        json["source"]!.GetValue<string>().ShouldBe("checkout");
        json["type"]!.GetValue<string>().ShouldBe("com.dapr.event.sent");
    }

    [Fact]
    public void does_not_write_wolverine_envelope_header_keys()
    {
        // What a transport's native mapper copies into Headers on the way in, so a forwarded
        // envelope must not echo them into the CloudEvents document
        var envelope = outgoing();
        envelope.Headers[EnvelopeConstants.CorrelationIdKey] = "abc";
        envelope.Headers[EnvelopeConstants.MessageTypeKey] = "something";
        envelope.Headers[EnvelopeConstants.SentAtKey] = "2026-10-18";

        var json = write(envelope);

        json.ContainsKey(EnvelopeConstants.CorrelationIdKey).ShouldBeFalse();
        json.ContainsKey(EnvelopeConstants.MessageTypeKey).ShouldBeFalse();
        json.ContainsKey(EnvelopeConstants.SentAtKey).ShouldBeFalse();
    }

    [Fact]
    public void no_headers_means_no_extension_attributes()
    {
        var envelope = new Envelope(new ApproveOrder(1)) { MessageType = "com.dapr.event.sent" };

        write(envelope).Select(x => x.Key).ShouldBe([
            "topic", "tenantid", "traceid", "tracestate", "data", "id", "specversion", "datacontenttype",
            "source", "type", "time", "traceparent"
        ], ignoreOrder: true);
    }

    [Fact]
    public void round_trips_the_headers()
    {
        var incoming = new Envelope();
        theMapper.MapIncoming(incoming, theMapper.WriteToString(outgoing()));

        incoming.Headers["x-user"].ShouldBe("alice");
        incoming.Headers["x-country"].ShouldBe("ca");
        incoming.Message.ShouldBeOfType<ApproveOrder>().OrderId.ShouldBe(1);
    }

    [Fact]
    public void reads_extension_attributes_from_any_cloud_events_producer()
    {
        var json = """
                   {
                     "specversion": "1.0",
                     "id": "5929aaac-a5e2-4ca1-859c-edfe73f11565",
                     "source": "checkout",
                     "type": "com.dapr.event.sent",
                     "data": { "orderId": 1 },
                     "x-user": "alice",
                     "x-country": "ca",
                     "priority": 3,
                     "urgent": true,
                     "Region": "west",
                     "nested": { "a": 1 }
                   }
                   """;

        var envelope = new Envelope();
        theMapper.MapIncoming(envelope, json);

        envelope.Headers["x-user"].ShouldBe("alice");
        envelope.Headers["x-country"].ShouldBe("ca");
        envelope.Headers["priority"].ShouldBe("3");
        envelope.Headers["urgent"].ShouldBe("true");

        envelope.Headers.ContainsKey("Region").ShouldBeFalse();
        envelope.Headers.ContainsKey("nested").ShouldBeFalse();
        envelope.Headers.ContainsKey("source").ShouldBeFalse();
        envelope.Headers.ContainsKey("data").ShouldBeFalse();
    }

    [Fact]
    public void a_header_the_transport_already_mapped_wins()
    {
        var envelope = new Envelope();
        envelope.Headers["x-user"] = "from-the-transport";

        theMapper.MapIncoming(envelope, theMapper.WriteToString(outgoing()));

        envelope.Headers["x-user"].ShouldBe("from-the-transport");
        envelope.Headers["x-country"].ShouldBe("ca");
    }

    [Fact]
    public void reads_headers_from_a_cloud_event_wrapped_by_sns()
    {
        var inner = theMapper.WriteToString(outgoing());
        var sns = new JsonObject { ["Type"] = "Notification", ["Message"] = inner }.ToJsonString();

        var envelope = new Envelope();
        theMapper.MapIncoming(envelope, sns);

        envelope.Headers["x-user"].ShouldBe("alice");
        envelope.Headers["x-country"].ShouldBe("ca");
        envelope.Headers.ContainsKey("Type").ShouldBeFalse();
    }
}
