using Bobcat;
using Bobcat.Engine;

namespace Wolverine.Bobcat.Tests;

// GH-5076: a Given that arranges by running a handler, rendered as a Given and never the act
[Collection(nameof(AppointmentsCollection))]
public class arranging_by_a_received_message(AppointmentsHost app) : WolverineSpec(app.Host)
{
    private static ScheduleAppointment schedule(Guid id) => new(id, new Patient("Ann", new Address("Austin")), []);

    [Fact]
    public async Task the_handler_runs_as_a_given_and_the_when_is_still_the_act()
    {
        var theAppointment = Guid.NewGuid();

        var recording = await Recordings.RecordAsync(async () =>
        {
            await GivenReceived(schedule(theAppointment));
            await WhenReceived(new ConfirmAppointment(theAppointment));
            ThenEvents(typeof(AppointmentConfirmed));
            ThenMessageSent<SendConfirmationEmail>();
        });

        recording.GatheredFailures().ShouldBeNull();
        recording.Steps.Select(x => x.Keyword).ShouldBe(["Given", "When", "Then", "And"]);
        // Too long for the line, so the message's values are a table under the step
        recording.Steps[0].Text.ShouldBe("ScheduleAppointment has already been received");
        recording.Steps[0].Cells.ShouldNotBeEmpty();
    }

    [Fact]
    public async Task arranging_is_not_an_act()
    {
        await GivenReceived(schedule(Guid.NewGuid()));

        LastAct.ShouldBeSameAs(ActOutcome.None);
        Should.Throw<SpecificationFailedException>(() => ThenEvents(typeof(AppointmentScheduled)))
            .Message.ShouldContain("no act has run");
        Should.Throw<SpecificationFailedException>(() => ThenNotHandled<ScheduleAppointment>())
            .Message.ShouldContain("no act has run");
    }

    [Fact]
    public async Task what_the_arranging_handler_appended_is_not_the_acts()
    {
        var id = Guid.NewGuid();
        await GivenReceived(schedule(id));
        await WhenReceived(new ConfirmAppointment(id));

        ThenEvents(typeof(AppointmentConfirmed));
        LastAct.AllNewEvents.Select(x => x.Data).OfType<AppointmentScheduled>().ShouldBeEmpty();
        ThenNotHandled<ScheduleAppointment>();
    }

    [Fact]
    public async Task a_handler_that_throws_fails_the_given()
    {
        var id = Guid.NewGuid();
        await GivenEvents<Appointment>(id, new AppointmentScheduled(id, new Patient("Ann", new Address("Austin")), []),
            new AppointmentConfirmed(id, DateTimeOffset.UtcNow));

        var thrown = await Should.ThrowAsync<SpecCriticalException>(() => GivenReceived(new ConfirmAppointment(id)));
        thrown.Message.ShouldContain("handling it failed, so it arranged nothing");
        thrown.Message.ShouldContain("The appointment is already confirmed");
    }

    [Fact]
    public async Task a_timeout_message_is_only_scheduled_so_it_arranges_nothing_and_the_given_fails()
    {
        // GH-5077: sent exactly as given, as Wolverine sends it
        var thrown = await Should.ThrowAsync<SpecCriticalException>(() => GivenReceived(new ConfirmationOverdue(Guid.NewGuid())));
        thrown.Message.ShouldContain("it was only scheduled for later, so its handler never ran and it arranged nothing");
        LastAct.ShouldBeSameAs(ActOutcome.None);
    }
}
