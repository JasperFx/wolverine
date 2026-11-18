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
/// <see cref="NotSupportedException" />. Phase 1 of GH-5415: nothing implements this yet.
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

/// <summary>GH-5415. Which page of a saga's history to read: 1-based, with the page size clamped to <see cref="MaxPageSize" />.</summary>
public sealed record SagaHistoryPageRequest(int Page = 1, int PageSize = 50)
{
    /// <summary>The largest page a history read returns</summary>
    public const int MaxPageSize = 500;
}

/// <summary>
/// GH-5415. One event of a saga's stream: its version in the stream, when it was recorded, the store's name for its
/// event type, the .NET type when the store can say, and the event body as JSON.
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
