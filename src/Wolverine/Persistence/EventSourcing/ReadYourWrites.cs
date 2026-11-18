using System.ComponentModel;
using System.Diagnostics;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using JasperFx.Events;
using JasperFx.Events.Daemon;
using Microsoft.Extensions.Logging;
using Wolverine.Runtime;

namespace Wolverine.Persistence.EventSourcing;

/// <summary>
///     GH-5401: read-your-writes for asynchronous projections. The header names, and the waits that the code
///     generated for <see cref="WaitForProjectionAttribute{T}" /> calls.
/// </summary>
public static class ReadYourWrites
{
    /// <summary>
    ///     The position of the events a request or message appended, as <see cref="EventStorePosition.ToString" />
    ///     writes it. An HTTP endpoint with <c>[WaitForProjection&lt;T&gt;]</c> returns it, and an endpoint or
    ///     message handler with <c>[WaitForProjection&lt;T&gt;]</c> that receives it waits for that position
    ///     before it runs.
    /// </summary>
    public const string ConsistencyTokenHeader = "Wolverine-Consistency-Token";

    /// <summary>
    ///     Set to <c>true</c> on an HTTP response when a projection did not reach the position in time. The
    ///     request still succeeds.
    /// </summary>
    public const string ProjectionTimeoutHeader = "Wolverine-Projection-Timeout";

    /// <summary>
    ///     Called by generated code after the chain's commit: wait for the projections to process the events this
    ///     session appended. Null when the session appended no events.
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static Task<ProjectionWaitResult?> WaitForAppendedEventsAsync(object store, object session,
        Type[] projections, int timeoutMs, MessageContext context, CancellationToken token)
    {
        // A session that cannot report a position, a read-only one, appended nothing to wait for
        var position = (session as IStorageOperations)?.LastAppendedPosition;
        if (position == null) return Task.FromResult<ProjectionWaitResult?>(null);

        return WaitForPositionAsync(store, position, projections, timeoutMs, context, token);
    }

    /// <summary>
    ///     Called by generated code before a message handler runs: when the incoming envelope carries a
    ///     <see cref="ConsistencyTokenHeader" />, wait for the projections to reach it.
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static Task<ProjectionWaitResult?> WaitForEnvelopeTokenAsync(object store, Type[] projections,
        int timeoutMs, MessageContext context, CancellationToken token)
    {
        string? consistencyToken = null;
        context.Envelope?.TryGetHeader(ConsistencyTokenHeader, out consistencyToken);

        return WaitForTokenAsync(store, consistencyToken, projections, timeoutMs, context, token);
    }

    /// <summary>
    ///     Wait for the projections to reach the position in a <see cref="ConsistencyTokenHeader" /> value. Null when
    ///     there is no token, or it is not a position.
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static Task<ProjectionWaitResult?> WaitForTokenAsync(object store, string? consistencyToken,
        Type[] projections, int timeoutMs, MessageContext context, CancellationToken token)
    {
        if (consistencyToken.IsEmpty()) return Task.FromResult<ProjectionWaitResult?>(null);

        if (!EventStorePosition.TryParse(consistencyToken, out var position))
        {
            context.Runtime.Logger.LogWarning(
                "Ignored the {Header} value '{Token}', which is not an event store position",
                ConsistencyTokenHeader, consistencyToken);
            return Task.FromResult<ProjectionWaitResult?>(null);
        }

        return WaitForPositionAsync(store, position, projections, timeoutMs, context, token);
    }

    internal static async Task<ProjectionWaitResult?> WaitForPositionAsync(object store, EventStorePosition position,
        Type[] projections, int timeoutMs, MessageContext context, CancellationToken token)
    {
        if (store is not IEventStore eventStore)
        {
            throw new InvalidOperationException(
                $"[WaitForProjection] needs an event store, and {store.GetType().FullNameInCode()} does not implement {typeof(IEventStore).FullNameInCode()}.");
        }

        // An inline or live projection is already consistent when the commit returns, so there is nothing to wait
        // for. That also lets one build of the code run with a projection that is inline in tests and async in
        // production.
        var asynchronous = projections.Where(x => eventStore.AsyncShardNamesFor(x).Count > 0).ToArray();
        if (asynchronous.Length == 0)
        {
            return new ProjectionWaitResult(ProjectionWaitStatus.Reached, position, [], TimeSpan.Zero);
        }

        var result = await eventStore
            .WaitForProjectionsAsync(position, asynchronous, timeoutMs.Milliseconds(), token)
            .ConfigureAwait(false);

        Activity.Current?.SetTag(WolverineTracing.ProjectionWaitStatus, result.Status.ToString());

        if (!result.Reached)
        {
            context.Runtime.Logger.LogWarning(
                "[WaitForProjection] {Status} after {Elapsed} waiting for {Projections} to reach {Position}; carrying on without it. {Result}",
                result.Status, result.Elapsed, asynchronous.Select(x => x.FullNameInCode()).Join(", "), position,
                result);
        }

        return result;
    }
}
