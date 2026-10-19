using Bobcat;
using Bobcat.Engine;
using Bobcat.Partial;
using JasperFx.Core;
using Microsoft.Extensions.DependencyInjection;
using Wolverine.Tracking;

namespace Wolverine.Bobcat.Tests;

// GH-5077: WhenReceived sends a message exactly as Wolverine does, so a TimeoutMessage or a delayed message is only
// scheduled. A step about what its handler did fails rather than passing on nothing, and WhenScheduledMessagesAreExecuted
// runs it as the next act
[Collection(nameof(AppointmentsCollection))]
public class scheduled_messages(AppointmentsHost app) : WolverineSpec(app.Host)
{
    private const string guard =
        "The act's ConfirmationOverdue was only scheduled for later, so no handler ran and nothing this step checks could " +
        "have happened. To run it, add `await WhenScheduledMessagesAreExecuted();` after the act.";

    [Fact]
    public async Task a_received_timeout_message_is_only_scheduled_and_a_negative_step_fails_saying_so()
    {
        var id = Guid.NewGuid();
        await WhenReceived(new ConfirmationOverdue(id));

        LastAct.OnlyScheduled.ShouldHaveSingleItem().ShouldBeOfType<ConfirmationOverdue>();
        ThenMessageScheduled<ConfirmationOverdue>(4.Days());

        // The false green of GH-5077, gone
        Should.Throw<SpecificationFailedException>(() => ThenNoMessageSent<EscalateUnconfirmedAppointment>())
            .Message.ShouldBe($"{guard} Activity: 0 sent, 1 scheduled, 0 executed, 0 failed — see the Message activity report");
        Should.Throw<SpecificationFailedException>(() => ThenNotHandled<EscalateUnconfirmedAppointment>())
            .Message.ShouldStartWith(guard);
        Should.Throw<SpecificationFailedException>(() => ThenNoMessageScheduled<EscalateUnconfirmedAppointment>())
            .Message.ShouldStartWith(guard);

        // A positive step that fails carries the same hint
        Should.Throw<SpecificationFailedException>(() => ThenMessageSent<EscalateUnconfirmedAppointment>())
            .Message.ShouldBe($"Expected a EscalateUnconfirmedAppointment to be sent, but none was. {guard} " +
                              "Activity: 0 sent, 1 scheduled, 0 executed, 0 failed — see the Message activity report");
        Should.Throw<SpecificationFailedException>(() => ThenHandled<ConfirmationOverdue>())
            .Message.ShouldBe($"Expected a ConfirmationOverdue to be handled successfully, but it never ran. {guard} " +
                              "Activity: 0 sent, 1 scheduled, 0 executed, 0 failed — see the Message activity report");
    }

    [Fact]
    public async Task executing_the_scheduled_messages_runs_the_timeout_as_the_next_act()
    {
        var id = Guid.NewGuid();
        await WhenReceived(new ConfirmationOverdue(id));
        var scheduledAct = LastAct;

        await WhenScheduledMessagesAreExecuted();

        LastAct.ShouldNotBeSameAs(scheduledAct);
        LastAct.OnlyScheduled.ShouldBeEmpty();
        LastAct.CorrelationId.ShouldBe(scheduledAct.CorrelationId);

        ThenHandled<ConfirmationOverdue>();
        ThenMessageSent<EscalateUnconfirmedAppointment>();
        ThenMessageSent(new EscalateUnconfirmedAppointment(id));
        Should.Throw<SpecificationFailedException>(() => ThenNoMessageSent<EscalateUnconfirmedAppointment>())
            .Message.ShouldStartWith("Expected no EscalateUnconfirmedAppointment but 1 were sent, to local://");
    }

