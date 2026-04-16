using JasperFx.Core;
using JasperFx.Core.Reflection;
using Wolverine.Runtime.Partitioning;
using Wolverine.Transports.Local;

namespace Wolverine.Runtime.Routing;

public class MessageRouter<T> : MessageRouterBase<T>
{
    public MessageRouter(WolverineRuntime runtime, IEnumerable<IMessageRoute> routes) : base(runtime)
    {
        Routes = DeduplicateRoutes(routes.ToArray());

        // ReSharper disable once VirtualMemberCallInConstructor
        foreach (var route in Routes)
        {
            if (route is MessageRoute { Sender.Endpoint: LocalQueue } messageRoute)
            {
                messageRoute.Rules.Fill(HandlerRules);
            }
        }
    }

    public override IMessageRoute[] Routes { get; }

    /// <summary>
    /// When a GlobalPartitionedRoute is present, it will fan out messages to sticky handler
    /// local queues via companion local queues. If there are also explicit local queue routes
    /// targeting those same sticky handler queues, remove the duplicates to prevent handlers
    /// from executing multiple times. See https://github.com/JasperFx/wolverine/issues/2303
    /// </summary>
    internal static IMessageRoute[] DeduplicateRoutes(IMessageRoute[] routes)
    {
        var hasGlobalPartitioned = routes.Any(r => r is GlobalPartitionedRoute);
        if (!hasGlobalPartitioned) return routes;

        // Collect the URIs of local queues that have sticky handlers.
        // These will be reached by the GlobalPartitionedRoute's fanout, so explicit
        // routes to these same queues are redundant.
        var stickyLocalQueueUris = new HashSet<Uri>();
        foreach (var route in routes)
        {
            if (route is MessageRoute { Sender.Endpoint: LocalQueue localQueue } &&
                localQueue.StickyHandlers.Count > 0)
            {
                stickyLocalQueueUris.Add(localQueue.Uri);
            }
        }

        if (stickyLocalQueueUris.Count == 0) return routes;

        // Remove explicit routes to sticky handler local queues — the GlobalPartitionedRoute
        // fanout will deliver to them
        return routes.Where(r =>
        {
            if (r is MessageRoute { Sender.Endpoint: LocalQueue lq } &&
                stickyLocalQueueUris.Contains(lq.Uri))
            {
                return false; // Skip this duplicate route
            }
            return true;
        }).ToArray();
    }

    public override Envelope[] RouteForSend(T message, DeliveryOptions? options)
    {
        if (message == null)
        {
            throw new ArgumentNullException(nameof(message));
        }

        return RouteForPublish(message, options);
    }

    public override Envelope[] RouteForPublish(T message, DeliveryOptions? options)
    {
        if (message == null)
        {
            throw new ArgumentNullException(nameof(message));
        }

        var envelopes = new Envelope[Routes.Length];
        for (var i = 0; i < envelopes.Length; i++)
        {
            envelopes[i] = Routes[i].CreateForSending(message, options, LocalDurableQueue, Runtime, null);
        }

        return envelopes;
    }

    public override IMessageRoute FindSingleRouteForSending()
    {
        if (Routes.Length == 1)
        {
            return Routes[0];
        }

        throw new MultipleSubscribersException(typeof(T), Routes);
    }
}

public class MultipleSubscribersException : Exception
{
    public MultipleSubscribersException(Type messageType, IMessageRoute[] routes) : base(
        $"There are multiple subscribing endpoints {routes.OfType<MessageRoute>().Select(x => x.Sender.Destination.ToString()).Join(", ")} for message {messageType.FullNameInCode()}")
    {
    }
}