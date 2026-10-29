using Shouldly;
using Wolverine.Configuration;
using Xunit;

namespace Wolverine.Grpc.Tests;

/// <summary>
/// GH-5278. A durable receiver that cannot write a received envelope to its inbox "defers" it back to the listener,
/// and leaves redelivery to the transport. GrpcListener.DeferAsync does nothing, and the transport service answers
/// the sender with <c>Ack { Success = true }</c> as soon as ReceivedAsync returns -- while GrpcSender treats any
/// reply that is not an RpcException as delivered and never reads Ack.Success. That combination would lose messages,
/// and the only thing keeping it from doing so is that a gRPC listener can never be durable, so its receiver never
/// defers anything. See the TCP transport's handling of the same case (InboxReceiptListener) before changing this.
/// </summary>
public class inbox_write_failure_5278
{
    [Fact]
    public void a_grpc_listener_cannot_be_made_durable()
    {
        var endpoint = new GrpcEndpoint(new Uri("grpc://localhost:5000"));

        endpoint.SupportsMode(EndpointMode.Durable).ShouldBeFalse(
            "A durable gRPC listener must refuse what its inbox could not store with an RpcException first (GH-5278)");
    }
}
