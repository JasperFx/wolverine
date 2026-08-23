using Microsoft.Extensions.Logging;
using Wolverine.Transports.Local;

namespace Wolverine.Configuration;

internal enum ListenerConfigurationSeverity
{
    /// <summary>
    /// The setting is inert, but the endpoint still does something reasonable. Log and carry on.
    /// </summary>
    Warning,

    /// <summary>
    /// The setting asked for a processing guarantee the endpoint's mode cannot deliver. Refuse to start.
    /// </summary>
    Fatal
}

internal record ListenerConfigurationProblem(
    Endpoint Endpoint,
    ListenerConfigurationSeverity Severity,
    string Message);

/// <summary>
/// GH-3712. Several listener configuration combinations used to be accepted in silence and then do
/// nothing at runtime -- most damagingly <c>ProcessInline()</c> together with
/// <c>PartitionProcessingByGroupId()</c>, which reads as "messages sharing a group id never run
/// concurrently" and delivered no such thing. Everything Wolverine's Inline listening path ignores is
/// enumerated here so a misconfiguration is either refused at bootstrap or said out loud in the log.
/// </summary>
internal static class ListenerConfigurationValidator
{
    /// <summary>
    /// Validate every listening endpoint, logging the warnings and throwing on the first fatal problem.
    /// Expects the endpoints to have already been compiled, so that endpoint policies and delayed
    /// configuration have had their say about the final mode.
    /// </summary>
    internal static void AssertValid(IEnumerable<Endpoint> endpoints, ILogger? logger)
    {
        var problems = endpoints.SelectMany(Validate).ToArray();

        foreach (var problem in problems.Where(x => x.Severity == ListenerConfigurationSeverity.Warning))
        {
            logger?.LogWarning(problem.Message);
        }

        var fatal = problems.FirstOrDefault(x => x.Severity == ListenerConfigurationSeverity.Fatal);
        if (fatal != null)
        {
            throw new InvalidListenerConfigurationException(fatal.Message);
        }
    }

    internal static IEnumerable<ListenerConfigurationProblem> Validate(Endpoint endpoint)
    {
        if (!endpoint.IsListener && endpoint is not LocalQueue)
        {
            yield break;
        }

        if (endpoint.Mode != EndpointMode.Inline)
        {
            yield break;
        }

        var name = describe(endpoint);

        if (endpoint.GroupShardingSlotNumber.HasValue)
        {
            yield return new ListenerConfigurationProblem(endpoint, ListenerConfigurationSeverity.Fatal,
                $"Invalid listener configuration for {name}: PartitionProcessingByGroupId() was configured on an Inline endpoint. " +
                "Partitioned processing is implemented by Wolverine's local execution block, and an Inline endpoint executes each message " +
                "directly on the transport's listening callback without one -- so the group id ordering guarantee would silently not exist. " +
                "Either remove ProcessInline() (partitioned processing requires BufferedInMemory or Durable), or remove " +
                "PartitionProcessingByGroupId() from this endpoint.");
        }

        if (endpoint.DiscardedMaxDegreeOfParallelism is { } discarded)
        {
            yield return new ListenerConfigurationProblem(endpoint, ListenerConfigurationSeverity.Warning,
                $"Ignored listener configuration for {name}: a maximum parallelism of {discarded} was configured on an Inline endpoint, " +
                "and Wolverine has reset it to 1. MaximumParallelMessages(), Sequential(), ExclusiveNodeWithParallelism() and " +
                "ExclusiveNodeWithSessionOrdering() all size Wolverine's local execution block, which an Inline endpoint does not have. " +
                "How many messages an Inline listener handles at once is decided by the transport's own listener (for example RabbitMQ's " +
                "ConsumerDispatchConcurrency). Use BufferedInMemory() or UseDurableInbox() if you want Wolverine to govern the parallelism.");
        }

        if (endpoint.BufferingLimitsAreExplicit)
        {
            yield return new ListenerConfigurationProblem(endpoint, ListenerConfigurationSeverity.Warning,
                $"Ignored listener configuration for {name}: BufferingLimits were configured on an Inline endpoint. Back pressure is applied " +
                "by counting messages queued in Wolverine's local execution block, so an Inline endpoint never starts a BackPressureAgent " +
                "and these limits do nothing. Use BufferedInMemory(limits) or UseDurableInbox(limits) if you want back pressure.");
        }
    }

    private static string describe(Endpoint endpoint)
    {
        return endpoint.EndpointName == endpoint.Uri.ToString()
            ? $"endpoint '{endpoint.Uri}'"
            : $"endpoint '{endpoint.EndpointName}' ({endpoint.Uri})";
    }
}

/// <summary>
/// GH-3712. Thrown at bootstrap when a listening endpoint's configuration asks for a processing
/// guarantee that its <see cref="EndpointMode"/> cannot deliver.
/// </summary>
public class InvalidListenerConfigurationException : Exception
{
    public InvalidListenerConfigurationException(string message) : base(message)
    {
    }
}
