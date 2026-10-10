using Microsoft.AspNetCore.Mvc;
using Bobcat.Engine;

namespace Wolverine.Bobcat.Http.Tests;

// GH-4834: HTTP acts through Alba inside the tracked session
[Collection(nameof(AppCollection))]
public class posting_commands(AppHost app) : WolverineHttpSpec(app.Host)
{
    private async Task<Guid> scheduled()
    {
        var theAppointment = Guid.NewGuid();
        await GivenEvents<Appointment>(theAppointment, new AppointmentScheduled(theAppointment, "Ann"));
        return theAppointment;
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
        when.Text.ShouldBe("ConfirmAppointmentRequest(AppointmentId: theAppointment) is posted to \"/api/appointments/confirm\"");
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
        recording.Steps[0].Text.ShouldBe($"CancelAppointment(AppointmentId: theAppointment) is posted to \"/api/appointments/{id}/cancel\"");
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
    public async Task a_problem_details_refusal_matches_the_members_a_partial_names()
    {
        var id = await scheduled();
        await GivenEventsOn<Appointment>(id, new AppointmentConfirmed(id));

        await WhenPosted(new ConfirmAppointmentRequest(id));

        var problem = await ThenRefusedWithProblem(
            Specify<ProblemDetails>().With(x => x.Detail, "The appointment is already confirmed").With(x => x.Status, 400));
        problem!.Detail.ShouldBe("The appointment is already confirmed");
    }

    [Fact]
    public async Task a_problem_details_that_disagrees_fails()
    {
        var id = await scheduled();
        await GivenEventsOn<Appointment>(id, new AppointmentConfirmed(id));

        await WhenPosted(new ConfirmAppointmentRequest(id));

        await Should.ThrowAsync<SpecificationFailedException>(() =>
            ThenRefusedWithProblem(Specify<ProblemDetails>().With(x => x.Detail, "Something else")));
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

// GH-4931: an endpoint's own events are what it committed with no envelope; anything it cascades is not
[Collection(nameof(AppCollection))]
public class the_endpoints_own_events(AppHost app) : WolverineHttpSpec(app.Host)
{
    [Fact]
    public async Task then_events_is_the_endpoints_own_and_its_cascades_events_are_not()
    {
        var theAppointment = Guid.NewGuid();
        await GivenEvents<Appointment>(theAppointment, new AppointmentScheduled(theAppointment, "Ann"));

        await WhenPosted(new RemindPatient(theAppointment));

        ThenEvents(new PatientReminded(theAppointment));
        ThenEventsOn<Appointment>(theAppointment, new PatientReminded(theAppointment), new ReminderLogged(theAppointment));
    }

    [Fact]
    public async Task a_stream_the_endpoint_started_is_named_by_the_id_it_assigned()
    {
        await WhenPosted(new BookAppointment("Ann"));

        var theAppointment = TheStartedStream<Appointment>();
        ThenEvents(new AppointmentScheduled(theAppointment, "Ann"));
    }
}
