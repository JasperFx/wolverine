using Grpc.Core;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Wolverine.Grpc.Tests.GrpcClientStreaming.Generated;
using Xunit;

namespace Wolverine.Grpc.Tests.GrpcClientStreaming;

/// <summary>
///     End-to-end tests for the proto-first client-streaming generated wrapper. Verifies that
///     the generated code adapts the inbound <see cref="NumberRequest"/> stream to
///     <c>IAsyncEnumerable&lt;NumberRequest&gt;</c>, forwards it to
///     <c>IMessageBus.StreamAsync</c>, and returns the handler's single <see cref="SumReply"/>.
/// </summary>
public class grpc_client_streaming_tests : IClassFixture<ClientStreamingFixture>
{
    private readonly ClientStreamingFixture _fixture;

    public grpc_client_streaming_tests(ClientStreamingFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task streamed_requests_fold_into_a_single_reply()
    {
        using var call = _fixture.CreateClient().Collect();

        await call.RequestStream.WriteAsync(new NumberRequest { Value = 1 });
        await call.RequestStream.WriteAsync(new NumberRequest { Value = 2 });
        await call.RequestStream.WriteAsync(new NumberRequest { Value = 3 });
        await call.RequestStream.CompleteAsync();

        var reply = await call;

        reply.Total.ShouldBe(6);
        reply.Count.ShouldBe(3);
    }

    [Fact]
    public async Task zero_requests_still_produce_a_reply()
    {
        using var call = _fixture.CreateClient().Collect();
        await call.RequestStream.CompleteAsync();

        var reply = await call;

        reply.Total.ShouldBe(0);
        reply.Count.ShouldBe(0);
    }

    [Fact]
    public async Task cancelling_the_call_aborts_with_cancelled_status()
    {
        using var cts = new CancellationTokenSource();
        using var call = _fixture.CreateClient().Collect(cancellationToken: cts.Token);

        await call.RequestStream.WriteAsync(new NumberRequest { Value = 1 });
        await cts.CancelAsync();

        var ex = await Should.ThrowAsync<RpcException>(async () => await call);
        ex.StatusCode.ShouldBe(StatusCode.Cancelled);
    }

    [Fact]
    public void client_streaming_method_is_classified_on_the_chain()
    {
        var graph = _fixture.Services.GetRequiredService<GrpcGraph>();
        var chain = graph.Chains.Single(c => c.StubType == typeof(CollectStub));

        chain.ClientStreamingMethods.Count.ShouldBe(1);
        chain.ClientStreamingMethods[0].Name.ShouldBe("Collect");
        chain.UnaryMethods.ShouldBeEmpty();
        chain.ServerStreamingMethods.ShouldBeEmpty();
        chain.BidirectionalStreamingMethods.ShouldBeEmpty();
    }
}
