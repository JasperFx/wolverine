using Grpc.Core;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Wolverine.Persistence.Sagas;
using Xunit;

namespace Wolverine.Grpc.Tests.SagaOverGrpc;

/// <summary>
///     Tests for driving a Wolverine saga over a gRPC service hop, covering both identity models.
///     <para>
///         <b>Message-identified</b> sagas (id on the request DTO) work over gRPC just like they do
///         over HTTP — the gRPC shim forwards to the same <c>InvokeAsync</c> pipeline that a
///         <c>[WolverinePost]</c> endpoint uses. <see cref="can_start_and_continue_a_message_identified_saga_over_grpc"/>
///         is the gRPC parallel of
///         <c>Wolverine.Http.Tests.building_a_saga_and_publishing_other_messages_from_http_endpoint</c>.
///     </para>
///     <para>
///         <b>Header-identified</b> sagas (id from the envelope <c>saga-id</c> header) can't resolve
///         their id over gRPC, because nothing carries <c>saga-id</c> across the hop — the same
///         limitation HTTP endpoints have. That gap (GH-3385) is pinned by
///         <see cref="starting_a_header_identified_saga_over_grpc_fails_with_opaque_status_today"/>;
///         when the scoped diagnostic lands, flip its assertions to the actionable status/message.
///     </para>
/// </summary>
public class saga_over_grpc_tests : IClassFixture<SagaOverGrpcFixture>
{
    private readonly SagaOverGrpcFixture _fixture;

    public saga_over_grpc_tests(SagaOverGrpcFixture fixture)
    {
        _fixture = fixture;
    }

    #region sample_starting_and_continuing_saga_over_grpc

    [Fact]
    public async Task can_start_and_continue_a_message_identified_saga_over_grpc()
    {
        var client = _fixture.CreateReservationClient();
        var persistor = _fixture.Services.GetRequiredService<InMemorySagaPersistor>();

        // Start the saga over gRPC — same InvokeAsync path a WolverinePost endpoint would take.
        var booked = await client.Start(new StartReservationRequest { ReservationId = "dinner" });
        booked.ReservationId.ShouldBe("dinner");

        // The saga was persisted by its message-supplied id, no saga-id header required.
        var saved = persistor.Load<ReservationSaga>("dinner");
        saved.ShouldNotBeNull();
        saved.Booked.ShouldBeFalse();

        // Continue the saga over gRPC — id comes off the follow-up message.
        var result = await client.Book(new BookReservationRequest { Id = "dinner" });
        result.Completed.ShouldBeTrue();

        // Handle(BookReservationRequest) marked the saga completed, so its state is deleted.
        persistor.Load<ReservationSaga>("dinner").ShouldBeNull();
    }

    #endregion

    [Fact]
    public async Task starting_a_header_identified_saga_over_grpc_fails_with_an_actionable_diagnostic()
    {
        var client = _fixture.CreateClient();

        var ex = await Should.ThrowAsync<RpcException>(async () =>
            await client.Start(new StartCountingRequest { Label = "first" }));

        // GH-3385. A header-identified saga still cannot work over a gRPC hop — the 'saga-id' header
        // is not propagated across the call, so no id can be resolved. That is a caller-side contract
        // problem, not a transient server fault, so it is InvalidArgument (AIP-193) rather than the
        // Internal it used to report...
        ex.StatusCode.ShouldBe(StatusCode.InvalidArgument);

        // ...and the detail now names the cause AND the remedy, instead of the bare
        // "Could not determine a valid saga state id for Envelope ..." that told the developer
        // nothing they could act on.
        ex.Status.Detail.ShouldContain("ON THE MESSAGE BODY");
        ex.Status.Detail.ShouldContain("[SagaIdentity]");
        ex.Status.Detail.ShouldContain("wolverinefx.net/guide/grpc/sagas");
    }
}