    [Fact]
    public async Task the_replay_renders_as_a_when_naming_what_was_scheduled()
    {
        var id = Guid.NewGuid();
        var recording = await Recordings.RecordAsync(async () =>
        {
            await WhenReceived(new AwaitConfirmation(id));
            ThenMessageScheduled(new ConfirmationOverdue(id));
            await WhenScheduledMessagesAreExecuted();
            ThenMessageSent<EscalateUnconfirmedAppointment>();
        });

        recording.GatheredFailures().ShouldBeNull();
        var replay = recording.Steps[2];
        replay.Text.ShouldBe("the scheduled ConfirmationOverdue is executed");
        replay.Keyword.ShouldBe("When");

        // Both acts' activity is in the report a failure would show
        var activity = recording.Reports.OfType<MessageActivityReport>().Single();
        activity.Cells.ShouldContain(x => x.Name == "event" && x.DisplayText.Contains("cheduled"));
        activity.Cells.ShouldContain(x => x.Name == "message" && x.DisplayText == nameof(EscalateUnconfirmedAppointment));
    }

    [Fact]
    public async Task a_published_timeout_message_is_only_scheduled_too_until_it_is_executed()
    {
        var id = Guid.NewGuid();
        await WhenPublished(new ConfirmationOverdue(id));

        Should.Throw<SpecificationFailedException>(() => ThenNoMessageSent<EscalateUnconfirmedAppointment>())
            .Message.ShouldStartWith(guard);

        await WhenScheduledMessagesAreExecuted();
        ThenMessageSent(new EscalateUnconfirmedAppointment(id));
        Should.Throw<SpecificationFailedException>(() => ThenNoMessageSent<EscalateUnconfirmedAppointment>());
    }

    [Fact]
    public async Task what_the_acts_own_code_scheduled_is_its_own_message_too()
    {
        var id = Guid.NewGuid();
        using var scope = app.Host.Services.CreateScope();
        var bus = scope.ServiceProvider.GetRequiredService<IMessageBus>();

        await WhenTracked("the escalation is scheduled", () => bus.ScheduleAsync(new EscalateUnconfirmedAppointment(id), 1.Hours()).AsTask());

        LastAct.OnlyScheduled.ShouldHaveSingleItem().ShouldBeOfType<EscalateUnconfirmedAppointment>();
        Should.Throw<SpecificationFailedException>(() => ThenNotHandled<EscalateUnconfirmedAppointment>())
            .Message.ShouldStartWith("The act's EscalateUnconfirmedAppointment was only scheduled for later");

        await WhenScheduledMessagesAreExecuted();
        ThenHandled<EscalateUnconfirmedAppointment>();
    }

    [Fact]
    public async Task a_handler_scheduling_a_timeout_is_its_output_not_an_unhandled_act()
    {
        // AwaitConfirmation's handler ran; the timeout it scheduled is what it did, so negative steps judge normally
        await WhenReceived(new AwaitConfirmation(Guid.NewGuid()));

        LastAct.OnlyScheduled.ShouldBeEmpty();
        ThenNoMessageSent<EscalateUnconfirmedAppointment>();
        ThenNotHandled<ConfirmationOverdue>();
    }

    [Fact]
    public async Task with_no_act_before_it_the_replay_fails()
    {
        var thrown = await Should.ThrowAsync<SpecCriticalException>(() => WhenScheduledMessagesAreExecuted());
        thrown.Message.ShouldContain("There is no act whose scheduled messages could be executed");
    }

    [Fact]
    public async Task with_nothing_scheduled_the_replay_fails_saying_what_the_act_did()
    {
        var id = Guid.NewGuid();
        await GivenEvents<Appointment>(id, new AppointmentScheduled(id, new Patient("Ann", new Address("Austin")), []));
        await WhenReceived(new ConfirmAppointment(id));

        var thrown = await Should.ThrowAsync<SpecCriticalException>(() => WhenScheduledMessagesAreExecuted());
        thrown.Message.ShouldStartWith("The last act scheduled nothing, so there is nothing to execute. Activity: 2 sent, 0 scheduled, ");
    }

