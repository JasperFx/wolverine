using Grpc.Core;
using Shouldly;
using Xunit;

namespace Wolverine.Grpc.Tests.Client;

/// <summary>
///     Unit tests for <see cref="WolverineGrpcExceptionMapper.MapToException"/> — the inverse
///     of the long-standing <see cref="WolverineGrpcExceptionMapper.Map"/> table. Run as a
///     plain unit test (no gRPC plumbing) so the mapping table is verified independently of
///     interceptor wiring.
/// </summary>
public class exception_mapper_reverse_tests
{
    [Theory]
    [InlineData(StatusCode.Cancelled, typeof(OperationCanceledException))]
    [InlineData(StatusCode.DeadlineExceeded, typeof(TimeoutException))]
    [InlineData(StatusCode.InvalidArgument, typeof(ArgumentException))]
    [InlineData(StatusCode.NotFound, typeof(KeyNotFoundException))]
    [InlineData(StatusCode.PermissionDenied, typeof(UnauthorizedAccessException))]
    [InlineData(StatusCode.Unauthenticated, typeof(UnauthorizedAccessException))]
    [InlineData(StatusCode.FailedPrecondition, typeof(InvalidOperationException))]
    [InlineData(StatusCode.Unimplemented, typeof(NotImplementedException))]
    public void known_status_codes_map_to_idiomatic_net_exception_types(
        StatusCode statusCode,
        Type expectedType)
    {
        var rpc = new RpcException(new Status(statusCode, "boom"));

        var mapped = WolverineGrpcExceptionMapper.MapToException(rpc);

        mapped.ShouldBeOfType(expectedType);
        mapped.Message.ShouldContain("boom");
        mapped.InnerException.ShouldBeSameAs(rpc);
    }

    [Theory]
    [InlineData(StatusCode.Internal)]
    [InlineData(StatusCode.Unknown)]
    [InlineData(StatusCode.DataLoss)]
    public void unmapped_status_codes_return_the_original_rpc_exception(StatusCode statusCode)
    {
        var rpc = new RpcException(new Status(statusCode, "boom"));

        var mapped = WolverineGrpcExceptionMapper.MapToException(rpc);

        mapped.ShouldBeSameAs(rpc);
    }

    [Fact]
    public void preserves_original_rpc_exception_on_inner_exception_for_diagnostics()
    {
        // Rich error details live on RpcException (trailers, status details bin) — the client-side
        // translation MUST preserve the original so diagnostic fidelity is not lost across the
        // boundary. Regression guard.
        var rpc = new RpcException(new Status(StatusCode.NotFound, "record 42"));

        var mapped = WolverineGrpcExceptionMapper.MapToException(rpc);

        mapped.ShouldBeOfType<KeyNotFoundException>();
        mapped.InnerException.ShouldBeSameAs(rpc);
    }
}
