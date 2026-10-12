using Bobcat;
using Bobcat.Generated.EventModel;
using Bobcat.Xunit;
using JasperFx.Events.EventModeling;

namespace Wolverine.Bobcat.Http.Tests;

// bobcat#471 step 2 / bobcat#463: WolverineHttpSpec.WhenPosted declares the act's HTTP trigger and its route, so a
// projected spec reaches the Event Model as an HTTP slice. An unbound [BobcatFeature] names the slice.
[BobcatFeature("Confirming appointments over HTTP")]
[Collection(nameof(AppCollection))]
public class projected_http_slice(AppHost app) : WolverineHttpSpec(app.Host)
{
    [Fact]
    public async Task a_scheduled_appointment_is_confirmed_over_http()
    {
        var id = Guid.NewGuid();
        await GivenEvents<Appointment>(id, new AppointmentScheduled(id, "Ann"));

        await WhenPosted(new ConfirmAppointmentRequest(id), "/api/appointments/confirm");

        ThenEvents(typeof(AppointmentConfirmed));
        ThenMessageSent<SendConfirmationEmail>();
    }

    [Fact]
    public void the_slice_is_an_http_one_with_its_route()
    {
        var slice = BobcatEventModelSource.Describe().Slices.Single(x => x.Name == "Confirming appointments over HTTP");

        slice.CommandType.ShouldNotBeNull().Name.ShouldBe(nameof(ConfirmAppointmentRequest));
        slice.TriggerKind.ShouldBe(TriggerKind.Http);
        slice.TriggerOrigin.ShouldNotBeNull().HttpRoute.ShouldBe("/api/appointments/confirm");
        slice.TriggerOrigin.HttpMethod.ShouldBe("POST");
        slice.PublishedMessages.Select(x => x.Name).ShouldBe([nameof(SendConfirmationEmail)]);
    }
}
