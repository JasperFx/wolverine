using Bobcat;
using Bobcat.Engine;
using JasperFx.Core;

namespace Wolverine.Bobcat.Tests;

// GH-5077: WhenReceived of a TimeoutMessage, or of any message carrying a delay, used to hand it to Wolverine, which
// scheduled it days out: the handler never ran, the act succeeded, and every negative assertion passed on nothing
[Collection(nameof(AppointmentsCollection))]
public class received_messages_are_handled_now(AppointmentsHost app) : WolverineSpec(app.Host)
{
    [Fact]
    public async Task a_timeout_message_is_handled_now_not_rescheduled()
    {
        var id = Guid.NewGuid();
        await WhenReceived(new ConfirmationOverdue(id));

        ThenHandled<ConfirmationOverdue>();
        ThenMessageSent<EscalateUnconfirmedAppointment>();
        ThenMessageSent(new EscalateUnconfirmedAppointment(id));
        ThenNoMessageScheduled<ConfirmationOverdue>();
        LastAct.Attempts.ShouldBe(1);
    }

    [Fact]
    public async Task the_false_green_is_gone()
    {
        await WhenReceived(new ConfirmationOverdue(Guid.NewGuid()));

        Should.Throw<SpecificationFailedException>(() => ThenNoMessageSent<EscalateUnconfirmedAppointment>())
            .Message.ShouldContain("Expected no EscalateUnconfirmedAppointment but 1 were sent");
        Should.Throw<SpecificationFailedException>(() => ThenNotHandled<ConfirmationOverdue>());
    }

    [Fact]
    public async Task a_published_timeout_message_is_handled_now_too()
    {
        await WhenPublished(new ConfirmationOverdue(Guid.NewGuid()));

        ThenHandled<ConfirmationOverdue>();
        ThenMessageSent<EscalateUnconfirmedAppointment>();
    }

    [Fact]
    public async Task a_timeout_message_sent_with_delivery_options_is_handled_now()
    {
        var options = new DeliveryOptions { ScheduleDelay = 1.Hours() };
        await WhenReceived(new ConfirmationOverdue(Guid.NewGuid()), options: options);

        ThenHandled<ConfirmationOverdue>();
        ThenMessageSent<EscalateUnconfirmedAppointment>();
    }

    [Fact]
    public async Task a_delay_in_the_delivery_options_is_not_the_acts()
    {
        await WhenReceived(new EscalateUnconfirmedAppointment(Guid.NewGuid()),
            options: new DeliveryOptions { ScheduledTime = DateTimeOffset.UtcNow.AddDays(1) });

        ThenHandled<EscalateUnconfirmedAppointment>();
        ThenNoMessageScheduled<EscalateUnconfirmedAppointment>();
    }

    [Fact]
    public async Task a_delayed_wrapper_is_unwrapped_and_its_message_handled_now()
    {
        var id = Guid.NewGuid();
        var recording = await Recordings.RecordAsync(async () =>
        {
            await WhenReceived(new ConfirmationOverdue(id).DelayedFor(1.Hours()));
            ThenHandled<ConfirmationOverdue>();
            ThenMessageSent<EscalateUnconfirmedAppointment>();
        });

        recording.GatheredFailures().ShouldBeNull();
        // The step reads as the message, not as the wrapper it came in
        recording.Steps[0].Text.ShouldStartWith("ConfirmationOverdue(");
        recording.Steps[0].Text.ShouldEndWith(" is received");
    }

    [Fact]
    public async Task a_wrapper_and_options_both_is_refused_before_the_act()
    {
        await Should.ThrowAsync<ArgumentException>(() =>
            WhenReceived(new ConfirmationOverdue(Guid.NewGuid()).DelayedFor(1.Hours()), options: new DeliveryOptions()));
    }

    [Fact]
    public async Task a_message_that_only_schedules_itself_fails_the_act_rather_than_passing()
    {
        Exception? thrown = null;
        var recording = await Recordings.RecordAsync(async () =>
        {
            try
            {
                await WhenReceived(new SnoozeReminder(Guid.NewGuid()));
                ThenNoMessageSent<EscalateUnconfirmedAppointment>();
            }
            catch (Exception e)
            {
                thrown = e;
            }
        });

        thrown.ShouldBeOfType<SpecCriticalException>()
            .Message.ShouldContain("SnoozeReminder was only scheduled for later, so its handler never ran");

        // The act's own line fails, and the negative step after it never gets to pass on nothing
        var when = recording.Steps.ShouldHaveSingleItem();
        when.Keyword.ShouldBe("When");
        when.Status.ShouldNotBe(ResultStatus.success);
    }
}

