using System.Text;
using System.Text.Json;
using Shouldly;
using Wolverine.Runtime.Handlers;
using Wolverine.Runtime.Interop;
using Xunit;

namespace CoreTests.Runtime.Interop;

public class CloudEventsMapper_non_guid_id_tests
{
    private const string Json = """
        { "data": { "orderId": 1 }, "id": "90020260911171709093778000000000000001223372037067692204",
          "specversion": "1.0", "type": "some.event", "source": "/repro" }
        """;

    private readonly CloudEventsMapper _mapper;

    public CloudEventsMapper_non_guid_id_tests()
    {
        var handlers = new HandlerGraph();
        handlers.RegisterMessageType(typeof(Order), "some.event");
        _mapper = new CloudEventsMapper(handlers, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
    }

    public record Order(int OrderId);

    [Fact]
    public void non_guid_id_maps_to_a_stable_envelope_id()
    {
        var one = new Envelope { Data = Encoding.UTF8.GetBytes(Json) };
        var two = new Envelope { Data = Encoding.UTF8.GetBytes(Json) };
        _mapper.Unwrap(one);
        _mapper.Unwrap(two);

        one.Id.ShouldNotBe(Guid.Empty);
        one.Id.ShouldBe(two.Id);
    }

    [Fact]
    public void reading_a_loaded_envelope_keeps_its_id()
    {
        var id = Guid.NewGuid();
        var envelope = new Envelope { Id = id, Data = Encoding.UTF8.GetBytes(Json) };
        _mapper.ReadFromData(typeof(Order), envelope);

        envelope.Id.ShouldBe(id);
    }
}
