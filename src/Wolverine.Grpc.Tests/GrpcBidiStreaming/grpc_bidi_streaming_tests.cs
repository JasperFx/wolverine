using Grpc.Core;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Wolverine.Grpc.Tests.GrpcBidiStreaming.Generated;
using Xunit;

namespace Wolverine.Grpc.Tests.GrpcBidiStreaming;

/// <summary>
///     End-to-end tests for the proto-first bidirectional-streaming generated wrapper.
///     Verifies that the generated code correctly loops each inbound <see cref="EchoRequest"/>
///     through <c>IMessageBus.StreamAsync</c> and writes all yielded <see cref="EchoReply"/>
///     messages back to the client.
/// </summary>
public class grpc_bidi_streaming_tests : IClassFixture<BidiStreamingFixture>
{
    private readonly BidiStreamingFixture _fixture;

    public grpc_bidi_streaming_tests(BidiStreamingFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task single_request_yields_the_expected_number_of_replies()
    {
        using var call = _fixture.CreateClient().Echo();
        await call.RequestStream.WriteAsync(new EchoRequest { Text = "ping", RepeatCount = 3 });
        await call.RequestStream.CompleteAsync();

        var replies = new List<string>();
        await foreach (var reply in call.ResponseStream.ReadAllAsync())
            replies.Add(reply.Text);

        replies.ShouldBe(["ping", "ping", "ping"]);
    }

    [Fact]
    public async Task multiple_requests_each_produce_their_own_replies()
    {
        using var call = _fixture.CreateClient().Echo();

        await call.RequestStream.WriteAsync(new EchoRequest { Text = "a", RepeatCount = 2 });
        await call.RequestStream.WriteAsync(new EchoRequest { Text = "b", RepeatCount = 1 });
        await call.RequestStream.WriteAsync(new EchoRequest { Text = "c", RepeatCount = 3 });
        await call.RequestStream.CompleteAsync();

        var replies = new List<string>();
        await foreach (var reply in call.ResponseStream.ReadAllAsync())
            replies.Add(reply.Text);

        replies.ShouldBe(["a", "a", "b", "c", "c", "c"]);
    }

    [Fact]
    public async Task zero_requests_produces_zero_replies()
    {
        using var call = _fixture.CreateClient().Echo();
        await call.RequestStream.CompleteAsync();

        var replies = new List<EchoReply>();
        await foreach (var reply in call.ResponseStream.ReadAllAsync())
            replies.Add(reply);

        replies.ShouldBeEmpty();
    }

    [Fact]
    public async Task request_with_zero_repeat_count_produces_no_replies()
    {
        using var call = _fixture.CreateClient().Echo();
        await call.RequestStream.WriteAsync(new EchoRequest { Text = "nothing", RepeatCount = 0 });
        await call.RequestStream.CompleteAsync();

        var replies = new List<EchoReply>();
        await foreach (var reply in call.ResponseStream.ReadAllAsync())
            replies.Add(reply);

        replies.ShouldBeEmpty();
    }

    [Fact]
    public void bidi_method_is_classified_as_bidirectional_streaming_by_the_chain()
    {
        var graph = _fixture.Services.GetRequiredService<GrpcGraph>();
        var chain = graph.Chains.Single(c => c.StubType == typeof(BidiEchoStub));

        chain.BidirectionalStreamingMethods.Count.ShouldBe(1);
        chain.BidirectionalStreamingMethods[0].Name.ShouldBe("Echo");
        chain.UnaryMethods.ShouldBeEmpty();
        chain.ServerStreamingMethods.ShouldBeEmpty();
    }
}

public class grpc_bidi_discovery_tests
{
    [Fact]
    public void classifies_bidi_method_as_bidirectional_streaming()
    {
        var classified = GrpcServiceChain.DiscoverSupportedMethods(typeof(BidiEchoTest.BidiEchoTestBase))
            .ToDictionary(m => m.Method.Name, m => m.Kind);

        classified["Echo"].ShouldBe(GrpcMethodKind.BidirectionalStreaming);
    }

    [Fact]
    public void classifies_client_streaming_method_correctly()
    {
        var classified = GrpcServiceChain.DiscoverSupportedMethods(typeof(ClientStreamTest.ClientStreamTestBase))
            .ToDictionary(m => m.Method.Name, m => m.Kind);

        classified["Collect"].ShouldBe(GrpcMethodKind.ClientStreaming);
    }
}
