using Grpc.Core;
using Shouldly;
using Wolverine.Grpc.Tests.GrpcMiddlewareScoping.Generated;
using Xunit;

namespace Wolverine.Grpc.Tests.GrpcMiddlewareScoping;

/// <summary>
///     M15 Phase 1: verifies that <see cref="Wolverine.Attributes.MiddlewareScoping.Grpc"/>-scoped
///     middleware methods on a proto-first stub are actually woven into the generated gRPC service
///     wrappers and fire at RPC time in the correct order.
/// </summary>
public class middleware_weaving_execution_tests : IClassFixture<MiddlewareScopingFixture>
{
    private readonly MiddlewareScopingFixture _fixture;

    public middleware_weaving_execution_tests(MiddlewareScopingFixture fixture)
    {
        _fixture = fixture;
        _fixture.Sink.Clear();
    }

    // ── unary ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task unary_fires_anywhere_scoped_before()
    {
        await _fixture.CreateClient().GreetAsync(new GreetRequest { Name = "test" });

        _fixture.Sink.Contains(GreeterMiddlewareTestStub.AnywhereMarker).ShouldBeTrue(
            "[WolverineBefore] (Anywhere scope) must execute on every unary gRPC call");
    }

    [Fact]
    public async Task unary_fires_grpc_scoped_before()
    {
        await _fixture.CreateClient().GreetAsync(new GreetRequest { Name = "test" });

        _fixture.Sink.Contains(GreeterMiddlewareTestStub.GrpcMarker).ShouldBeTrue(
            "[WolverineBefore(MiddlewareScoping.Grpc)] must execute on unary gRPC calls");
    }

    [Fact]
    public async Task unary_does_not_fire_message_handlers_scoped_before()
    {
        await _fixture.CreateClient().GreetAsync(new GreetRequest { Name = "test" });

        _fixture.Sink.Contains(GreeterMiddlewareTestStub.MessageHandlersMarker).ShouldBeFalse(
            "[WolverineBefore(MiddlewareScoping.MessageHandlers)] must not execute on gRPC calls");
    }

    [Fact]
    public async Task unary_fires_anywhere_scoped_after()
    {
        await _fixture.CreateClient().GreetAsync(new GreetRequest { Name = "test" });

        _fixture.Sink.Contains(GreeterMiddlewareTestStub.AnywhereMarker + ".After").ShouldBeTrue(
            "[WolverineAfter] (Anywhere scope) must execute after a unary gRPC call");
    }

    [Fact]
    public async Task unary_fires_grpc_scoped_after()
    {
        await _fixture.CreateClient().GreetAsync(new GreetRequest { Name = "test" });

        _fixture.Sink.Contains(GreeterMiddlewareTestStub.GrpcMarker + ".After").ShouldBeTrue(
            "[WolverineAfter(MiddlewareScoping.Grpc)] must execute after a unary gRPC call");
    }

    [Fact]
    public async Task unary_does_not_fire_message_handlers_scoped_after()
    {
        await _fixture.CreateClient().GreetAsync(new GreetRequest { Name = "test" });

        _fixture.Sink.Contains(GreeterMiddlewareTestStub.MessageHandlersMarker + ".After").ShouldBeFalse(
            "[WolverineAfter(MiddlewareScoping.MessageHandlers)] must not execute on gRPC calls");
    }

    [Fact]
    public async Task unary_before_fires_before_handler_and_after_fires_after_handler()
    {
        await _fixture.CreateClient().GreetAsync(new GreetRequest { Name = "test" });

        var events = _fixture.Sink.Events;
        var beforeGrpcIdx = events.ToList().IndexOf(GreeterMiddlewareTestStub.GrpcMarker);
        var handlerIdx = events.ToList().IndexOf(GreetMessageHandler.Marker);
        var afterGrpcIdx = events.ToList().IndexOf(GreeterMiddlewareTestStub.GrpcMarker + ".After");

        beforeGrpcIdx.ShouldBeLessThan(handlerIdx, "before-middleware must run before the handler");
        handlerIdx.ShouldBeLessThan(afterGrpcIdx, "handler must run before after-middleware");
    }

    // ── server streaming ───────────────────────────────────────────────────────

    [Fact]
    public async Task server_streaming_fires_grpc_scoped_before()
    {
        using var call = _fixture.CreateClient().GreetMany(new GreetManyRequest { Name = "test" });
        await foreach (var _ in call.ResponseStream.ReadAllAsync()) { }

        _fixture.Sink.Contains(GreeterMiddlewareTestStub.GrpcMarker).ShouldBeTrue(
            "[WolverineBefore(MiddlewareScoping.Grpc)] must execute on server-streaming gRPC calls");
    }

    [Fact]
    public async Task server_streaming_fires_grpc_scoped_after()
    {
        using var call = _fixture.CreateClient().GreetMany(new GreetManyRequest { Name = "test" });
        await foreach (var _ in call.ResponseStream.ReadAllAsync()) { }

        _fixture.Sink.Contains(GreeterMiddlewareTestStub.GrpcMarker + ".After").ShouldBeTrue(
            "[WolverineAfter(MiddlewareScoping.Grpc)] must execute after the server-streaming loop completes");
    }

    [Fact]
    public async Task server_streaming_does_not_fire_message_handlers_scoped_middleware()
    {
        using var call = _fixture.CreateClient().GreetMany(new GreetManyRequest { Name = "test" });
        await foreach (var _ in call.ResponseStream.ReadAllAsync()) { }

        _fixture.Sink.Contains(GreeterMiddlewareTestStub.MessageHandlersMarker).ShouldBeFalse(
            "[WolverineBefore(MiddlewareScoping.MessageHandlers)] must not execute on gRPC calls");
        _fixture.Sink.Contains(GreeterMiddlewareTestStub.MessageHandlersMarker + ".After").ShouldBeFalse(
            "[WolverineAfter(MiddlewareScoping.MessageHandlers)] must not execute on gRPC calls");
    }
}
