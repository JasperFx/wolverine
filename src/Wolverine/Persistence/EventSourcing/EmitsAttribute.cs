using JasperFx.Events.EventModeling;

namespace Wolverine.Persistence.EventSourcing;

/// <summary>
///     <b>Purely diagnostic — this attribute changes no behavior.</b> Nothing about dispatch, code
///     generation or persistence reads it; it only tells Wolverine's derived Event Model which events a
///     handler appends when the signature cannot say so (GH-4386, GH-4890). Adding, removing or getting it
///     wrong never changes what the handler does.
/// </summary>
/// <remarks>
///     <para>
///         Use it where the events are constructed in the method body and returned through
///         <see cref="EventsToAppend" />, the store's <c>Events</c> collection, <see cref="StartStream" />,
///         <see cref="AppendEvents" /> or an <c>IEventStream&lt;T&gt;</c>. A handler that returns its event
///         type directly — <c>public static OrderShipped? Handle(ShipOrder command, [WriteModel] Order order)</c>
///         — needs none: the signature already says it.
///     </para>
///     <para>
///         Wolverine's Event Model is <em>derived</em>: the roles of a slice are read off the handler
///         signature, so a typed event return is reported without anything being declared. The two shapes
///         the Critter Stack's own scaffolding emits for a modelled slice are the exception —
///     </para>
///     <code>
///     public static (ConfirmAppointmentResponse, EventsToAppend) Post(ConfirmAppointmentRequest request, [WriteModel] Appointment appointment)
///     public static StartStream Handle(HomeCheckAssignmentAccepted trigger)
///     </code>
///     <para>
///         — because a collection of events and a stream side effect both erase the element types. The
///         slice is known to append events and the events themselves are unreadable, so the more
///         idiomatically event-modelled an application is, the emptier its derived model gets. That is not
///         a gap reflection can close: the types genuinely are not on the signature.
///     </para>
///     <para>
///         This attribute is the cheap, honest way to put the fact back where the code is. It is
///         <b>additive</b> — the types named here are unioned with whatever the signature already says —
///         and it is purely diagnostic: nothing about dispatch, codegen or persistence reads it, so a
///         wrong or stale declaration costs an <see cref="EventModelProvenance.Derived" /> claim that
///         disagrees with the board, which is exactly the drift the merge exists to surface.
///     </para>
///     <para>
///         Put it on the handler method, or on the handler type when every method of it emits the same
///         events. Repeat it freely; the declarations accumulate.
///     </para>
/// </remarks>
/// <example>
///     <code>
///     [Emits(typeof(AppointmentConfirmed))]
///     public static (ConfirmAppointmentResponse, EventsToAppend) Post(
///         ConfirmAppointmentRequest request, [WriteModel] Appointment appointment)
///     {
///         var events = new EventsToAppend { new AppointmentConfirmed(request.Id) };
///         return (new ConfirmAppointmentResponse(request.Id), events);
///     }
///     </code>
/// </example>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class, AllowMultiple = true, Inherited = true)]
public sealed class EmitsAttribute : Attribute
{
    public EmitsAttribute(params Type[] eventTypes)
    {
        EventTypes = eventTypes ?? Array.Empty<Type>();
    }

    /// <summary>The event types this handler appends, in declaration order.</summary>
    public IReadOnlyList<Type> EventTypes { get; }
}
