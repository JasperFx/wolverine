using System.Text;
using System.Text.Json;
using Wolverine.Runtime.Handlers;
using Wolverine.Runtime.Interop;
using Wolverine.Util;
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
        _handlers.RegisterMessageType(typeof(MultiAliasApproveOrder), "data.updated.v1");
        _handlers.RegisterMessageType(typeof(MultiAliasApproveOrder), "data.updated.v2");

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

    [Fact]
    public void should_fall_back_to_original_message_type_when_cloudevent_type_is_unknown()
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

        var envelope = new Envelope
        {
            Data = Encoding.UTF8.GetBytes(json),
            MessageType = typeof(FallbackApproveOrder).ToMessageTypeName()
        };

        var message = _mapper.ReadFromData(typeof(FallbackApproveOrder), envelope);

        message.ShouldBeOfType<FallbackApproveOrder>().OrderId.ShouldBe(1);
        envelope.Message.ShouldBeSameAs(message);
        envelope.MessageType.ShouldBe(typeof(FallbackApproveOrder).ToMessageTypeName());
    }

    [Fact]
    public void should_resolve_second_registered_alias_for_same_message_type()
    {
        var json = """
        {
          "data": { "orderId": 2 },
          "id": "5929aaac-a5e2-4ca1-859c-edfe73f11565",
          "specversion": "1.0",
          "type": "data.updated.v2",
          "source": "test"
        }
        """;

        var envelope = new Envelope();
        _mapper.MapIncoming(envelope, json);

        envelope.Message.ShouldBeOfType<MultiAliasApproveOrder>().OrderId.ShouldBe(2);
        envelope.MessageType.ShouldNotBe("data.updated.v2");
    }
}

public record FallbackApproveOrder(int OrderId);
public record MultiAliasApproveOrder(int OrderId);
