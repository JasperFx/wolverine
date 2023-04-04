using Shouldly;
using WolverineWebApi;

namespace Wolverine.Http.Tests;

public class building_a_saga_and_publishing_other_messages_from_http_endpoint : IntegrationContext
{
    public building_a_saga_and_publishing_other_messages_from_http_endpoint(AppFixture fixture) : base(fixture)
    {
    }

    [Fact]
    public async Task can_create_saga_and_publish_message()
    {
        await Store.Advanced.Clean.DeleteDocumentsByTypeAsync(typeof(Reservation));
        
        var (tracked, result) = await TrackedHttpCall(x =>
        {
            x.Post.Json(new StartReservation("dinner")).ToUrl("/reservation");
        });

        tracked.Sent.SingleMessage<ReservationTimeout>().ShouldNotBeNull();

        using var session = Store.LightweightSession();
        var reservation = await session.LoadAsync<Reservation>("dinner");
        reservation.ShouldNotBeNull();
        
        result.ReadAsJson<ReservationBooked>()
            .ReservationId.ShouldBe("dinner");
    }
}