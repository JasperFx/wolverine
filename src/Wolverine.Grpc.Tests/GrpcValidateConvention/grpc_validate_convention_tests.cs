using Grpc.Core;
using Shouldly;
using Wolverine.Grpc.Tests.GrpcValidateConvention.Generated;
using Xunit;

namespace Wolverine.Grpc.Tests.GrpcValidateConvention;

/// <summary>
///     End-to-end tests for the proto-first <c>Validate → Status?</c> short-circuit
///     convention. Verifies that a static <c>Validate</c> method returning a non-null
///     <see cref="Status"/> throws <see cref="RpcException"/> before the Wolverine
///     handler runs, and that null-returning validate passes through to the handler.
/// </summary>
public class grpc_validate_convention_tests : IClassFixture<ValidateConventionFixture>
{
    private readonly ValidateConventionFixture _fixture;

    public grpc_validate_convention_tests(ValidateConventionFixture fixture)
    {
        _fixture = fixture;
        _fixture.Sink.Clear();
    }

    [Fact]
    public async Task valid_request_passes_through_to_handler()
    {
        var reply = await _fixture.CreateClient().GreetAsync(new ValidateGreetRequest { Name = "Erik" });

        reply.Message.ShouldBe("Hello, Erik");
        _fixture.Sink.Contains(ValidateGreetHandler.Marker).ShouldBeTrue(
            "handler must run when Validate returns null");
    }

    [Fact]
    public async Task empty_name_is_short_circuited_with_invalid_argument()
    {
        var ex = await Should.ThrowAsync<RpcException>(
            async () => await _fixture.CreateClient().GreetAsync(new ValidateGreetRequest { Name = "" }));

        ex.StatusCode.ShouldBe(StatusCode.InvalidArgument);
        ex.Status.Detail.ShouldBe("Name is required");
    }

    [Fact]
    public async Task blank_name_is_short_circuited_with_invalid_argument()
    {
        var ex = await Should.ThrowAsync<RpcException>(
            async () => await _fixture.CreateClient().GreetAsync(new ValidateGreetRequest { Name = "   " }));

        ex.StatusCode.ShouldBe(StatusCode.InvalidArgument);
    }

    [Fact]
    public async Task forbidden_prefix_is_short_circuited_with_permission_denied()
    {
        var ex = await Should.ThrowAsync<RpcException>(
            async () => await _fixture.CreateClient().GreetAsync(new ValidateGreetRequest { Name = "forbidden:bob" }));

        ex.StatusCode.ShouldBe(StatusCode.PermissionDenied);
        ex.Status.Detail.ShouldBe("Name prefix is not allowed");
    }

    [Fact]
    public async Task handler_does_not_run_when_validate_rejects()
    {
        await Should.ThrowAsync<RpcException>(
            async () => await _fixture.CreateClient().GreetAsync(new ValidateGreetRequest { Name = "" }));

        _fixture.Sink.Contains(ValidateGreetHandler.Marker).ShouldBeFalse(
            "handler must NOT run when Validate returns a non-null Status");
    }

    [Fact]
    public async Task validate_returning_null_does_not_pollute_sink_from_handler_invocation()
    {
        _fixture.Sink.Clear();
        await _fixture.CreateClient().GreetAsync(new ValidateGreetRequest { Name = "Alice" });

        _fixture.Sink.CountOf(ValidateGreetHandler.Marker).ShouldBe(1,
            "handler runs exactly once per valid call");
    }
}
