using JasperFx.Events;

namespace Wolverine.Tracking;

/// <summary>
///     GH-4931. What one event-store session committed while a tracked session was active: the streams it
///     started or appended to, each with its events, and the message being handled when it committed. Read
///     from the session itself rather than the store, so concurrent work never shows up here, and the events
///     keep what they carried when they were appended -- their tags, for one, which no store reads back.
/// </summary>
public sealed class AppendedEvents
{
    internal AppendedEvents(Envelope? envelope, string? serviceName, IReadOnlyList<StreamAction> streams)
    {
        Envelope = envelope;
        ServiceName = serviceName;
        Streams = streams;
    }

    /// <summary>
    ///     The message being handled when the session committed, or null for work with no incoming envelope,
    ///     such as an HTTP endpoint.
    /// </summary>
    public Envelope? Envelope { get; }

    /// <summary>
    ///     GH-4977. The event store the session belongs to -- the main store or an ancillary one, such as Marten's
    ///     <c>AddMartenStore&lt;IInvoicingStore&gt;()</c> -- when the store integration says. Null when it does not.
    /// </summary>
    public EventStoreIdentity? Store { get; internal init; }

    /// <summary>The service that committed the session.</summary>
    public string? ServiceName { get; }

    /// <summary>Every stream the session started or appended to.</summary>
    public IReadOnlyList<StreamAction> Streams { get; }

    /// <summary>The streams the session started, which is how a test learns an id the handler assigned.</summary>
    public IEnumerable<StreamAction> StartedStreams => Streams.Where(x => x.ActionType == StreamActionType.Start);

    /// <summary>Every event the session appended, stream by stream.</summary>
    public IEnumerable<IEvent> Events => Streams.SelectMany(x => x.Events);

    public override string ToString()
        => $"{Envelope?.MessageType ?? "(no envelope)"}: {string.Join(", ", Events.Select(x => x.Data.GetType().Name))}";
}
