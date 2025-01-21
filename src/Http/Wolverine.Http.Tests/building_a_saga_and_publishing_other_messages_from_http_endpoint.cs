using Alba;
using Shouldly;
using Wolverine.Tracking;
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
        await Host.GetRuntime().Storage.Admin.ClearAllAsync();
        await Store.Advanced.Clean.DeleteDocumentsByTypeAsync(typeof(Reservation));

        IScenarioResult result = null!;

        Func<IMessageContext, Task> action = async _ =>
        {
            result = await Host.Scenario(x =>
            {
                x.Post.Json(new StartReservation("dinner")).ToUrl("/reservation");
            });
        };
        
        // The outer part is tying into Wolverine's test support
        // to "wait" for all detected message activity to complete
        await Host
            .TrackActivity()
            .ExecuteAndWaitAsync(action);

        using var session = Store.LightweightSession();
        var reservation = await session.LoadAsync<Reservation>("dinner");
        reservation.ShouldNotBeNull();

        result.ReadAsJson<ReservationBooked>()
            .ReservationId.ShouldBe("dinner");
    }
}