    [Fact]
    public async Task a_delayed_wrapper_is_scheduled_and_the_guard_names_the_message_inside_it()
    {
        await WhenReceived(new EscalateUnconfirmedAppointment(Guid.NewGuid()).DelayedFor(1.Hours()));

        LastAct.OnlyScheduled.ShouldHaveSingleItem().ShouldBeOfType<EscalateUnconfirmedAppointment>();
        Should.Throw<SpecificationFailedException>(() => ThenNotHandled<EscalateUnconfirmedAppointment>())
            .Message.ShouldStartWith("The act's EscalateUnconfirmedAppointment was only scheduled for later");

        await WhenScheduledMessagesAreExecuted();
        ThenHandled<EscalateUnconfirmedAppointment>();
    }

    [Fact]
    public async Task a_delay_in_the_delivery_options_schedules_it_too()
    {
        await WhenReceived(new EscalateUnconfirmedAppointment(Guid.NewGuid()),
            options: new DeliveryOptions { ScheduleDelay = 1.Hours() });

        LastAct.OnlyScheduled.ShouldNotBeEmpty();
        await WhenScheduledMessagesAreExecuted();
        ThenHandled<EscalateUnconfirmedAppointment>();
    }

    [Fact]
    public async Task sent_and_scheduled_never_count_each_other()
    {
        var id = Guid.NewGuid();
        await WhenReceived(new AwaitConfirmation(id));

        // A handler's ordinary act: its own message is handled, so no guard
        LastAct.OnlyScheduled.ShouldBeEmpty();
        ThenNoMessageSent<ConfirmationOverdue>();
        TheMessagesSent.ShouldNotContain(x => x is ConfirmationOverdue);
        LastAct.MessagesSent.ShouldNotContain(x => x is ConfirmationOverdue);
        Should.Throw<SpecificationFailedException>(() => ThenMessageSent<ConfirmationOverdue>())
            .Message.ShouldContain("(one was SCHEDULED for later: use ThenMessageScheduled)");
        Should.Throw<SpecificationFailedException>(() => ThenMessageSent(new ConfirmationOverdue(id)))
            .Message.ShouldContain("(one was SCHEDULED for later: use ThenMessageScheduled)");
    }

    [Fact]
    public async Task the_scheduled_set_is_judged_as_a_whole()
    {
        var id = Guid.NewGuid();
        await WhenReceived(new AwaitConfirmation(id));

        ThenMessagesScheduled(new ConfirmationOverdue(id));
        ThenMessagesScheduled(typeof(ConfirmationOverdue));
        ThenMessagesScheduled(Specify<ConfirmationOverdue>().With(x => x.AppointmentId, id));
        ThenMessagesScheduled(PartialObjects.FromTable(typeof(ConfirmationOverdue), $"""
                                                                                   | AppointmentId |
                                                                                   | {id}          |
                                                                                   """));

        Should.Throw<SpecificationFailedException>(() =>
                ThenMessagesScheduled(new ConfirmationOverdue(id), typeof(EscalateUnconfirmedAppointment)))
            .Message.ShouldContain("Activity: 1 sent, 1 scheduled, 1 executed, 0 failed — see the Message activity report");
        Should.Throw<ArgumentException>(() => ThenMessagesScheduled());
    }

