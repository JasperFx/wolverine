using JasperFx.Events.EventModeling;
using Wolverine.Attributes;
using Wolverine.Configuration.EventModeling;
using Wolverine.Persistence;
using Wolverine.Persistence.EventSourcing;
using Wolverine.Runtime.Handlers;
using Xunit;

namespace CoreTests.Acceptance.EventModel4386;

// GH-4386: the two shapes the Critter Stack's own scaffolding emits for a modelled slice --
// a collection of events (EventsToAppend / the store's Events) and a StartStream side effect --
// erase the element types, so the more idiomatically event-modelled an application is, the emptier
// its derived Event Model gets. [Emits] is how a handler names what its signature cannot.
public class emitted_events_a_signature_cannot_carry_4386
{
    private static HandlerChain chainFor<THandler>(System.Linq.Expressions.Expression<Action<THandler>> expression)
        => HandlerChain.For(expression, new HandlerGraph());

    [Fact]
    public void an_events_collection_return_still_erases_its_element_types()
    {
        // the state of play this issue is about, kept as the baseline: the chain is known to append
        // events and the events themselves are unreadable
        var slice = EventModelRoles.ForHandlerChain(
            chainFor<ConfirmAppointmentHandler>(x => ConfirmAppointmentHandler.Handle(null!, null!)));

        slice.AggregateTypes.Select(x => x.Name).ShouldBe(new[] { nameof(Appointment) });
        slice.EmittedEvents.ShouldBeEmpty();
    }

    [Fact]
    public void emits_puts_the_events_back_on_the_slice()
    {
        var slice = EventModelRoles.ForHandlerChain(
            chainFor<ConfirmAppointmentWithEmitsHandler>(x => ConfirmAppointmentWithEmitsHandler.Handle(null!, null!)));

        slice.EmittedEvents.Select(x => x.Name).ShouldBe(new[] { nameof(AppointmentConfirmed) });
        slice.AggregateTypes.Select(x => x.Name).ShouldBe(new[] { nameof(Appointment) });
    }

    [Fact]
    public void emits_is_additive_to_what_the_signature_already_says()
    {
        var slice = EventModelRoles.ForHandlerChain(
            chainFor<RescheduleAppointmentHandler>(x => RescheduleAppointmentHandler.Handle(null!, null!)));

        // the declaration is read off the handler first, then the signature's own typed return
        slice.EmittedEvents.Select(x => x.Name)
            .ShouldBe(new[] { nameof(AppointmentConfirmed), nameof(AppointmentRescheduled) });
    }

    [Fact]
    public void emits_on_the_handler_type_covers_its_methods()
    {
        var slice = EventModelRoles.ForHandlerChain(
            chainFor<CancelAppointmentHandler>(x => CancelAppointmentHandler.Handle(null!, null!)));

        slice.EmittedEvents.Select(x => x.Name).ShouldBe(new[] { nameof(AppointmentCancelled) });
    }

    [Fact]
    public void a_start_stream_side_effect_does_not_turn_a_cascaded_message_into_an_event()
    {
        // StartStream appends events that were built in the method body. The OTHER return value is a
        // cascaded message and stays one -- the same reasoning GH-4204 applied to IEventStream<T>.
        var slice = EventModelRoles.ForHandlerChain(
            chainFor<ProposeHomeCheckAppointmentHandler>(x => ProposeHomeCheckAppointmentHandler.Handle(null!)));

        slice.PublishedMessages.Select(x => x.Name).ShouldBe(new[] { nameof(HomeCheckAppointmentScheduled) });
        slice.EmittedEvents.ShouldBeEmpty();
    }

    [Fact]
    public void emits_names_the_events_a_start_stream_carries()
    {
        var slice = EventModelRoles.ForHandlerChain(
            chainFor<ProposeHomeCheckAppointmentWithEmitsHandler>(x => ProposeHomeCheckAppointmentWithEmitsHandler.Handle(null!)));

        slice.EmittedEvents.Select(x => x.Name).ShouldBe(new[] { nameof(HomeCheckAppointmentProposed) });
    }
}

#region sample types for GH-4386

public record ConfirmAppointment(Guid Id);

public record RescheduleAppointment(Guid Id);

public record CancelAppointment(Guid Id);

public record HomeCheckAssignmentAccepted(Guid Id);

public record AppointmentConfirmed(Guid Id);

public record AppointmentRescheduled(Guid Id);

public record AppointmentCancelled(Guid Id);

public record HomeCheckAppointmentProposed(Guid Id);

public record HomeCheckAppointmentScheduled(Guid Id);

public class Appointment
{
    public Guid Id { get; set; }

    public void Apply(AppointmentConfirmed e)
    {
    }
}

public class ConfirmAppointmentHandler
{
    public static EventsToAppend Handle(ConfirmAppointment command, [WriteModel] Appointment appointment)
        => new() { new AppointmentConfirmed(command.Id) };
}

public class ConfirmAppointmentWithEmitsHandler
{
    [Emits(typeof(AppointmentConfirmed))]
    public static EventsToAppend Handle(ConfirmAppointment command, [WriteModel] Appointment appointment)
        => new() { new AppointmentConfirmed(command.Id) };
}

public class RescheduleAppointmentHandler
{
    [Emits(typeof(AppointmentConfirmed))]
    public static AppointmentRescheduled Handle(RescheduleAppointment command, [WriteModel] Appointment appointment)
        => new(command.Id);
}

[Emits(typeof(AppointmentCancelled))]
public class CancelAppointmentHandler
{
    public static EventsToAppend Handle(CancelAppointment command, [WriteModel] Appointment appointment)
        => new() { new AppointmentCancelled(command.Id) };
}

// Excluded from discovery: a StartStream return makes SideEffectPolicy demand a registered event store
// at HandlerGraph.Compile(), and every other CoreTests fixture that scans this assembly would fail to
// boot. These chains are built directly, so nothing here needs to be discovered.
[WolverineIgnore]
public class ProposeHomeCheckAppointmentHandler
{
    public static (StartStream, HomeCheckAppointmentScheduled) Handle(HomeCheckAssignmentAccepted trigger)
        => (Storage.StartStream<Appointment>(trigger.Id, new HomeCheckAppointmentProposed(trigger.Id)),
            new HomeCheckAppointmentScheduled(trigger.Id));
}

[WolverineIgnore]
public class ProposeHomeCheckAppointmentWithEmitsHandler
{
    [Emits(typeof(HomeCheckAppointmentProposed))]
    public static StartStream Handle(HomeCheckAssignmentAccepted trigger)
        => Storage.StartStream<Appointment>(trigger.Id, new HomeCheckAppointmentProposed(trigger.Id));
}

#endregion
