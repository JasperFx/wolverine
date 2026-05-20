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

        // The route sources / conventions that actually contributed this message's routes, so
        // tooling (and AI agents) can see *why* it routes where it does, not just the destinations.
        var explanation = runtime.ExplainRoutingFor(messageType);
        RouteSources.AddRange(explanation.Steps
            .Where(x => x.SkipReason is null && x.Produced.Count != 0)
            .Select(x => x.Source));

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

    /// <summary>
    /// The route sources and conventions that contributed this message type's routes — the "why"
    /// behind <see cref="Subscriptions"/>. Surfaced so external tooling (e.g. CritterWatch) and AI
    /// agents can reason about routing decisions, not just the resulting destinations.
    /// </summary>
    public List<RouteSourceDescriptor> RouteSources { get; set; } = new();
}