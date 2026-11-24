using JasperFx.CodeGeneration.Model;
using JasperFx.Events;

namespace Wolverine.Persistence.EventSourcing;

/// <summary>
///     Everything a store needs in order to build the frame that loads an aggregate's event stream for
///     writing. Handed to <see cref="IEventSourcingFrameProvider.BuildLoadAggregateFrame"/>.
/// </summary>
/// <remarks>
///     This exists so that the seam is expressed entirely in public types. The shared workflow's own
///     bookkeeping (<c>AggregateHandling</c>) stays internal to Wolverine and free to move, while what
///     crosses into a store package is this narrow, stable request.
/// </remarks>
/// <param name="AggregateType">The aggregate (single stream projection) type being loaded.</param>
/// <param name="AggregateId">The variable holding the stream identity.</param>
/// <param name="LoadStyle">Optimistic or exclusive stream locking.</param>
/// <param name="Version">
///     The expected stream version for an optimistic concurrency check, when one was found. Null means
///     "no version check", and is always null when <paramref name="LoadStyle" /> is
///     <see cref="ModelConcurrencyStyle.Exclusive" />.
/// </param>
/// <param name="AlwaysEnforceConsistency">
///     Enforce the optimistic concurrency check at commit even when the handler appended no events.
/// </param>
/// <param name="IsNaturalKey">
///     True when <paramref name="AggregateId" /> is a natural key rather than the stream's own id, in
///     which case the identity is passed whole rather than unwrapped from a strong typed id.
/// </param>
public sealed record AggregateLoadRequest(
    Type AggregateType,
    Variable AggregateId,
    ModelConcurrencyStyle LoadStyle,
    Variable? Version,
    bool AlwaysEnforceConsistency,
    bool IsNaturalKey)
{
    /// <summary>
    ///     How the store the aggregate lives on identifies its streams. A store that has to start a new stream itself,
    ///     as Fisher does for a natural key no stream has yet (GH-5948), needs to know which kind of id to make.
    /// </summary>
    public StreamIdentity StreamIdentity { get; init; } = StreamIdentity.AsGuid;
}
