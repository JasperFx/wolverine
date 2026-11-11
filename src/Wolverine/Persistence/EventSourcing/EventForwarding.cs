using System.Diagnostics.CodeAnalysis;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using JasperFx.Events;
using Wolverine.Runtime;
using Wolverine.Runtime.Routing;

namespace Wolverine.Persistence.EventSourcing;

/// <summary>
///     GH-5141: the event forwarding router shared by the Marten, Polecat and Fisher integrations. It routes
///     an <see cref="IEvent{T}" /> wrapper to the handlers and subscribers of the wrapper, of the unwrapped
///     event data and of any transformation registered through an integration's <c>SubscribeToEvent&lt;T&gt;()</c>.
/// </summary>
/// <remarks>
///     Each integration still owns its own instance, so an application with two event store integrations keeps
///     one set of transformations per integration, exactly as it did when each integration carried its own copy
///     of this class (<c>MartenEventRouter</c>, <c>PolecatEventRouter</c>, <c>FisherEventRouter</c>).
/// </remarks>
internal class EventForwardingRouter : IMessageRouteSource
{
    // Reflects over the runtime-resolved IEvent<T> wrapper type to close EventUnwrappingMessageRoute<T>.
    // The same shape the three integrations' own routers had before GH-5141 moved them here (they were
    // suppressed wholesale in the integrations' GlobalSuppressions). Event forwarding only ever routes event
    // types the application registered with its event store, which the event store roots itself.
    [UnconditionalSuppressMessage("Trimming", "IL2026",
        Justification = "Closes EventUnwrappingMessageRoute<T> over a registered event type; see comment.")]
    [UnconditionalSuppressMessage("Trimming", "IL2067",
        Justification = "Interface walk over a registered event wrapper type; see comment.")]
    [UnconditionalSuppressMessage("AOT", "IL3050",
        Justification = "Closes IEvent<T> / EventUnwrappingMessageRoute<T> over a registered event type; see comment.")]
    public IEnumerable<IMessageRoute> FindRoutes(Type messageType, IWolverineRuntime runtime)
    {
        if (messageType.Closes(typeof(IEvent<>)))
        {
            var eventType = messageType.GetGenericArguments().Single();
            var wrappedType = typeof(IEvent<>).MakeGenericType(eventType);

            if (messageType.IsConcrete())
            {
                return runtime.RoutingFor(wrappedType).Routes;
            }

            MessageRoute[] innerRoutes = new ExplicitRouting().FindRoutes(wrappedType, runtime).OfType<MessageRoute>().ToArray();
            if (!innerRoutes.Any())
            {
                innerRoutes = new LocalRouting().FindRoutes(wrappedType, runtime).OfType<MessageRoute>().ToArray();
            }

            // First look for explicit transformations
            var transformers = Transformers.Where(x => x.SourceType == wrappedType);
            var transformed = transformers.SelectMany(x =>
                runtime.RoutingFor(x.DestinationType).Routes.Select(x.CreateRoute));

            var forEventType = runtime.RoutingFor(eventType).Routes.Select(route =>
                typeof(EventUnwrappingMessageRoute<>).CloseAndBuildAs<IMessageRoute>(route, eventType));

            var candidates = forEventType.Concat(transformed).Concat(innerRoutes).ToArray();
            return candidates;
        }

        return [];
    }

    public bool IsAdditive => false;
    public List<IMessageTransformation> Transformers { get; } = [];

    /// <summary>
    ///     GH-4310: SubscribeToEvent&lt;T&gt;() calls whose transformation was never completed with
    ///     TransformedTo(). Checked at bootstrap — bare entries with no event forwarding enabled are
    ///     a configuration error, because nothing would ever deliver those events.
    /// </summary>
    internal List<Type> BareSubscriptions { get; } = [];

    /// <summary>
    ///     Start a transformation of a forwarded event. The subscription counts as bare until
    ///     <see cref="CompleteTransformation{TSource,TDestination}" /> is called for it.
    /// </summary>
    internal void StartTransformation<TSource>() where TSource : notnull
    {
        BareSubscriptions.Add(typeof(TSource));
    }

    internal void CompleteTransformation<TSource, TDestination>(Func<IEvent<TSource>, TDestination> transformer)
        where TSource : notnull
    {
        // The subscription is now completed — see BareSubscriptions (GH-4310).
        BareSubscriptions.Remove(typeof(TSource));

        var transformation = new MessageTransformation<IEvent<TSource>, TDestination>(transformer);
        Transformers.Add(transformation);
    }

    /// <summary>
    ///     GH-4310: refuse a bare <c>SubscribeToEvent&lt;T&gt;()</c> when nothing else delivers the events.
    /// </summary>
    /// <param name="fastEventForwardingEnabled">Whether the integration forwards events on commit anyway</param>
    /// <param name="fastEventForwardingPropertyName">The integration's name for that switch, for the message</param>
    internal void AssertNoBareSubscriptions(bool fastEventForwardingEnabled, string fastEventForwardingPropertyName)
    {
        if (!BareSubscriptions.Any() || fastEventForwardingEnabled) return;

        var names = BareSubscriptions.Select(x => x.Name).Join(", ");
        throw new InvalidOperationException(
            $"SubscribeToEvent<T>() was called for [{names}] but nothing completes the subscription, so these events would never reach a handler. " +
            $"SubscribeToEvent<T>() only defines a transformation of a forwarded event — either finish it with .TransformedTo(...), " +
            $"or, to simply have Wolverine handlers receive the event after commit, set {fastEventForwardingPropertyName} = true on this integration instead.");
    }
}

/// <summary>
///     Lets a handler of <c>IEvent&lt;T&gt;</c> count as a handler of the forwarded event wrapper.
/// </summary>
internal class EventWrapperForwarder : IHandledTypeRule
{
    [UnconditionalSuppressMessage("Trimming", "IL2067",
        Justification = "Interface walk over a concrete handled message type, which handler discovery already roots.")]
    public bool TryFindHandledType(Type concreteType, [NotNullWhen(true)] out Type? handlerType)
    {
        handlerType = concreteType.FindInterfaceThatCloses(typeof(IEvent<>))!;
        return handlerType != null;
    }
}

internal class EventUnwrappingMessageRoute<T> : TransformedMessageRoute<IEvent<T>, T> where T : notnull
{
    public EventUnwrappingMessageRoute(IMessageRoute inner) : base(e => e.Data, inner)
    {
    }

    public override string ToString()
    {
        return "Unwrap event wrapper to " + typeof(T).FullNameInCode();
    }
}
