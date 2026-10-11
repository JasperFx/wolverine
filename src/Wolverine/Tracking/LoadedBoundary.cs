using JasperFx.Events.Tags;

namespace Wolverine.Tracking;

/// <summary>
///     GH-4940. One Dynamic Consistency Boundary a handler or HTTP endpoint loaded while a tracked session was
///     active: the <see cref="EventTagQuery" /> its <c>[DcbModel]</c> (or <c>[BoundaryModel]</c>) was fetched
///     over, which is also the query a concurrent append is checked against when the boundary is saved, the
///     model type it was projected into, and the message being handled. Recorded by Wolverine's generated code
///     for every DCB load, whichever event store serves it.
/// </summary>
public sealed class LoadedBoundary
{
    internal LoadedBoundary(Envelope? envelope, string? serviceName, Type modelType, EventTagQuery query)
    {
        Envelope = envelope;
        ServiceName = serviceName;
        ModelType = modelType;
        Query = query;
        Conditions = query.Conditions.ToArray();
    }

    /// <summary>
    ///     The message being handled when the boundary was loaded, or null for work with no incoming envelope,
    ///     such as an HTTP endpoint.
    /// </summary>
    public Envelope? Envelope { get; }

    /// <summary>The service that loaded the boundary.</summary>
    public string? ServiceName { get; }

    /// <summary>The model type the boundary's events were projected into.</summary>
    public Type ModelType { get; }

    /// <summary>The tag query the boundary was loaded over.</summary>
    public EventTagQuery Query { get; }

    /// <summary>
    ///     The query's conditions as they were when the boundary was loaded, OR'd together, so a query object
    ///     changed afterward does not change what was recorded.
    /// </summary>
    public IReadOnlyList<EventTagQueryCondition> Conditions { get; }

    /// <summary>
    ///     Whether an event of <paramref name="eventType" /> carrying <paramref name="tags" /> falls inside this
    ///     boundary: one of its conditions names a tag the event carries, and either no event type or the
    ///     event's own. Such an event, appended by someone else between this load and its save, is a conflict.
    /// </summary>
    public bool Selects(Type eventType, IEnumerable<object> tags)
    {
        ArgumentNullException.ThrowIfNull(eventType);
        ArgumentNullException.ThrowIfNull(tags);

        var carried = tags.ToArray();
        return Conditions.Any(condition =>
            (condition.EventType is null || condition.EventType == eventType)
            && carried.Any(tag => tag.GetType() == condition.TagType && Equals(tag, condition.TagValue)));
    }

    public override string ToString()
        => $"{Envelope?.MessageType ?? "(no envelope)"}: {ModelType.Name} over " +
           string.Join(" or ", Conditions.Select(x => x.EventType is null ? $"{x.TagValue}" : $"{x.EventType.Name} {x.TagValue}"));
}
