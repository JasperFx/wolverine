using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using JasperFx;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;
using JasperFx.Events;
using Wolverine.Configuration;
using Wolverine.Persistence.EventSourcing;
using Wolverine.Persistence.Sagas;

namespace Wolverine.Persistence;

/// <summary>
///     Shared codegen for <see cref="AppendEvents" /> and <see cref="StartStream" />.
/// </summary>
internal static class EventSideEffectFrames
{
    /// <summary>
    ///     Build the frame that invokes the side effect's <c>Execute(IEventOperations)</c>, having first enrolled
    ///     the chain in the event store's transaction.
    /// </summary>
    /// <remarks>
    ///     The transaction enrollment is the reason these implement <see cref="ISideEffectAware" /> rather than
    ///     plain <see cref="ISideEffect" />. Appending through <c>IEventOperations</c> only queues the work into
    ///     the store's unit of work; something still has to call SaveChangesAsync. <c>AutoApplyTransactions</c>
    ///     will not do it, because it only fires when a provider's <c>CanApply</c> recognizes a store type in the
    ///     chain -- and the entire point of these side effects is that a store agnostic handler names no store
    ///     type at all. Without this the events were silently queued and never committed.
    /// </remarks>
    public static Frame BuildFrame(
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicMethods)]
        Type effectType, IChain chain, Variable variable, GenerationRules rules,
        IServiceContainer container)
    {
        var provider = findEventStoreProvider(effectType, chain, rules, container);
        provider.ApplyTransactionSupport(chain, container);

        var method = effectType.GetMethod(nameof(AppendEvents.Execute),
            BindingFlags.Public | BindingFlags.Instance)!;

        return new IfElseNullGuardFrame.IfNullGuardFrame(variable,
            new MethodCall(effectType, method)
            {
                Target = variable,
                CommentText = "Append events through the registered event store"
            });
    }

    private static IPersistenceFrameProvider findEventStoreProvider(Type effectType, IChain chain,
        GenerationRules rules, IServiceContainer container)
    {
        // An event side effect needs an *event* store, so narrow to the providers that also implement the
        // event sourcing seam. Marten, Polecat and Fisher each implement both interfaces on one class.
        var candidates = rules.PersistenceProviders()
            .Where(x => x is IEventSourcingFrameProvider)
            .ToArray();

        if (candidates.Length == 1)
        {
            return candidates[0];
        }

        if (candidates.Length == 0)
        {
            throw new InvalidOperationException(
                $"{chain.Description} returns {effectType.NameInCode()}, but this application has no registered " +
                "event store. Storage.AppendEvents() and Storage.StartStream() require an event store " +
                "integration -- call IntegrateWithWolverine() on your Marten, Polecat, or Fisher store " +
                "registration.");
        }

        // More than one event store is a real configuration, and there is nothing on the side effect itself
        // that says which one it belongs to -- unlike IStorageAction<T>, which resolves by entity type.
        var narrowed = candidates.Where(x => x.CanApply(chain, container)).ToArray();
        if (narrowed.Length == 1)
        {
            return narrowed[0];
        }

        throw new InvalidOperationException(
            $"{chain.Description} returns {effectType.NameInCode()}, but this application has " +
            $"{candidates.Length} registered event stores and nothing on the side effect says which one to use. " +
            "Mark the handler with [Storage(typeof(IYourStore))] to name the store explicitly.");
    }
}

/// <summary>
///     Append events to an existing event stream as a Wolverine <see cref="ISideEffect" />, returned from a handler
///     or HTTP endpoint rather than written by reaching for a store's session.
/// </summary>
/// <remarks>
///     <para>
///         The work is expressed entirely against <see cref="IEventOperations" />, the shared write-side event API
///         that Marten, Polecat and Fisher all implement, so a handler returning one of these is valid on any of
///         them. Each store's <c>IntegrateWithWolverine()</c> registers the variable source that hands Wolverine
///         the <see cref="IEventOperations" /> for the active session.
///     </para>
///     <para>
///         Build these through <see cref="Storage.AppendEvents(Guid,object[])" /> and friends rather than
///         constructing them directly.
///     </para>
/// </remarks>
public class AppendEvents : ISideEffectAware
{
    static Frame ISideEffectAware.BuildFrame(IChain chain, Variable variable, GenerationRules rules,
        IServiceContainer container)
        => EventSideEffectFrames.BuildFrame(typeof(AppendEvents), chain, variable, rules, container);

