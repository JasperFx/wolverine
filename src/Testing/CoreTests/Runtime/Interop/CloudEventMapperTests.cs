using System.Text.Json;
using Wolverine.Runtime.Handlers;
using Wolverine.Runtime.Interop;
using Xunit;

namespace CoreTests.Runtime.Interop;

public class CloudEventMapperTests
{
    private readonly HandlerGraph theHandlers;
    private readonly Envelope theEnvelope;

    public static string Json = @"
{
  ""topic"": ""orders"",
  ""pubsubname"": ""order_pub_sub"",
  ""traceid"": ""00-113ad9c4e42b27583ae98ba698d54255-e3743e35ff56f219-01"",
  ""tracestate"": """",
  ""data"": {
    ""orderId"": 1
  },
  ""id"": ""5929aaac-a5e2-4ca1-859c-edfe73f11565"",
  ""specversion"": ""1.0"",
  ""datacontenttype"": ""application/json; charset=utf-8"",
  ""source"": ""checkout"",
  ""type"": ""com.dapr.event.sent"",
  ""time"": ""2020-09-23T06:23:21Z"",
  ""traceparent"": ""00-113ad9c4e42b27583ae98ba698d54255-e3743e35ff56f219-01""
}
";

    public CloudEventMapperTests()
    {
        theHandlers = new HandlerGraph();
        theHandlers.RegisterMessageType(typeof(ApproveOrder), "com.dapr.event.sent");

        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        
        theEnvelope = new Envelope();
        new CloudEventsMapper(theHandlers, options).MapIncoming(theEnvelope, Json);
    }

    [Fact]
    public void has_the_message_body()
    {
        theEnvelope.Message.ShouldBeOfType<ApproveOrder>().OrderId.ShouldBe(1);
    }

    [Fact]
    public void has_the_message_id()
    {
        theEnvelope.Id.ShouldBe(Guid.Parse("5929aaac-a5e2-4ca1-859c-edfe73f11565"));
    }

    [Fact]
    public void has_the_content_type_just_in_case()
    {
        theEnvelope.ContentType.ShouldBe("application/json");
    }

    [Fact]
    public void has_the_source()
    {
        theEnvelope.Source.ShouldBe("checkout");
    }

    [Fact]
    public void has_a_correlation_id()
    {
        theEnvelope.CorrelationId.ShouldBe("00-113ad9c4e42b27583ae98ba698d54255-e3743e35ff56f219-01");
    }

    [Fact]
    public void has_a_sent_time()
    {
        theEnvelope.SentAt.ShouldBe(DateTimeOffset.Parse("2020-09-23T06:23:21Z"));
    }
}

public record ApproveOrder(int OrderId);