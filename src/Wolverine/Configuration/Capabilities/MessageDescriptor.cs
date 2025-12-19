using System.Text.Json.Serialization;
using JasperFx.Descriptors;
using Wolverine.Runtime;
using Wolverine.Runtime.Handlers;
using Wolverine.Runtime.Routing;

namespace Wolverine.Configuration.Capabilities;

public class MessageDescriptor
{
    internal void ReadHandlerChain(HandlerChain chain, HandlerGraph handlers)
    {
        if (chain.Handlers.Any())
        {
            Handlers.Add(new MessageHandlerDescriptor(chain, handlers));
        }

        foreach (var handlerChain in chain.ByEndpoint)
        {
            ReadHandlerChain(handlerChain, handlers);
        }
    }
    
    public MessageDescriptor(Type messageType, IWolverineRuntime runtime) : this(TypeDescriptor.For(messageType))
    {
        var routes = runtime.RoutingFor(messageType).Routes;
        Subscriptions.AddRange(routes.Select(x => x.Describe()));

        var chain = runtime.Options.HandlerGraph.ChainFor(messageType);
        if (chain != null)
        {
            ReadHandlerChain(chain, runtime.Options.HandlerGraph);
        }
    }

    [JsonConstructor]
    public MessageDescriptor(TypeDescriptor type)
    {
        Type = type;
    }

    public TypeDescriptor Type { get; }

    public List<MessageHandlerDescriptor> Handlers { get; set; } = new();

    public List<MessageSubscriptionDescriptor> Subscriptions { get; set; } = new();
}