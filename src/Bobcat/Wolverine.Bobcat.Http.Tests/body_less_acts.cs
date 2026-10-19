using Bobcat.Engine;

namespace Wolverine.Bobcat.Http.Tests;

// GH-5076: POST / PUT / PATCH / DELETE with no body, so the step reads as the endpoint rather than an anonymous type
[Collection(nameof(AppCollection))]
public class body_less_acts(AppHost app) : WolverineHttpSpec(app.Host)
{
    private async Task<Guid> scheduled()
    {
        var theAppointment = Guid.NewGuid();
        await GivenEvents<Appointment>(theAppointment, new AppointmentScheduled(theAppointment, "Ann"));
        return theAppointment;
    }

    [Fact]
    public async Task a_post_with_no_body_reads_as_its_route()
    {
        var id = await scheduled();

        var recording = await Recordings.RecordAsync(async () =>
        {
            await WhenPostedTo("/api/appointments/{id}/arrive", id);
            ThenResponseIs(204);
            ThenEvents(new PatientArrivedAtDesk(id));
        });

        recording.GatheredFailures().ShouldBeNull();
        recording.Steps[0].Keyword.ShouldBe("When");
        recording.Steps[0].Text.ShouldBe("a POST is sent to \"/api/appointments/{id}/arrive\"");
        recording.Steps[0].Cells.Single(x => x.Name == "status").DisplayText.ShouldBe("204");

        // The filled URL, and no body, are in the HTTP exchange report
        var exchange = recording.Reports.OfType<HttpExchangeReport>().Single();
        exchange.Cells.ShouldContain(x => x.DisplayText.Contains($"/api/appointments/{id}/arrive"));
    }

    [Fact]
    public async Task put_patch_and_delete_with_no_body()
    {
        var id = await scheduled();

        await WhenPutTo("/api/appointments/{id}/hold", id);
        ThenEvents(new AppointmentHeld(id));

        await WhenPatchedTo("/api/appointments/{id}/flag", id);
        ThenEvents(new AppointmentFlagged(id));

        await WhenDeletedAt("/api/appointments/{id}/confirmation", id);
        ThenEvents(new AppointmentUnconfirmed(id));

        var recording = await Recordings.RecordAsync(() => WhenDeletedAt("/api/appointments/{id}/confirmation", id));
        recording.Steps.Single().Text.ShouldBe("a DELETE is sent to \"/api/appointments/{id}/confirmation\"");
    }

    [Fact]
    public async Task a_refusal_is_still_a_refusal()
    {
        await WhenPostedTo("/api/appointments/{id}/arrive", Guid.NewGuid());

        ThenNotFound();
        LastAct.Refusal.ShouldNotBeNull();
    }

    [Fact]
    public async Task the_route_values_must_fill_the_template()
    {
        await Should.ThrowAsync<ArgumentException>(() => WhenPostedTo("/api/appointments/{id}/arrive"));
    }

    [Fact]
    public async Task what_the_endpoint_cascaded_was_handled()
    {
        var id = await scheduled();
        await WhenPosted(new ConfirmAppointmentRequest(id));

        ThenHandled<SendConfirmationEmail>();
        ThenNotHandled<ConfirmAppointmentRequest>();
    }
}
