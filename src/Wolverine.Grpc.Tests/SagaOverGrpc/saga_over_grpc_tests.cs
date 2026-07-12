using Grpc.Core;
using Shouldly;
using Xunit;

namespace Wolverine.Grpc.Tests.SagaOverGrpc;

/// <summary>
///     Characterization tests for driving a Wolverine saga over a gRPC service hop.
///     <para>
///         The <b>message-identified</b> saga case (id on the request DTO) already works today and is
///         covered elsewhere by the ordinary saga compliance suites — the gRPC shim just forwards to
///         <c>InvokeAsync</c> and Wolverine reads the id out of the message body.
///     </para>
///     <para>
///         This test pins the <b>header-identified</b> gap: a saga whose id comes from the envelope
///         <c>saga-id</c> header can't resolve its id over gRPC, because nothing carries <c>saga-id</c>
///         across the hop. The failure currently surfaces as an opaque <see cref="StatusCode.Internal"/>
///         (because <c>IndeterminateSagaStateIdException</c> is a bare <see cref="Exception"/> and
///         <c>WolverineGrpcExceptionMapper</c> falls through to its default). When the scoped diagnostic
///         lands, flip the assertions below to the actionable status/message.
///     </para>
/// </summary>
public class saga_over_grpc_tests : IClassFixture<SagaOverGrpcFixture>
{
    private readonly SagaOverGrpcFixture _fixture;

    public saga_over_grpc_tests(SagaOverGrpcFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task starting_a_header_identified_saga_over_grpc_fails_with_opaque_status_today()
    {
        var client = _fixture.CreateClient();

        var ex = await Should.ThrowAsync<RpcException>(async () =>
            await client.Start(new StartCountingRequest { Label = "first" }));

        // CHARACTERIZATION of current behavior (GH-3385). saga-id is not propagated across a gRPC
        // hop, so PullSagaIdFromEnvelopeFrame throws IndeterminateSagaStateIdException, which maps to
        // Internal with a message that doesn't tell the developer what to do about it.
        ex.StatusCode.ShouldBe(StatusCode.Internal);
        ex.Status.Detail.ShouldContain("saga state id");
    }
}
