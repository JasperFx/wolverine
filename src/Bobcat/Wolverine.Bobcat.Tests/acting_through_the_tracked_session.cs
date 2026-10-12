using Bobcat;
using Bobcat.CritterStack;
using Bobcat.Engine;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Wolverine.Tracking;

namespace Wolverine.Bobcat.Tests;

// bobcat#471 step 2: the store-only acts WolverineScenario inherits from CritterStackScenario -- WhenActing and
// WhenActingOn -- run inside Wolverine's tracked session, and fall back to the store's high-water read when the
// tracked session heard no event commits at all
[Collection(nameof(AppointmentsCollection))]
public class acting_through_the_tracked_session(AppointmentsHost app) : WolverineSpec(app.Host)
{
    private static AppointmentScheduled scheduled(Guid id) => new(id, new Patient("Ann", new Address("Austin")), []);

    // A session Wolverine did not open, so the tracked session never hears it commit
    private static async Task appendOutsideWolverine(IServiceProvider services, Guid id, params object[] events)
    {
        await using var session = services.GetRequiredService<IDocumentStore>().LightweightSession();
        session.Events.Append(id, events);
        await session.SaveChangesAsync();
    }

    [Fact]
    public async Task an_act_that_goes_through_the_bus_is_judged_on_what_the_tracked_session_saw()
    {
        var theAppointment = Guid.NewGuid();
        await GivenEvents<Appointment>(theAppointment, scheduled(theAppointment));

        var recording = await Recordings.RecordAsync(async () =>
        {
            await WhenActing("the front desk confirms the appointment",
                services => services.GetRequiredService<IMessageBus>().InvokeAsync(new ConfirmAppointment(theAppointment)));
            ThenEvents(typeof(AppointmentConfirmed));
            ThenMessageSent<SendConfirmationEmail>();
        });

        recording.GatheredFailures().ShouldBeNull();
        recording.Steps.Select(x => $"{x.Keyword} {x.Text}").ShouldBe([
            "When the front desk confirms the appointment",
            "Then AppointmentConfirmed is emitted",
            "And SendConfirmationEmail is sent"
        ]);

        LastAct.Session.ShouldNotBeNull();
        LastAct.Appended.ShouldNotBeEmpty();

        // Heard from the tracked session, so the events keep what they were appended with
        LastAct.EventTagsKnown.ShouldBeTrue();
    }

    [Fact]
    public async Task events_appended_outside_the_tracked_session_still_count()
    {
        var theAppointment = Guid.NewGuid();
        await GivenEvents<Appointment>(theAppointment, scheduled(theAppointment));

        var recording = await Recordings.RecordAsync(async () =>
        {
            await WhenActing("the appointment is confirmed straight through the store",
                services => appendOutsideWolverine(services, theAppointment,
                    new AppointmentConfirmed(theAppointment, DateTimeOffset.UtcNow)));
            ThenEvents(typeof(AppointmentConfirmed));
            ThenNoMessageSent<SendConfirmationEmail>();
        });

        recording.GatheredFailures().ShouldBeNull();

        // The tracked session heard nothing, so the act read what the store gained past its high-water mark
        LastAct.Appended.ShouldBeEmpty();
        LastAct.NewEvents.Select(x => x.Data).ShouldHaveSingleItem().ShouldBeOfType<AppointmentConfirmed>();
        LastAct.AllNewEvents.Count.ShouldBe(1);

        // Read back from the store, which returns no tags
        LastAct.EventTagsKnown.ShouldBeFalse();
    }

    [Fact]
    public async Task a_stream_started_outside_the_tracked_session_is_started_with_its_aggregate_type()
    {
        var theAppointment = Guid.NewGuid();

        await WhenActing("the appointment is booked straight through the store", async services =>
        {
            await using var session = services.GetRequiredService<IDocumentStore>().LightweightSession();
            session.Events.StartStream<Appointment>(theAppointment, scheduled(theAppointment));
            await session.SaveChangesAsync();
        });

        await ThenStreamIsStarted<Appointment>(theAppointment);
        TheStartedStream<Appointment>().ShouldBe(theAppointment);
    }

    [Fact]
    public async Task the_fallback_is_only_taken_when_the_tracked_session_heard_no_commit_at_all()
    {
        var theAppointment = Guid.NewGuid();
        var another = Guid.NewGuid();
        await GivenEvents<Appointment>(theAppointment, scheduled(theAppointment));
        await GivenEvents<Appointment>(another, scheduled(another));

        await WhenActing("one appointment is confirmed through the bus, another straight through the store", async services =>
        {
            await services.GetRequiredService<IMessageBus>().InvokeAsync(new ConfirmAppointment(theAppointment));
            await appendOutsideWolverine(services, another, new AppointmentConfirmed(another, DateTimeOffset.UtcNow));
        });

        // What the tracked session heard is the act's; the store's other appends are never read
        LastAct.NewEvents.ShouldHaveSingleItem().StreamId.ShouldBe(theAppointment);
        LastAct.EventTagsKnown.ShouldBeTrue();
    }

    [Fact]
    public async Task acting_on_a_named_stream_through_the_bus()
    {
        var theAppointment = Guid.NewGuid();
        await GivenEvents<Appointment>(theAppointment, scheduled(theAppointment));

        var recording = await Recordings.RecordAsync(() =>
            WhenActingOn<Appointment>(theAppointment, "the front desk confirms it",
                services => services.GetRequiredService<IMessageBus>().InvokeAsync(new ConfirmAppointment(theAppointment)),
                Specify<AppointmentConfirmed>().With(x => x.AppointmentId, theAppointment)));

        recording.GatheredFailures().ShouldBeNull();
        LastAct.Session.ShouldNotBeNull();
        LastAct.MessagesSent.OfType<SendConfirmationEmail>().ShouldHaveSingleItem();
    }

