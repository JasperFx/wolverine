using System;
using System.Collections.Generic;
using System.Linq;
using JasperFx.Core;
using Wolverine.Transports.Local;

namespace Wolverine.Runtime.Routing;

public class MessageRouter<T> : MessageRouterBase<T>
{
    public MessageRouter(WolverineRuntime runtime, IEnumerable<MessageRoute> routes) : base(runtime)
    {
        Routes = routes.ToArray();

        foreach (var route in Routes.Where(x => x.Sender.Endpoint is LocalQueueSettings))
            route.Rules.Fill(HandlerRules);
    }

    public MessageRoute[] Routes { get; }

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
}