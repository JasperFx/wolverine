using Grpc.Core;
using Shouldly;
using Wolverine.Grpc.Tests.GrpcMiddlewareScoping.Generated;
using Xunit;

namespace Wolverine.Grpc.Tests.GrpcMiddlewareScoping;

/// <summary>
///     Sanity-checks for the M15 test harness. These run BEFORE the production weaving lands
///     (Phase 1) so the fixture itself is known-good — when P1 tests start asserting middleware
///     invocations, any failure narrows to "P1 weaving" rather than "harness was never working."
/// </summary>
public class middleware_scoping_fixture_smoke_tests : IClassFixture<MiddlewareScopingFixture>
{
    private readonly MiddlewareScopingFixture _fixture;

    public middleware_scoping_fixture_smoke_tests(MiddlewareScopingFixture fixture)
    {
        _fixture = fixture;
        _fixture.Sink.Clear();
    }

    [Fact]
    public async Task unary_rpc_round_trips_through_wolverine_handler()
    {
        var client = _fixture.CreateClient();

        var reply = await client.GreetAsync(new GreetRequest { Name = "Erik" });

        reply.Message.ShouldBe("Hello, Erik");
        _fixture.Sink.Contains(GreetMessageHandler.Marker).ShouldBeTrue(
            "the Wolverine handler must run for the unary RPC");
    }

    [Fact]
    public async Task server_streaming_rpc_round_trips_through_wolverine_streaming_handler()
    {
        var client = _fixture.CreateClient();

        using var call = client.GreetMany(new GreetManyRequest { Name = "Erik" });

        var replies = new List<string>();
        await foreach (var reply in call.ResponseStream.ReadAllAsync())
        {
            replies.Add(reply.Message);
        }

        replies.Count.ShouldBe(3);
        replies[0].ShouldBe("Hello #0, Erik");
        _fixture.Sink.Contains(GreetMessageHandler.Marker).ShouldBeTrue(
            "the Wolverine streaming handler must run for the server-streaming RPC");
    }
}
