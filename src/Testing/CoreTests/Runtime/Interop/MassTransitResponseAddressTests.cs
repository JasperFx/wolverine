using NSubstitute;
using Wolverine.Runtime.Interop.MassTransit;
using Xunit;

namespace CoreTests.Runtime.Interop;

public class MassTransitResponseAddressTests
{
    [Fact]
    public void reads_a_message_from_a_sender_without_a_reply_endpoint()
    {
        // A sender with no listening endpoint has no reply Uri, so it writes an empty response address
        var endpoint = Substitute.For<IMassTransitInteropEndpoint>();
        endpoint.MassTransitReplyUri().Returns((Uri?)null);
        var serializer = new MassTransitJsonSerializer(endpoint);

        var data = serializer.Write(new Envelope { Id = Guid.NewGuid(), Message = new Ping("hello") });
        var incoming = new Envelope { Data = data };
        var message = serializer.ReadFromData(typeof(Ping), incoming);

        message.ShouldBeOfType<Ping>().Text.ShouldBe("hello");
        incoming.ReplyUri.ShouldBeNull();
    }

    public record Ping(string Text);
}
