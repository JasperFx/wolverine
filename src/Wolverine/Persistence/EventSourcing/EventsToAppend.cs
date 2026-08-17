using Wolverine.Configuration;

namespace Wolverine.Persistence.EventSourcing;

/// <summary>
///     Tells Wolverine handlers that this value contains a list of events to be appended to the
///     current stream — the store-agnostic sibling of <c>Wolverine.Marten.Events</c>,
///     <c>Wolverine.Polecat.Events</c> and <c>Wolverine.Fisher.Events</c>.
/// </summary>
/// <remarks>
///     <para>
///         The three store-specific types are identical and store-named, so a handler that wants to
///         be store-agnostic could not use any of them. The store-agnostic path did exist — a bare
///         <c>IReadOnlyList&lt;object&gt;</c> return is picked up by the
///         <c>IEnumerable&lt;object&gt;</c> fallback in
///         <see cref="EventSourcingFrameProviderExtensions.DetermineEventCaptureHandling" /> — but
///         that fallback is <b>positional and implicit</b>. Because <c>IEnumerable&lt;T&gt;</c> is
///         covariant, every reference-typed collection in a return tuple is castable to
///         <c>IEnumerable&lt;object&gt;</c>, so a handler returning
///         <c>(IReadOnlyList&lt;object&gt;, IReadOnlyList&lt;string&gt;)</c> has two candidates and
///         whichever lands first in <c>Creates</c> silently becomes the appended events. Nothing
///         fails at codegen and nothing fails at runtime; the wrong collection just ends up in the
///         event stream.
///     </para>
///     <para>
///         <c>OutgoingMessages</c> escapes that only because it is an
///         <see cref="IWolverineReturnType" />, which the fallback explicitly excludes — a happy
///         accident of an unrelated marker rather than a designed guarantee, and one that does not
///         extend to a user's own collection type. Returning this type instead makes the intent
///         declared, so <c>(EventsToAppend, OutgoingMessages)</c> is unambiguous.
///     </para>
///     <para>
///         The store-specific types stay exactly as they are for existing code, the same way
///         <c>WriteAggregateAttribute</c> was kept alongside <c>WriteModelAttribute</c> in GH-3907.
///     </para>
///     <para>
///         <b>Why this is not just called <c>Events</c>,</b> which would have mirrored the three
///         store-specific types: it cannot be. This type lives beside
///         <see cref="WriteModelAttribute" />, and <c>[WriteModel]</c> is what makes a
///         store-agnostic handler possible in the first place — so the handler that wants this type
///         imports <c>Wolverine.Persistence.EventSourcing</c> by necessity, and a real application
///         imports its store's <c>Wolverine.Marten</c> / <c>.Polecat</c> / <c>.Fisher</c> as well.
///         Naming this <c>Events</c> made those two imports collide with CS0104 <em>on the handler's
///         return type itself</em>, forcing a <c>using</c> alias onto precisely the code this exists
///         to serve. The name says when the append happens, which the bare noun did not.
///     </para>
///     <para>
///         Single-stream only: this is for the case where <c>[WriteModel]</c> pins the aggregate and
///         every event goes to its stream. Appending to a variable number of <em>different</em>
///         streams is a separate gap and is deliberately not addressed here.
///     </para>
/// </remarks>
public class EventsToAppend : List<object>, IWolverineReturnType
{
    public EventsToAppend()
    {
    }

    public EventsToAppend(IEnumerable<object> collection) : base(collection)
    {
    }

    public static EventsToAppend operator +(EventsToAppend events, object @event)
    {
        events.Add(@event);
        return events;
    }
}
