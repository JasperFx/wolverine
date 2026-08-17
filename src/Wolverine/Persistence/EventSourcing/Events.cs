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
///         declared, so <c>(Events, OutgoingMessages)</c> is unambiguous.
///     </para>
///     <para>
///         The store-specific types stay exactly as they are for existing code, the same way
///         <c>WriteAggregateAttribute</c> was kept alongside <c>WriteModelAttribute</c> in GH-3907.
///     </para>
///     <para>
///         Single-stream only: this is for the case where <c>[WriteModel]</c> pins the aggregate and
///         every event goes to its stream. Appending to a variable number of <em>different</em>
///         streams is a separate gap and is deliberately not addressed here.
///     </para>
/// </remarks>
public class Events : List<object>, IWolverineReturnType
{
    public Events()
    {
    }

    public Events(IEnumerable<object> collection) : base(collection)
    {
    }

    public static Events operator +(Events events, object @event)
    {
        events.Add(@event);
        return events;
    }
}
