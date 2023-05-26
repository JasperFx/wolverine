using JasperFx.Core;
using JasperFx.Core.Reflection;
using Wolverine.Transports.Local;

namespace Wolverine.Runtime.Routing;

public class MessageRouter<T> : MessageRouterBase<T>
{
    public MessageRouter(WolverineRuntime runtime, IEnumerable<MessageRoute> routes) : base(runtime)
    {
        Routes = routes.ToArray();

        foreach (var route in Routes.Where(x => x.Sender.Endpoint is LocalQueue))
            route.Rules.Fill(HandlerRules);
    }

    public override MessageRoute[] Routes { get; }

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
            envelopes[i] = Routes[i].CreateForSending(message, options, LocalDurableQueue, Runtime);
        }

        return envelopes;
    }

    public override IMessageRoute FindSingleRouteForSending()
    {
        if (Routes.Length == 1)
        {
            return Routes.Single();
        }

        throw new MultipleSubscribersException(typeof(T), Routes);
    }
}

public class MultipleSubscribersException : Exception
{
    public MultipleSubscribersException(Type messageType, MessageRoute[] routes) : base(
        $"There are multiple subscribing endpoints {routes.Select(x => x.Sender.Destination!.ToString()).Join(", ")} for message {messageType.FullNameInCode()}")
    {
    }
}