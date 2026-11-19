using System.Text.Json;
using JasperFx.Descriptors;

namespace Wolverine.Persistence.Sagas;

/// <summary>
/// GH-5415. Reads a saga's history, for a saga whose storage keeps one: the events of an event-sourced saga's stream,
/// a page at a time, and the saga's state at a version or a point in time, through each store's own aggregation of a
/// stream to a version or timestamp (time travel). Implemented beside <see cref="ISagaStoreDiagnostics" /> by the
/// storages that can answer, and by Wolverine's aggregate of every saga storage, so
/// <c>IWolverineRuntime.SagaStorage as ISagaHistoryDiagnostics</c> is the one place to ask.
/// </summary>
/// <remarks>
/// Every implementation lives inside Wolverine, so members may be added here as CritterWatch needs them. Check
/// <c>SagaDescriptor.SupportsHistory</c> before calling: a saga type whose storage keeps no history is refused with
/// <see cref="NotSupportedException" />. GH-5665: a document saga that records its message history
/// (<see cref="RecordSagaHistoryAttribute" />) is read here too, one handled message to an entry, from the message store
/// rather than the saga's storage. It keeps no state, so <c>ReadSagaAtAsync</c> refuses it;
/// <c>SagaDescriptor.HistoryKind</c> says which kind of history a saga has.
/// </remarks>
public interface ISagaHistoryDiagnostics
{
    /// <summary>
    /// The events of the saga's stream, oldest first, one page at a time. An unknown saga instance reads as an empty
    /// page with a total count of 0.
    /// </summary>
    /// <param name="sagaTypeName">The saga type's full name in code, as <c>SagaDescriptor.StateType</c> names it.</param>
    /// <param name="identity">The saga identity, coerced as <see cref="ISagaStoreDiagnostics.ReadSagaAsync" /> coerces it.</param>
    /// <param name="page">Which page, and how many events to a page.</param>
    /// <param name="ct">Token to cancel the diagnostic call.</param>
    Task<SagaHistoryPage> ReadSagaHistoryAsync(string sagaTypeName, object identity, SagaHistoryPageRequest page,
        CancellationToken ct);

    /// <summary>
    /// The saga's state after the event at <paramref name="version" /> of its stream, or null when the stream has no
    /// such version.
    /// </summary>
    Task<SagaInstanceState?> ReadSagaAtAsync(string sagaTypeName, object identity, long version, CancellationToken ct);

    /// <summary>
    /// The saga's state as of <paramref name="timestamp" />: after every event of its stream recorded at or before it,
    /// or null when the stream had not started by then.
    /// </summary>
    Task<SagaInstanceState?> ReadSagaAtAsync(string sagaTypeName, object identity, DateTimeOffset timestamp,
        CancellationToken ct);
}

/// <summary>
/// GH-5415. Which page of a saga's history to read: 1-based, with the page size clamped to <see cref="MaxPageSize" />.
/// GH-5665: <paramref name="TenantId" /> names the tenant whose message store holds a document saga's message history;
/// null reads every message store.
/// </summary>
public sealed record SagaHistoryPageRequest(int Page = 1, int PageSize = 50, string? TenantId = null)
{
    /// <summary>The largest page a history read returns</summary>
    public const int MaxPageSize = 500;
}

/// <summary>
/// GH-5415. One event of a saga's stream: its version in the stream, when it was recorded, the store's name for its
/// event type, the .NET type when the store can say, and the event body as JSON.
/// GH-5665: in a document saga's message history an entry is one handled message instead: <c>Version</c> is its position
/// in the history (1 for the first), <c>EventTypeName</c> Wolverine's message type name, <c>DotNetTypeName</c> the
/// message's .NET type, and <c>Data</c> the whole <see cref="SagaHistoryRecord" /> (handler, outcome, sent messages).
/// </summary>
public sealed record SagaHistoryEvent(
    long Version,
    DateTimeOffset Timestamp,
    string EventTypeName,
    string? DotNetTypeName,
    JsonElement Data);

/// <summary>GH-5415. One page of a saga's history.</summary>
public sealed record SagaHistoryPage(
    string SagaTypeName,
    object Identity,
    IReadOnlyList<SagaHistoryEvent> Events,
    int Page,
    int PageSize,
    long TotalCount)
{
    /// <summary>Whether a later page has more events</summary>
    public bool HasMore => (long)Page * PageSize < TotalCount;
}
