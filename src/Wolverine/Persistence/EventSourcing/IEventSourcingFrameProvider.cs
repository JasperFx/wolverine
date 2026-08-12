using System.Diagnostics.CodeAnalysis;
using JasperFx;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;
using JasperFx.Events;
using Wolverine.Configuration;
using Wolverine.Runtime.Handlers;

namespace Wolverine.Persistence.EventSourcing;

/// <summary>
/// The store seam for the shared aggregate handler workflow: a <b>sibling</b> of
/// <see cref="IPersistenceFrameProvider"/> rather than new members on it, so that non-event-sourcing
/// providers (EF Core, RavenDb, Cosmos) don't grow no-op aggregate members. Each event sourcing
/// integration implements this <b>on the same class</b> as its <see cref="IPersistenceFrameProvider"/>,
/// which is what makes it discoverable through the persistence strategies already registered on
/// <see cref="JasperFx.CodeGeneration.GenerationRules"/> — no second registry, and no list of known
/// stores anywhere in Wolverine core. See GH-3907, decision 5.
/// </summary>
/// <remarks>
/// <para>
/// Everything here is a fact about a store that the shared workflow cannot derive on its own. The two
/// frame-building members are the important ones: they exist so that <b>core never writes
/// <c>session.Events.SomeMethod(...)</c> into generated source</b>. No JasperFx session abstraction
/// declares the <c>.Events</c> accessor — each store surfaces its own <c>Events</c> property of its own
/// operations type — so a store hands back a finished <see cref="Frame"/> and keeps that spelling
/// entirely on its side of the seam. That also means the generated code stays byte-identical per store
/// as shared mechanism moves into core.
/// </para>
/// <para>
/// This is a genuine extension point: a store package that implements this interface and registers its
/// provider through <c>GenerationRules.AddPersistenceStrategy&lt;T&gt;()</c> lights the whole
/// <see cref="WriteModelAttribute"/> / <see cref="ReadModelAttribute"/> /
/// <see cref="DeciderFunctionAttribute"/> vocabulary up without any change to Wolverine core.
/// </para>
/// </remarks>
public interface IEventSourcingFrameProvider
{
    /// <summary>
    /// The store's display name, e.g. "Marten" or "Polecat". This is written verbatim into generated
    /// source as a comment, and into the exception messages the workflow throws when it cannot make
    /// sense of a handler signature.
    /// </summary>
    string StoreName { get; }

    /// <summary>
    /// The store's own public collection-of-events return type — <c>Wolverine.Marten.Events</c> or
    /// <c>Wolverine.Polecat.Events</c>. Both stay store-side: they are public vocabulary, and GH-3907
    /// retires nothing. The workflow only needs to recognize the type, so the seam hands it over rather
    /// than core naming either one.
    /// </summary>
    Type EventsCollectionType { get; }

    /// <summary>
    /// The exception type thrown from generated code when a required aggregate cannot be found —
    /// <c>Wolverine.Marten.UnknownAggregateException</c> or the Polecat equivalent. The seam carries it
    /// so that the shared missing-aggregate check can move into core without changing which exception
    /// type user code catches.
    /// </summary>
    Type UnknownAggregateExceptionType { get; }

    /// <summary>
    /// Build the frame that loads the aggregate's event stream for writing — the store's
    /// <c>FetchForWriting</c> / <c>FetchForExclusiveWriting</c> call. Must create exactly one variable of
    /// type <c>IEventStream&lt;TAggregate&gt;</c>; the workflow finds it there rather than through a
    /// store-specific frame type, which is what lets a store keep extras like batch-query enlistment
    /// private to itself.
    /// </summary>
    Frame BuildLoadAggregateFrame(AggregateLoadRequest request);

    /// <summary>
    /// Build the frame that reads the current state of an aggregate without opening it for writing —
    /// the store's <c>FetchLatest</c> call. Must create exactly one variable of the aggregate type.
    /// </summary>
    Frame BuildFetchLatestFrame(Type aggregateType, Variable identity);

    /// <summary>
    /// Whether the store identifies streams by <see cref="Guid"/> or by string key. Consulted only to
    /// pick between <c>IEvent.StreamId</c> and <c>IEvent.StreamKey</c> when the handler's input is an
    /// <c>IEvent&lt;T&gt;</c>.
    /// </summary>
    StreamIdentity DetermineStreamIdentity(IServiceContainer container);