    [Fact]
    public async Task acting_on_a_named_stream_straight_through_the_store()
    {
        var theAppointment = Guid.NewGuid();
        await GivenEvents<Appointment>(theAppointment, scheduled(theAppointment));

        var recording = await Recordings.RecordAsync(() =>
            WhenActingOn<Appointment>(theAppointment,
                services => appendOutsideWolverine(services, theAppointment,
                    new AppointmentConfirmed(theAppointment, DateTimeOffset.UtcNow)),
                Specify<AppointmentConfirmed>().With(x => x.AppointmentId, theAppointment)));

        recording.GatheredFailures().ShouldBeNull();
        LastAct.NewEvents.ShouldHaveSingleItem().Data.ShouldBeOfType<AppointmentConfirmed>();
    }

    [Fact]
    public async Task an_act_that_throws_is_captured_and_its_inner_exception_is_the_refusal()
    {
        await WhenActing("the sweep runs", _ =>
            throw new InvalidOperationException("The sweep failed", new ArgumentException("the clinic is closed")));

        LastAct.Error.ShouldNotBeNull();
        ThenRefusedWith("the clinic is closed");

        // Ordinal, as the base compares a reason
        Should.Throw<SpecificationFailedException>(() => ThenRefusedWith("The Clinic Is Closed"));
    }
}

// bobcat#471 step 2: what Wolverine's overrides must keep of the base's behaviour
[Collection(nameof(AppointmentsCollection))]
public class the_base_behaviour_through_the_wolverine_overrides(AppointmentsHost app)
{
    private static AppointmentScheduled scheduled(Guid id) => new(id, new Patient("Ann", new Address("Austin")), []);

    [Fact]
    public void a_projection_wait_defaults_to_the_bases_twenty_seconds()
        => new WolverineScenario(app.Host).ProjectionTimeout.ShouldBe(TimeSpan.FromSeconds(20));

    [Fact]
    public async Task what_an_act_touched_reaches_the_recording()
    {
        var scenario = new WolverineScenario(app.Host);
        var id = Guid.NewGuid();
        await scenario.GivenEvents<Appointment>(id, scheduled(id));

        var recording = await Recordings.RecordAsync(() => scenario.WhenReceived(new ConfirmAppointment(id)));

        // The command dispatched, the event appended and the message cascaded, which travel on scenario_finished
        recording.TouchedTypes.ShouldContain(typeof(ConfirmAppointment));
        recording.TouchedTypes.ShouldContain(typeof(AppointmentConfirmed));
        recording.TouchedTypes.ShouldContain(typeof(SendConfirmationEmail));
    }

    [Fact]
    public async Task what_an_act_appended_outside_the_tracked_session_reaches_the_recording_too()
    {
        var scenario = new WolverineScenario(app.Host);
        var id = Guid.NewGuid();

        var recording = await Recordings.RecordAsync(() => scenario.WhenActing("booked straight through the store",
            async services =>
            {
                await using var session = services.GetRequiredService<IDocumentStore>().LightweightSession();
                session.Events.StartStream<Appointment>(id, scheduled(id));
                await session.SaveChangesAsync();
            }));

        recording.TouchedTypes.ShouldContain(typeof(AppointmentScheduled));
    }

    [Fact]
    public async Task a_negative_message_step_with_no_act_fails()
    {
        var scenario = new WolverineScenario(app.Host);

        var recording = await Recordings.RecordAsync(() =>
        {
            scenario.ThenNoMessageSent<SendConfirmationEmail>();
            scenario.ThenNoMessageScheduled<SendConfirmationEmail>();
            scenario.ThenMessageSent<SendConfirmationEmail>();
            return Task.CompletedTask;
        });

        recording.Steps.Count.ShouldBe(3);
        recording.Steps.ShouldAllBe(x => x.Status == ResultStatus.failed);
        recording.GatheredFailures().ShouldNotBeNull().ShouldContain("no act has run");

        // and outside a recording, thrown
        Should.Throw<SpecificationFailedException>(() => scenario.ThenNoMessageSent<SendConfirmationEmail>());
    }

    [Fact]
    public async Task a_reset_scenario_has_no_act_again()
    {
        var scenario = new WolverineScenario(app.Host);
        await scenario.WhenPublished(new PatientArrived(Guid.NewGuid()));
        scenario.ThenNoMessageSent<SendConfirmationEmail>();

        await scenario.ResetAsync(TestContext.Current.CancellationToken);

        Should.Throw<SpecificationFailedException>(() => scenario.ThenNoMessageSent<SendConfirmationEmail>())
            .Message.ShouldContain("no act has run");
    }

    [Fact]
    public async Task an_act_step_that_throws_fails_its_own_line()
    {
        var scenario = new ThrowingScenario(app.Host);

        await Should.ThrowAsync<InvalidOperationException>(() => Recordings.RecordAsync(() => scenario.ActThenThrow()));

        // RecordAsync rethrew, so read the step off the recording the scenario saw open
        scenario.Recording.ShouldNotBeNull().Steps.ShouldHaveSingleItem().Status.ShouldNotBe(ResultStatus.success);
    }

    // An act whose completion throws: the step must not end as a pass
    private class ThrowingScenario(Microsoft.Extensions.Hosting.IHost host) : WolverineScenario(host)
    {
        public ScenarioRecorder.Recording? Recording { get; private set; }

        public Task ActThenThrow()
        {
            Recording = ScenarioRecorder.Current;
            return ActAsync("the act completes badly",
                tracking => tracking.ExecuteAndWaitAsync((Func<IMessageContext, Task>)(_ => Task.CompletedTask)),
                _ => throw new InvalidOperationException("the completion broke"));
        }
    }
}
