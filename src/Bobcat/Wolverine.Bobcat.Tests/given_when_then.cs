using Bobcat.Engine;

namespace Wolverine.Bobcat.Tests;

[Collection(nameof(AppointmentsCollection))]
public class given_when_then(AppointmentsHost app) : WolverineSpec(app.Host)
{
    private static Patient ann => new("Ann", new Address("Austin"));

    private static AppointmentScheduled scheduled(Guid id) => new(id, ann, ["first visit"]);

    [Fact]
    public async Task an_arranged_stream_an_act_and_the_events_it_appended_render_as_steps()
    {
        var id = Guid.NewGuid();

        var recording = await Recordings.RecordAsync(async () =>
        {
            await GivenEvents<Appointment>(id, scheduled(id));
            await WhenReceived(new ConfirmAppointment(id));
            ThenEvents(typeof(AppointmentConfirmed));
        });

        recording.Steps.Select(x => $"{x.Keyword} {x.Text}").ShouldBe(new[]
        {
            $"Given Appointment \"{id}\" has already recorded AppointmentScheduled",
            "When ConfirmAppointment is received",
            "Then AppointmentConfirmed is emitted"
        });

        recording.Steps.ShouldAllBe(x => x.Status == ResultStatus.success);
        recording.GatheredFailures().ShouldBeNull();
    }

    [Fact]
    public async Task the_wrong_event_type_is_a_failed_cell_and_the_scenario_carries_on()
    {
        var id = Guid.NewGuid();

        var recording = await Recordings.RecordAsync(async () =>
        {
            await GivenEvents<Appointment>(id, scheduled(id));
            await WhenReceived(new ConfirmAppointment(id));
            ThenEvents(typeof(AppointmentScheduled));
            ThenMessageSent<SendConfirmationEmail>();
        });

        var then = recording.Step("AppointmentScheduled is emitted");
        then.Status.ShouldBe(ResultStatus.failed);
        then.Cells.Single(x => x.Name == "event").Actual.ShouldBe(nameof(AppointmentConfirmed));

        // gathered, not thrown: the next step still ran and passed
        recording.Step("SendConfirmationEmail is sent").Status.ShouldBe(ResultStatus.success);
    }

    [Fact]
    public async Task outside_a_recording_a_wrong_is_thrown()
    {
        var id = Guid.NewGuid();
        await GivenEvents<Appointment>(id, scheduled(id));
        await WhenReceived(new ConfirmAppointment(id));

        Should.Throw<SpecificationFailedException>(() => ThenEvents(typeof(AppointmentScheduled)))
            .Message.ShouldContain("AppointmentConfirmed");
    }

    [Fact]
    public async Task a_handler_that_throws_is_captured_and_asserted_as_a_refusal()
    {
        var id = Guid.NewGuid();
        await GivenEvents<Appointment>(id, scheduled(id), new AppointmentConfirmed(id, DateTimeOffset.UtcNow));

        await WhenReceived(new ConfirmAppointment(id));

        ThenValidationFails("already confirmed");
        ThenNoEvents();
    }

    [Fact]
    public async Task an_event_assertion_after_a_failed_act_names_the_failure_not_the_count()
    {
        var id = Guid.NewGuid();
        await GivenEvents<Appointment>(id, scheduled(id), new AppointmentConfirmed(id, DateTimeOffset.UtcNow));
        await WhenReceived(new ConfirmAppointment(id));

        Should.Throw<SpecificationFailedException>(() => ThenEvents(typeof(AppointmentConfirmed)))
            .Message.ShouldContain("The act failed: InvalidOperationException: The appointment is already confirmed");
    }

    [Fact]
    public async Task a_slice_that_mints_its_stream_is_read_from_the_high_water_mark()
    {
        // No GivenEvents: the act's events are whatever the store issued after it began
        var id = Guid.NewGuid();

        await WhenReceived(new ScheduleAppointment(id, ann, []));

        ThenEvents(typeof(AppointmentScheduled));
        await ThenStreamIsStarted<Appointment>(id);
        TheEvent<AppointmentScheduled>().Patient.Name.ShouldBe("Ann");
    }

    [Fact]
    public async Task no_events_yet_renders_and_names_the_stream()
    {
        var id = Guid.NewGuid();

        var recording = await Recordings.RecordAsync(async () =>
        {
            await GivenNoEventsFor<Appointment>(id);
            await WhenReceived(new ScheduleAppointment(id, ann, []));
            ThenEvents(typeof(AppointmentScheduled));
        });

        recording.Steps[0].Text.ShouldBe($"Appointment \"{id}\" has no events yet");
        recording.GatheredFailures().ShouldBeNull();
    }

    [Fact]
    public async Task the_read_model_after_the_act()
    {
        var id = Guid.NewGuid();
        await GivenEvents<Appointment>(id, scheduled(id));
        await WhenReceived(new ConfirmAppointment(id));

        var appointment = await ThenReadModel<Appointment>(id);
        appointment.Confirmed.ShouldBeTrue();
    }

    [Fact]
    public async Task the_write_model_folded_from_its_stream()
    {
        var id = Guid.NewGuid();
        await GivenEvents<Appointment>(id, scheduled(id));

        (await TheAggregate<Appointment>(id))!.Patient.ShouldBe("Ann");
    }

    [Fact]
    public async Task a_published_message_is_tracked_like_a_received_one()
    {
        var id = Guid.NewGuid();
        await WhenPublished(new PatientArrived(id));

        ThenMessageSent<PatientArrived>("local://");
        ThenNoEvents();
    }
}
