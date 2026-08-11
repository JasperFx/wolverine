using System.Diagnostics.CodeAnalysis;
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
/// integration implements this alongside its existing <see cref="IPersistenceFrameProvider"/>.
/// See GH-3907, decision 5.
/// </summary>
/// <remarks>
/// Deliberately <c>internal</c>, reaching the first-party integrations through <c>InternalsVisibleTo</c>
/// — the same pattern core already uses for its transport integrations. Nothing here is public API, so
/// the shape stays free to change while the rest of the workflow is pulled down and while
/// <c>Wolverine.Fisher</c> proves it out. GH-3907 marks it for revisiting in 2027 as a public extension
/// point third-party stores can build on.
/// </remarks>
internal interface IEventSourcingFrameProvider
{
    /// <summary>
    /// The store's display name, e.g. "Marten" or "Polecat". This is written verbatim into generated
    /// source as a comment, so it is part of what keeps codegen output byte-identical per store as
    /// shared mechanism moves into core.
    /// </summary>
    string StoreName { get; }

    /// <summary>
    /// The store's own public collection-of-events return type — <c>Wolverine.Marten.Events</c> or
    /// <c>Wolverine.Polecat.Events</c>. Both stay store-side: they are public vocabulary, and GH-3907
    /// retires nothing in this release. The workflow only needs to recognize the type, so the seam
    /// hands it over rather than core naming either one.
    /// </summary>
    Type EventsCollectionType { get; }
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