    /// <summary>
    /// The store's natural-key type for this aggregate, when it has one and the workflow could not find
    /// the stream identity by any other means. Return null — the default — for a store with no
    /// natural-key concept, in which case the workflow simply reports that it could not determine an
    /// aggregate id.
    /// </summary>
    Type? TryDetermineNaturalKeyType(Type aggregateType, IServiceContainer container) => null;

    /// <summary>
    /// Store-specific <c>[Identity]</c> attribute types to honor on a command member, <em>in addition
    /// to</em> <see cref="JasperFx.IdentityAttribute"/>, which the workflow always checks. Polecat needs
    /// this because <c>Polecat.IdentityAttribute</c> shadows the shared one, so handler code written
    /// against Polecat may carry either.
    /// </summary>
    IReadOnlyList<Type> AdditionalIdentityAttributeTypes => [];
}

internal static class EventSourcingFrameProviderExtensions
{
    /// <summary>
    /// Decide how a handler's return value becomes events on the aggregate's stream. Lifted verbatim
    /// out of both integrations' <c>AggregateHandling</c>, where the two copies had come to differ only
    /// by the store's name once GH-3907's drift reconciliation landed.
    /// </summary>
    // These fire only now that the code lives in Wolverine core, whose trim/AOT analysis is stricter
    // than either integration ran - the behavior is identical to the two copies this replaces. Every
    // reflective close here happens at codegen time over the aggregate type, which AOT consumers
    // pre-generate via TypeLoadMode.Static, so none of it runs in a trimmed or AOT-published app.
    [UnconditionalSuppressMessage("Trimming", "IL2026",
        Justification = "CloseAndBuildAs closes ApplyEventsFromAsyncEnumerableFrame<>/RegisterEventsFrame<> over the aggregate type at codegen time. AOT consumers pre-generate via TypeLoadMode.Static.")]
    [UnconditionalSuppressMessage("AOT", "IL3050",
        Justification = "CloseAndBuildAs uses MakeGenericType at codegen time only. AOT consumers pre-generate via TypeLoadMode.Static so the reflective close never fires at runtime.")]
    [UnconditionalSuppressMessage("Trimming", "IL2072",
        Justification = "Closes() only tests whether a handler parameter type closes IEventStream<>; it reads interfaces off a type already rooted by handler discovery.")]
    public static void DetermineEventCaptureHandling(this IEventSourcingFrameProvider provider, IChain chain,
        MethodCall firstCall, Type aggregateType)
    {
        var asyncEnumerable = firstCall.Creates.FirstOrDefault(x => x.VariableType == typeof(IAsyncEnumerable<object>));
        if (asyncEnumerable != null)
        {
            asyncEnumerable.UseReturnAction(_ =>
            {
                return typeof(ApplyEventsFromAsyncEnumerableFrame<>).CloseAndBuildAs<Frame>(asyncEnumerable,
                    provider.StoreName, aggregateType);
            });

            return;
        }

        var eventsVariable = firstCall.Creates.FirstOrDefault(x => x.VariableType == provider.EventsCollectionType) ??
                             firstCall.Creates.FirstOrDefault(x =>
                                 x.VariableType.CanBeCastTo<IEnumerable<object>>() &&
                                 !x.VariableType.CanBeCastTo<IWolverineReturnType>());

        if (eventsVariable != null)
        {
            eventsVariable.UseReturnAction(
                v => typeof(RegisterEventsFrame<>).CloseAndBuildAs<MethodCall>(eventsVariable, aggregateType)
                    .WrapIfNotNull(v), $"Append events to the {provider.StoreName} event stream");

            return;
        }

        // If there's no return value of Events or IEnumerable<object>, and there's also no parameter of IEventStream<Aggregate>,
        // then assume that the default behavior of each return value is to be an event
        if (!firstCall.Method.GetParameters().Any(x => x.ParameterType.Closes(typeof(IEventStream<>))))
        {
            chain.ReturnVariableActionSource = new EventCaptureActionSource(aggregateType);
        }
    }
}