    internal AppendEvents(Guid streamId, object[] events)
    {
        StreamId = streamId;
        Events = events;
    }

    internal AppendEvents(string streamKey, object[] events)
    {
        StreamKey = streamKey;
        Events = events;
    }

    /// <summary>
    ///     The Guid identity of the target stream, for stores configured with Guid stream identity.
    /// </summary>
    public Guid? StreamId { get; }

    /// <summary>
    ///     The string key of the target stream, for stores configured with string stream identity.
    /// </summary>
    public string? StreamKey { get; }

    /// <summary>
    ///     The events to append, in order.
    /// </summary>
    public IReadOnlyList<object> Events { get; }

    /// <summary>
    ///     The expected current version of the stream on the server. When set, the transaction is aborted if the
    ///     stream has moved on, giving optimistic concurrency.
    /// </summary>
    public long? ExpectedVersion { get; init; }

    // Named Execute rather than Apply because Wolverine's ISideEffect convention only looks for
    // Execute/ExecuteAsync -- see SideEffectPolicy.findMethod. The IEventOperations parameter is resolved
    // like any other, through the variable source each store registers.
    public void Execute(IEventOperations operations)
    {
        // Returning an empty append is a legitimate "nothing happened" answer from a decision function,
        // and every store would otherwise be handed a stream action carrying no events.
        if (Events.Count == 0)
        {
            return;
        }

        if (StreamId.HasValue)
        {
            if (ExpectedVersion.HasValue)
            {
                operations.Append(StreamId.Value, ExpectedVersion.Value, Events.ToArray());
            }
            else
            {
                operations.Append(StreamId.Value, Events);
            }
        }
        else
        {
            if (ExpectedVersion.HasValue)
            {
                operations.Append(StreamKey!, ExpectedVersion.Value, Events);
            }
            else
            {
                operations.Append(StreamKey!, Events);
            }
        }
    }
}

/// <summary>
///     Start a brand new event stream as a Wolverine <see cref="ISideEffect" />. See <see cref="AppendEvents" />
///     for the rationale; this is the same mechanism for the "this stream does not exist yet" case.
/// </summary>
public class StartStream : ISideEffectAware
{
    static Frame ISideEffectAware.BuildFrame(IChain chain, Variable variable, GenerationRules rules,
        IServiceContainer container)
        => EventSideEffectFrames.BuildFrame(typeof(StartStream), chain, variable, rules, container);

    internal StartStream(Guid streamId, Type? aggregateType, object[] events)
    {
        StreamId = streamId;
        AggregateType = aggregateType;
        Events = events;
    }

    internal StartStream(string streamKey, Type? aggregateType, object[] events)
    {
        StreamKey = streamKey;
        AggregateType = aggregateType;
        Events = events;
    }

    /// <summary>
    ///     The Guid identity for the new stream, for stores configured with Guid stream identity.
    /// </summary>
    public Guid? StreamId { get; }

    /// <summary>
    ///     The string key for the new stream, for stores configured with string stream identity.
    /// </summary>
    public string? StreamKey { get; }

    /// <summary>
    ///     The aggregate type this stream is for, when the store tracks one. Optional.
    /// </summary>
    public Type? AggregateType { get; }

    /// <summary>
    ///     The events to start the stream with, in order.
    /// </summary>
    public IReadOnlyList<object> Events { get; }

    public void Execute(IEventOperations operations)
    {
        if (Events.Count == 0)
        {
            return;
        }

        if (StreamId.HasValue)
        {
            if (AggregateType != null)
            {
                operations.StartStream(AggregateType, StreamId.Value, Events);
            }
            else
            {
                operations.StartStream(StreamId.Value, Events);
            }
        }
        else
        {
            if (AggregateType != null)
            {
                operations.StartStream(AggregateType, StreamKey!, Events);
            }
            else
            {
                operations.StartStream(StreamKey!, Events);
            }
        }
    }
}