// GH-5076: what the act's tracked session handled, and the values of what it scheduled
[Collection(nameof(AppointmentsCollection))]
public class handled_and_scheduled_messages(AppointmentsHost app) : WolverineSpec(app.Host)
{
    private async Task<Guid> confirmedAppointment()
    {
        var id = Guid.NewGuid();
        await GivenEvents<Appointment>(id, new AppointmentScheduled(id, new Patient("Ann", new Address("Austin")), []));
        await WhenReceived(new ConfirmAppointment(id));
        return id;
    }

    [Fact]
    public async Task the_acts_own_message_and_its_cascades_are_handled()
    {
        await confirmedAppointment();

        ThenHandled<ConfirmAppointment>();
        ThenHandled(typeof(SendConfirmationEmail));
        ThenNotHandled<PatientArrived>();
        ThenNotHandled(typeof(EscalateUnconfirmedAppointment));
    }

    [Fact]
    public async Task a_wrong_handled_step_says_what_was_handled()
    {
        await confirmedAppointment();

        Should.Throw<SpecificationFailedException>(() => ThenHandled<PatientArrived>())
            .Message.ShouldContain("Expected a PatientArrived to be handled, but none was. Handled: ");
        Should.Throw<SpecificationFailedException>(() => ThenNotHandled<SendConfirmationEmail>())
            .Message.ShouldContain("Expected no SendConfirmationEmail to be handled, but it was handled 1 time");
    }

    [Fact]
    public async Task a_scheduled_message_is_not_handled_and_the_failure_says_so()
    {
        await WhenReceived(new AwaitConfirmation(Guid.NewGuid()));

        ThenNotHandled<ConfirmationOverdue>();
        Should.Throw<SpecificationFailedException>(() => ThenHandled<ConfirmationOverdue>())
            .Message.ShouldContain("SCHEDULED for later");
    }

    [Fact]
    public async Task the_handled_steps_render()
    {
        await confirmedAppointment();

        var recording = await Recordings.RecordAsync(() =>
        {
            ThenHandled<SendConfirmationEmail>();
            ThenNotHandled<PatientArrived>();
            return Task.CompletedTask;
        });

        recording.GatheredFailures().ShouldBeNull();
        recording.Steps.Select(x => $"{x.Keyword} {x.Text}").ShouldBe([
            "Then SendConfirmationEmail is handled",
            "And no PatientArrived is handled"
        ]);
    }

    [Fact]
    public async Task a_negative_handled_step_with_no_act_fails()
    {
        var scenario = new WolverineScenario(app.Host);
        Should.Throw<SpecificationFailedException>(() => scenario.ThenNotHandled<PatientArrived>())
            .Message.ShouldContain("no act has run");
    }

    [Fact]
    public async Task a_scheduled_message_is_judged_by_its_values()
    {
        var id = Guid.NewGuid();
        await WhenReceived(new AwaitConfirmation(id));

        ThenMessageScheduled(new ConfirmationOverdue(id));
        ThenMessageScheduled(Specify<ConfirmationOverdue>().With(x => x.AppointmentId, id));
        ThenMessageScheduled(Expect.Value(new ConfirmationOverdue(Guid.NewGuid())).Ignoring(x => x.AppointmentId));
        ThenMessageScheduled<ConfirmationOverdue>(4.Days());

        Should.Throw<SpecificationFailedException>(() => ThenMessageScheduled(new ConfirmationOverdue(Guid.NewGuid())))
            .Message.ShouldContain("No ConfirmationOverdue scheduled matched the expectation");
        Should.Throw<SpecificationFailedException>(() => ThenMessageScheduled(new EscalateUnconfirmedAppointment(id)))
            .Message.ShouldContain("Expected a EscalateUnconfirmedAppointment to be scheduled, but none was");
    }

    [Fact]
    public async Task a_sent_message_is_not_a_scheduled_one()
    {
        var id = await confirmedAppointment();

        Should.Throw<SpecificationFailedException>(() => ThenMessageScheduled(new SendConfirmationEmail(id, "Ann")))
            .Message.ShouldContain("one was SENT within the act: use ThenMessageSent");
    }

    [Fact]
    public async Task a_scheduled_value_renders_as_the_message()
    {
        var id = Guid.NewGuid();
        await WhenReceived(new AwaitConfirmation(id));

        var recording = await Recordings.RecordAsync(() =>
        {
            ThenMessageScheduled(Specify<ConfirmationOverdue>().With(x => x.AppointmentId, id));
            return Task.CompletedTask;
        });

        recording.GatheredFailures().ShouldBeNull();
        recording.Steps.Single().Text.ShouldStartWith("ConfirmationOverdue(AppointmentId: ");
        recording.Steps.Single().Text.ShouldEndWith(") is scheduled");
    }
}