    [Fact]
    public async Task a_failed_message_step_carries_the_message_activity_report()
    {
        var recording = await Recordings.RecordAsync(async () =>
        {
            await WhenReceived(new ConfirmationOverdue(Guid.NewGuid()));
            ThenNoMessageSent<EscalateUnconfirmedAppointment>();
        });

        recording.GatheredFailures().ShouldNotBeNull().ShouldContain(guard);
        var activity = recording.Reports.OfType<MessageActivityReport>().Single();
        activity.Visibility.ShouldBe(ReportVisibility.OnFailure);
        activity.Cells.ShouldContain(x => x.Name == "message" && x.DisplayText == nameof(ConfirmationOverdue));
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
    public async Task a_message_that_never_ran_says_so()
    {
        await confirmedAppointment();

        Should.Throw<SpecificationFailedException>(() => ThenHandled<PatientArrived>())
            .Message.ShouldBe("Expected a PatientArrived to be handled successfully, but it never appeared in the act's message " +
                              "activity. Activity: 2 sent, 0 scheduled, 2 executed, 0 failed — see the Message activity report");
    }

    [Fact]
    public async Task a_message_whose_handler_ran_is_not_unhandled_and_the_failure_says_how_it_went()
    {
        await confirmedAppointment();

        Should.Throw<SpecificationFailedException>(() => ThenNotHandled<SendConfirmationEmail>())
            .Message.ShouldStartWith("Expected no SendConfirmationEmail to be handled, but its handler ran once: 1 succeeded, 0 failed. Activity: ");
    }

    [Fact]
    public async Task every_attempt_failing_is_named_attempt_by_attempt()
    {
        var id = Guid.NewGuid();
        await GivenEvents<Appointment>(id, new AppointmentScheduled(id, new Patient("Ann", new Address("Austin")), []),
            new AppointmentConfirmed(id, DateTimeOffset.UtcNow));
        await WhenReceived(new ConfirmAppointment(id));

        var failure = Should.Throw<SpecificationFailedException>(() => ThenHandled<ConfirmAppointment>()).Message;
        failure.ShouldStartWith("Expected a ConfirmAppointment to be handled successfully, but it ran once and no attempt succeeded: " +
                                "attempt 1 threw AppointmentAlreadyConfirmed: The appointment is already confirmed.");
        failure.ShouldContain("It was moved to the error queue.");
        failure.ShouldEndWith("Activity: 1 sent, 0 scheduled, 1 executed, 1 failed — see the Message activity report");

        // A failed attempt is still a handler that ran
        Should.Throw<SpecificationFailedException>(() => ThenNotHandled<ConfirmAppointment>())
            .Message.ShouldContain("its handler ran once: 0 succeeded, 1 failed (attempt 1 threw AppointmentAlreadyConfirmed: The appointment is already confirmed)");
    }

    [Fact]
    public async Task another_messages_exception_is_listed_beside_the_story()
    {
        var id = Guid.NewGuid();
        await GivenEvents<Appointment>(id, new AppointmentScheduled(id, new Patient("Ann", new Address("Austin")), []),
            new AppointmentConfirmed(id, DateTimeOffset.UtcNow));
        await WhenReceived(new ConfirmAppointment(id));

        Should.Throw<SpecificationFailedException>(() => ThenHandled<PatientArrived>())
            .Message.ShouldContain("but it never appeared in the act's message activity. Other exceptions during the act: " +
                                   "ConfirmAppointment threw AppointmentAlreadyConfirmed: The appointment is already confirmed.");
    }

    [Fact]
    public async Task a_message_with_no_route_says_so()
    {
        var id = Guid.NewGuid();
        await WhenPublished(new AppointmentConfirmed(id, DateTimeOffset.UtcNow));

        Should.Throw<SpecificationFailedException>(() => ThenHandled<AppointmentConfirmed>())
            .Message.ShouldContain("but it never ran: Wolverine found no route or handler for it.");
    }

    [Fact]
    public async Task a_scheduled_message_is_not_handled_and_the_failure_says_so()
    {
        await WhenReceived(new AwaitConfirmation(Guid.NewGuid()));

        ThenNotHandled<ConfirmationOverdue>();
        Should.Throw<SpecificationFailedException>(() => ThenHandled<ConfirmationOverdue>())
            .Message.ShouldContain("but it never ran: it was SCHEDULED for later, and a scheduled message is not handled within " +
                                   "the act. To run it, add `await WhenScheduledMessagesAreExecuted();` after the act.");
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
    public void a_negative_handled_step_with_no_act_fails()
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
        Should.Throw<SpecificationFailedException>(() => ThenMessagesScheduled(typeof(SendConfirmationEmail)));
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
