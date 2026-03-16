using System.Text.Json;
using Wolverine.Runtime.Handlers;
using Wolverine.Runtime.Interop;
using Xunit;

namespace CoreTests.Runtime.Interop;

public class CloudEventsMapper_unknown_type_tests
{
    private readonly HandlerGraph _handlers;
    private readonly CloudEventsMapper _mapper;

    public CloudEventsMapper_unknown_type_tests()
    {
        _handlers = new HandlerGraph();
        _handlers.RegisterMessageType(typeof(ApproveOrder), "com.dapr.event.sent");

        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        _mapper = new CloudEventsMapper(_handlers, options);
    }

    [Fact]
    public void should_preserve_raw_type_on_envelope_for_unknown_type()
    {
        var json = """
        {
          "data": { "orderId": 1 },
          "id": "5929aaac-a5e2-4ca1-859c-edfe73f11565",
          "specversion": "1.0",
          "type": "some.unknown.event.v1",
          "source": "test"
        }
        """;

        var envelope = new Envelope();

        var ex = Should.Throw<UnknownMessageTypeNameException>(() => _mapper.MapIncoming(envelope, json));

        ex.Message.ShouldContain("some.unknown.event.v1");
        envelope.MessageType.ShouldBe("some.unknown.event.v1");
    }

    [Fact]
    public void should_not_set_MessageType_when_type_field_missing()
    {
        var json = """
        {
          "data": { "orderId": 1 },
          "id": "5929aaac-a5e2-4ca1-859c-edfe73f11565",
          "specversion": "1.0",
          "source": "test"
        }
        """;

        var envelope = new Envelope();
        _mapper.MapIncoming(envelope, json);

        envelope.MessageType.ShouldBeNull();
    }

    [Fact]
    public void should_overwrite_raw_type_with_resolved_type_on_success()
    {
        var json = """
        {
          "data": { "orderId": 1 },
          "id": "5929aaac-a5e2-4ca1-859c-edfe73f11565",
          "specversion": "1.0",
          "type": "com.dapr.event.sent",
          "source": "test"
        }
        """;

        var envelope = new Envelope();
        _mapper.MapIncoming(envelope, json);

        // Should be the resolved .NET type name, not the raw CloudEvent type
        envelope.MessageType.ShouldNotBe("com.dapr.event.sent");
        envelope.MessageType.ShouldNotBeNull();
        envelope.Message.ShouldBeOfType<ApproveOrder>();
    }
}
