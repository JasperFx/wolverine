using Bobcat.Engine;

namespace Wolverine.Bobcat.Http.Tests;

// GH-4834: HTTP acts through Alba inside the tracked session
[Collection(nameof(AppCollection))]
public class posting_commands(AppHost app) : WolverineHttpSpec(app.Host)
{
    private async Task<Guid> scheduled()
    {
        var id = Guid.NewGuid();
        await GivenEvents<Appointment>(id, new AppointmentScheduled(id, "Ann"));
        return id;
    }

    [Fact]
    public async Task the_route_is_resolved_from_the_request_type_and_the_whole_vocabulary_applies()
    {
        var id = await scheduled();

        var recording = await Recordings.RecordAsync(async () =>
        {
            await WhenPosted(new ConfirmAppointmentRequest(id));
            ThenEvents(typeof(AppointmentConfirmed));
            ThenMessageSent<SendConfirmationEmail>();
        });

        recording.GatheredFailures().ShouldBeNull();

        var when = recording.Steps[0];
        when.Text.ShouldBe("ConfirmAppointmentRequest(AppointmentId: Appointment) is posted to \"/api/appointments/confirm\"");
        when.Cells.Single(x => x.Name == "status").DisplayText.ShouldBe("200");
    }

    [Fact]
    public async Task a_partial_command_is_built_once_so_the_route_and_the_body_agree()
    {
        var id = await scheduled();

        var recording = await Recordings.RecordAsync(async () =>
        {
            // Reason is never mentioned: the build fills it, and the route still comes from AppointmentId
            await WhenPosted(Specify<CancelAppointment>().With(x => x.AppointmentId, id));
            ThenEvents(Specify<AppointmentCancelled>().With(x => x.AppointmentId, id));
        });

        recording.GatheredFailures().ShouldBeNull();
        recording.Steps[0].Text.ShouldBe($"CancelAppointment(AppointmentId: Appointment) is posted to \"/api/appointments/{id}/cancel\"");
    }

    [Fact]
    public async Task a_typed_response_is_handed_back()
    {
        var id = await scheduled();

        var response = await WhenPosted<ConfirmationResponse>(new ConfirmAppointmentRequest(id));

        response.ShouldBe(new ConfirmationResponse(id, "Confirmed"));
        Verify(response!, """
                          | Status    |
                          | Confirmed |
                          """);
    }

    [Fact]
    public async Task any_2xx_is_success_and_route_parameters_are_filled_from_the_command()
    {
        var id = await scheduled();

        await WhenPosted(new CancelAppointment(id, "moved away"));

        ThenResponseIs(204);
        ThenEvents(new AppointmentCancelled(id, "moved away"));
    }

    [Fact]
    public async Task a_4xx_is_a_refusal_not_a_failure()
    {
        var id = await scheduled();
        await GivenEventsOn<Appointment>(id, new AppointmentConfirmed(id));

        await WhenPosted(new ConfirmAppointmentRequest(id));

        ThenResponseIs(400);
        ThenRefusedWith("already confirmed");
        ThenNoEvents();
        LastAct.Error.ShouldBeNull();
    }

    [Fact]
    public async Task a_5xx_is_the_act_failing()
    {
        await WhenPosted(new ExplodingRequest("x"));

        // the endpoint's own exception, which the tracked session captured, says more than "500"
        LastAct.Error.ShouldBeOfType<DivideByZeroException>().Message.ShouldBe("boom");
        Should.Throw<SpecificationFailedException>(() => ThenEvents(typeof(AppointmentConfirmed)))
            .Message.ShouldContain("The act failed");
    }

    [Fact]
    public void several_endpoints_accepting_the_type_are_refused_rather_than_guessed()
    {
        var ex = Should.Throw<InvalidOperationException>(() => HttpRoutes.For(Host, new AmbiguousRequest("x")));
        ex.Message.ShouldContain("/api/ambiguous/one");
        ex.Message.ShouldContain("/api/ambiguous/two");
    }

    [Fact]
    public async Task an_explicit_route_settles_the_ambiguity_and_scenario_overrides_apply()
    {
        await WhenPosted(new AmbiguousRequest("x"), "/api/ambiguous/two", x => x.WithRequestHeader("X-Test", "yes"));

        (await LastResponse!.ReadAsTextAsync()).ShouldBe("two");
    }

    [Fact]
    public async Task the_exchange_rides_along_as_a_report()
    {
        var id = await scheduled();

        var recording = await Recordings.RecordAsync(() => WhenPosted(new ConfirmAppointmentRequest(id)));

        var report = recording.Reports.OfType<HttpExchangeReport>().Single();
        report.Visibility.ShouldBe(ReportVisibility.OnFailure);
        report.Cells.ShouldContain(x => x.Name == "route" && x.DisplayText == "/api/appointments/confirm");
        report.Cells.ShouldContain(x => x.Name == "response" && x.DisplayText.Contains("Confirmed"));
    }
}
