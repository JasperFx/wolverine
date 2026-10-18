using JasperFx.Events;

namespace Bobcat.CritterStack;

/// <summary>
/// What one act of a <see cref="CritterStackScenario" /> did: the events it appended, the streams it started,
/// and how it failed or was refused (bobcat#471).
/// </summary>
/// <remarks>
/// <para>
/// <b>Store terms only.</b> Everything here can be learned from the event store, so an application that uses
/// Marten, Polecat or Fisher without a message bus gets the whole assertion vocabulary.
/// <see cref="CritterStackScenario.WhenActing" /> fills it from the store's high-water mark before and after
/// the act. A scenario that knows more about an act — WolverineFx.Bobcat's, through Wolverine's tracked
/// session — derives from this record and fills it from what it saw instead.
/// </para>
/// </remarks>
/// <param name="NewEvents">
/// The events the act itself appended, in commit order. For a store-only act that is every event appended
/// while it ran on a stream the specification arranged, addressed or started (bobcat#503) — the rest are
/// <see cref="ForeignEvents" />; a scenario that can tell an act's own work from its cascades' puts only the
/// act's own here.
/// </param>
/// <param name="Error">The exception the act raised, when it raised one. Captured, never rethrown.</param>
/// <param name="Refusal">A refusal that is an answer rather than a failure, such as an HTTP endpoint's 4xx body.</param>
public record CritterStackActOutcome(
    IReadOnlyList<IEvent> NewEvents,
    Exception? Error,
    string? Refusal = null)
{
    /// <summary>An empty <see cref="StoreByEvent" />: nothing to tell apart. Declared before <see cref="None" />, which reads it.</summary>
    public static readonly IReadOnlyDictionary<IEvent, IEventStore> NoStores =
        new Dictionary<IEvent, IEventStore>(ReferenceEqualityComparer.Instance);

    /// <summary>Nothing has acted yet.</summary>
    public static readonly CritterStackActOutcome None = new(Array.Empty<IEvent>(), null);

    /// <summary>
    /// Every event appended while the act ran, on any stream, in commit order — its cascades' included,
    /// where the scenario can see them. What a stream already held never counts.
    /// </summary>
    public IReadOnlyList<IEvent> AllNewEvents { get; init; } = Array.Empty<IEvent>();

    /// <summary>
    /// bobcat#503. Events appended while a store-only act ran on streams that already existed and that the
    /// specification never arranged, addressed or started — most likely another test writing to the same store.
    /// Never judged by a whole-store assertion (<c>ThenEvents</c>, <c>ThenNoEvents</c>, …); a failing one says
    /// "cross-talk suspected" when this is not empty. A step that names a stream or a tag query still sees the
    /// ones it addresses. Always empty for a scenario that tracks its act, such as WolverineFx.Bobcat's.
    /// </summary>
    /// <remarks>
    /// A stream another test <em>started</em> during the act cannot be told from one the act started, so its
    /// events are in <see cref="NewEvents" />, not here.
    /// </remarks>
    public IReadOnlyList<IEvent> ForeignEvents { get; init; } = Array.Empty<IEvent>();

    /// <summary>The streams the act itself started, with the ids it assigned.</summary>
    public IReadOnlyList<StreamAction> StartedStreams { get; init; } = Array.Empty<StreamAction>();

    /// <summary>
    /// Every message the act caused to be sent, cascades included. Always empty for a store-only act:
    /// nothing tracked the messages. A scenario backed by a message bus fills it.
    /// </summary>
    public IReadOnlyList<object> MessagesSent { get; init; } = Array.Empty<object>();

    /// <summary>
    /// Whether the act's events carry the Dynamic Consistency Boundary tags they were appended with. False
    /// for a store-only act: its events are read back from the store, and no store reads tags back, so a
    /// step that judges tags says so rather than reporting every tagged event missing.
    /// </summary>
    public bool EventTagsKnown { get; init; } = true;

    /// <summary>
    /// bobcat#504. Which event store each event came from, when the act was observed on more than one — every
    /// event in <see cref="AllNewEvents" /> and <see cref="ForeignEvents" />, by reference. Empty when there was
    /// only one store to observe, and for a scenario that cannot tell (a tracked session that heard the commit).
    /// </summary>
    public IReadOnlyDictionary<IEvent, IEventStore> StoreByEvent { get; init; } = NoStores;

    /// <summary>bobcat#504. The store <paramref name="event" /> came from, or null when <see cref="StoreByEvent" /> does not say.</summary>
    public IEventStore? StoreOf(IEvent @event) => StoreByEvent.TryGetValue(@event, out var store) ? store : null;
}
