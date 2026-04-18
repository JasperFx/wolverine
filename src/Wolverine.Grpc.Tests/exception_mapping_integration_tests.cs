using Grpc.Core;
using ProtoBuf.Grpc;
using Shouldly;
using Xunit;

namespace Wolverine.Grpc.Tests;

/// <summary>
///     Exercises <see cref="WolverineGrpcExceptionInterceptor"/> + <see cref="WolverineGrpcExceptionMapper"/>
///     end-to-end through the code-first gRPC host so the full ASP.NET Core gRPC pipeline is involved,
///     not just the mapping table.
/// </summary>
[Collection("grpc")]
public class exception_mapping_integration_tests : IClassFixture<GrpcTestFixture>
{
    private readonly GrpcTestFixture _fixture;

    public exception_mapping_integration_tests(GrpcTestFixture fixture)
    {
        _fixture = fixture;
    }

    [Theory]
    [InlineData("argument", StatusCode.InvalidArgument)]
    [InlineData("key", StatusCode.NotFound)]
    [InlineData("file", StatusCode.NotFound)]
    [InlineData("unauthorized", StatusCode.PermissionDenied)]
    [InlineData("invalid", StatusCode.FailedPrecondition)]
    [InlineData("notimpl", StatusCode.Unimplemented)]
    [InlineData("timeout", StatusCode.DeadlineExceeded)]
    [InlineData("generic", StatusCode.Internal)]
    public async Task unary_handler_exception_maps_to_canonical_status_code(string kind, StatusCode expected)
    {
        var client = _fixture.CreateClient<IFaultingService>();

        var ex = await Should.ThrowAsync<RpcException>(
            () => client.Throw(new FaultCodeFirstRequest { Kind = kind }));

        ex.StatusCode.ShouldBe(expected);
    }

    [Fact]
    public async Task streaming_handler_exception_after_first_yield_maps_to_canonical_status_code()
    {
        var client = _fixture.CreateClient<IFaultingService>();

        var received = new List<string>();
        var ex = await Should.ThrowAsync<RpcException>(async () =>
        {
            await foreach (var reply in client.ThrowStream(new FaultStreamCodeFirstRequest { Kind = "key" }))
            {
                received.Add(reply.Message);
            }
        });

        received.ShouldHaveSingleItem();
        received[0].ShouldBe("about-to-fail");
        ex.StatusCode.ShouldBe(StatusCode.NotFound);
    }
}
