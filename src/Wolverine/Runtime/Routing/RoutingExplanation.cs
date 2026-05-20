using System.Text;
using JasperFx.Core.Reflection;

namespace Wolverine.Runtime.Routing;

/// <summary>
/// A structured, on-demand explanation of how a single message type is routed by Wolverine:
/// which <see cref="IMessageRouteSource"/>s were consulted, what each produced, whether a
/// terminating source short-circuited the chain, and the final route set. Produced by
/// <see cref="IWolverineRuntime.ExplainRoutingFor"/>.
///
/// The <see cref="ToText"/> rendering is intended to be read by both humans and AI agents: it
/// uses stable, labeled lines so an agent can parse "why is X routed here / why nowhere" without
/// scraping free-form prose.
/// </summary>
public class RoutingExplanation
{
    /// <summary>The fully-qualified message type name this explanation describes.</summary>
    public string MessageType { get; set; } = string.Empty;

    /// <summary>
    /// True when the type is a Wolverine framework "system message type"
    /// (IInternalMessage / IAgentCommand / INotToBeRouted, or from an
    /// [ExcludeFromServiceCapabilities] assembly) and is therefore filtered out of
    /// observer hooks and service capabilities.
    /// </summary>
    public bool IsSystemMessageType { get; set; }

    /// <summary>
    /// The current value of <see cref="WolverineOptions.LocalRoutingConventionDisabled"/>. When true,
    /// the LocalRouting source is short-circuited and never routes any message to a local in-process
    /// queue — surfaced here so it's obvious why local routing produced nothing.
    /// </summary>
    public bool LocalRoutingConventionDisabled { get; set; }

    /// <summary>Each route source consulted, in the order Wolverine consulted them.</summary>
    public List<RouteSourceStep> Steps { get; set; } = new();

    /// <summary>The final, combined route set produced for this message type.</summary>
    public List<MessageSubscriptionDescriptor> FinalRoutes { get; set; } = new();

    /// <summary>
    /// Render a deterministic, labeled text block that is both human-readable and friendly for an
    /// AI agent to consume. Stable line prefixes (MESSAGE / SYSTEM / SOURCE / produces / skipped /
    /// ROUTE) let tooling parse the trace without natural-language scraping.
    /// </summary>
    public string ToText()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"MESSAGE: {MessageType}");
        sb.AppendLine($"SYSTEM-MESSAGE-TYPE: {IsSystemMessageType.ToString().ToLowerInvariant()}");
        sb.AppendLine($"LOCAL-ROUTING-CONVENTION-DISABLED: {LocalRoutingConventionDisabled.ToString().ToLowerInvariant()}");
        sb.AppendLine();
        sb.AppendLine("ROUTE SOURCES (in order consulted):");
        if (Steps.Count == 0)
        {
            sb.AppendLine("  (none)");
        }
        foreach (var step in Steps)
        {
            var kind = step.Source.IsAdditive ? "additive" : "terminating";
            sb.AppendLine($"  SOURCE: {step.Source.Name} [{kind}]");
            if (!string.IsNullOrWhiteSpace(step.Source.Description))
            {
                sb.AppendLine($"    about: {step.Source.Description}");
            }

            if (step.SkipReason is not null)
            {
                sb.AppendLine($"    skipped: {step.SkipReason}");
            }
            else if (step.Produced.Count == 0)
            {
                sb.AppendLine("    produces: (no routes — no match)");
            }
            else
            {
                foreach (var route in step.Produced)
                {
                    sb.AppendLine($"    produces: {route.Endpoint} ({route.ContentType})");
                }
            }

            foreach (var convention in step.Source.Conventions)
            {
                var scheme = string.IsNullOrEmpty(convention.TransportScheme)
                    ? ""
                    : $" [{convention.TransportScheme}{(string.IsNullOrEmpty(convention.TransportName) || convention.TransportName == convention.TransportScheme ? "" : "/" + convention.TransportName)}]";
                sb.AppendLine($"    convention: {convention.Name}{scheme} — {convention.Description}");
                if (!string.IsNullOrWhiteSpace(convention.TransportDescription))
                {
                    sb.AppendLine($"      transport: {convention.TransportDescription}");
                }
            }
        }

        sb.AppendLine();
        sb.AppendLine($"FINAL ROUTES: {FinalRoutes.Count}");
        if (FinalRoutes.Count == 0)
        {
            sb.AppendLine("  (this message type is routed nowhere)");
        }
        foreach (var route in FinalRoutes)
        {
            sb.AppendLine($"  ROUTE: {route.Endpoint} ({route.ContentType})");
        }

        return sb.ToString();
    }

    public override string ToString() => ToText();
}

/// <summary>
/// One <see cref="IMessageRouteSource"/>'s contribution to a <see cref="RoutingExplanation"/>:
/// the source's descriptor, the routes it produced, and — if a prior terminating source already
/// short-circuited the chain — why it was skipped.
/// </summary>
public class RouteSourceStep
{
    /// <summary>Descriptor (name, description, additive/terminating, conventions) for the source.</summary>
    public RouteSourceDescriptor Source { get; set; } = new();

    /// <summary>The routes this source produced for the message type (empty if it produced none).</summary>
    public List<MessageSubscriptionDescriptor> Produced { get; set; } = new();

    /// <summary>
    /// Null when this source actually ran. Populated when an earlier terminating source already
    /// produced routes, so Wolverine never consulted this source for the message type.
    /// </summary>
    public string? SkipReason { get; set; }
}
