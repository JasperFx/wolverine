namespace Wolverine.Bobcat.Http.Tests;

// GH-5385 (decided on #5362): WhenTracked attributes every event committed in the application's store during the tracked
// window to the act, including those an endpoint commits through its own injected IDocumentSession
[Collection(nameof(AppCollection))]
public class events_committed_by_the_endpoints_own_session(AppHost app) : WolverineHttpSpec(app.Host)
{
    [Fact]
    public async Task then_emitted_sees_events_a_minimal_api_committed_through_its_own_session()
    {
        var id = Guid.NewGuid();
        await WhenTracked("a minimal API schedules an appointment", () => AlbaHost.Scenario(x =>
        {
            x.Post.Url($"/minimal/appointments/{id}/schedule");
            x.StatusCodeShouldBe(204);
        }));

        ThenEmitted(new AppointmentScheduled(id, "Minimal"), new AppointmentConfirmed(id));
    }

    [Fact]
    public async Task events_committed_by_a_wolverine_session_are_still_reported_once()
    {
        var id = Guid.NewGuid();
        await GivenEvents<Appointment>(id, new AppointmentScheduled(id, "Ann"));

        await WhenTracked("an appointment is confirmed over HTTP", () => AlbaHost.Scenario(x =>
        {
            x.Post.Json(new ConfirmAppointmentRequest(id)).ToUrl("/api/appointments/confirm");
            x.StatusCodeShouldBeSuccess();
        }));

        ThenEmitted(new AppointmentConfirmed(id));
        LastAct.AllNewEvents.Count.ShouldBe(1);
    }
}
