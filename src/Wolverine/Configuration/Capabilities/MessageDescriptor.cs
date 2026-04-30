using System.Text.Json.Serialization;
using JasperFx.Core.Reflection;
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
        IsTimeoutMessage = messageType.CanBeCastTo<TimeoutMessage>();

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

    /// <summary>
    /// True when the message type derives from <see cref="TimeoutMessage"/>.
    /// Surfaced so external monitoring tools (e.g. CritterWatch's saga
    /// visualization) can render timeout messages distinctly — they're a
    /// fundamentally different narrative element in saga workflows
    /// ("delay then re-enter the saga") versus regular handler-driven
    /// messages, and visualizations look much clearer when they're
    /// shaped differently.
    /// </summary>
    public bool IsTimeoutMessage { get; set; }

    public List<MessageHandlerDescriptor> Handlers { get; set; } = new();

    public List<MessageSubscriptionDescriptor> Subscriptions { get; set; } = new();
}