using Grpc.Core;
using JasperFx;
using JasperFx.Events;
using Shouldly;
using Wolverine.Persistence;
using Xunit;

namespace Wolverine.Grpc.Tests;

/// <summary>
///     GH-5983. A gRPC service forwards to a Wolverine handler, so the aggregate handler workflow's failures reach the
///     gRPC boundary as the handler threw them. They used to fall through to <see cref="StatusCode.Internal" />.
/// </summary>
public class exception_mapper_persistence_failures_5983
{
    [Fact]
    public void a_failed_concurrency_check_is_aborted()
    {
        // What a message handler throws for an expected stream version the stream is not at, or a stream that does not exist
        var exception = new ConcurrencyException("Expected the existing version to be 3, but was 0", typeof(string), Guid.NewGuid());

        WolverineGrpcExceptionMapper.Map(exception).ShouldBe(StatusCode.Aborted);
    }

    [Fact]
    public void a_stream_id_collision_already_exists()
    {
        WolverineGrpcExceptionMapper.Map(new ExistingStreamIdCollisionException(Guid.NewGuid()))
            .ShouldBe(StatusCode.AlreadyExists);
    }

    [Fact]
    public void missing_required_data_is_not_found()
    {
        WolverineGrpcExceptionMapper.Map(new RequiredDataMissingException("Unknown aggregate"))
            .ShouldBe(StatusCode.NotFound);
    }

    [Fact]
    public void the_rpc_exception_keeps_the_message()
    {
        var rpc = WolverineGrpcExceptionMapper.ToRpcException(
            new ConcurrencyException("Expected the existing version to be 3, but was 0", typeof(string), "abc"));

        rpc.StatusCode.ShouldBe(StatusCode.Aborted);
        rpc.Status.Detail.ShouldBe("Expected the existing version to be 3, but was 0");
    }
}
