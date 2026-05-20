namespace Wolverine.Runtime.Routing;

/// <summary>
/// Diagnostic description of an <see cref="IMessageRouteSource"/> — what it is, what it
/// matches, and whether it is additive (lets later sources contribute) or terminating
/// (short-circuits the route source chain once it produces any routes). Surfaced through
/// <see cref="IWolverineRuntime.ExplainRoutingFor"/>, the <c>describe-routing</c> CLI, and the
/// expanded service capabilities so both humans and AI agents can reason about routing.
/// </summary>
public class RouteSourceDescriptor
{
    /// <summary>
    /// Short, stable identifier for the route source (e.g. "LocalRouting",
    /// "RabbitMqConventionalRouting"). Defaults to the implementing type name.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Human- and AI-readable explanation of what this source matches and how it decides.
    /// </summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// True when this source lets subsequent route sources also contribute routes; false when
    /// producing any route terminates the route source chain.
    /// </summary>
    public bool IsAdditive { get; set; }

    /// <summary>
    /// For sources that delegate to routing conventions (the conventional-routing source and
    /// broker conventions), the conventions consulted by this source.
    /// </summary>
    public RoutingConventionDescriptor[] Conventions { get; set; } = [];

    public override string ToString()
    {
        var additive = IsAdditive ? "additive" : "terminating";
        return $"{Name} ({additive}): {Description}";
    }
}

/// <summary>
/// Diagnostic description of an <see cref="Wolverine.Runtime.Routing.IMessageRoutingConvention"/>.
/// For broker conventions the shared <see cref="Description"/> is combined with the parent
/// transport's own <see cref="TransportDescription"/>, plus the broker <see cref="TransportScheme"/>
/// and <see cref="TransportName"/> so that named brokers of the same transport type in a single
/// application can be told apart.
/// </summary>
public class RoutingConventionDescriptor
{
    /// <summary>
    /// Short, stable identifier for the convention. Defaults to the implementing type name.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Human- and AI-readable explanation of what this convention does (shared across all brokers
    /// using the same convention base).
    /// </summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// The parent transport's broker scheme/protocol (e.g. "rabbitmq"). Disambiguates named brokers
    /// of the same transport type within a single application.
    /// </summary>
    public string TransportScheme { get; set; } = string.Empty;

    /// <summary>
    /// The parent transport's name. For multiple named brokers of the same transport type, this
    /// distinguishes them.
    /// </summary>
    public string TransportName { get; set; } = string.Empty;

    /// <summary>
    /// The parent transport's own description, folded into the routing explanation alongside the
    /// shared convention <see cref="Description"/>.
    /// </summary>
    public string TransportDescription { get; set; } = string.Empty;

    public override string ToString()
    {
        var scheme = string.IsNullOrEmpty(TransportScheme) ? "" : $" [{TransportScheme}]";
        return $"{Name}{scheme}: {Description}".Trim();
    }
}
