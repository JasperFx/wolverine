using Wolverine.Configuration;

namespace Wolverine.AzureServiceBus;

/// <summary>
/// Extension methods for Azure Service Bus specific exclusive node configurations
/// </summary>
public static class AzureServiceBusListenerConfigurationExtensions
{
    /// <summary>
    /// Configure this Azure Service Bus queue to run exclusively on a single node
    /// with session-based parallel processing. This ensures only one node processes
    /// the queue while allowing multiple sessions to be processed in parallel.
    /// </summary>
    /// <param name="configuration">The Azure Service Bus listener configuration</param>
    /// <param name="maxParallelSessions">Maximum number of sessions to process in parallel. Default is 10.</param>
    /// <param name="endpointName">Optional endpoint name for identification</param>
    /// <returns>The configuration for method chaining</returns>
    public static AzureServiceBusListenerConfiguration ExclusiveNodeWithSessions(
        this AzureServiceBusListenerConfiguration configuration,
        int maxParallelSessions = 10,
        string? endpointName = null)
    {
        // First ensure sessions are required with the specified parallelism
        configuration.RequireSessions(maxParallelSessions);
        
        // Then apply exclusive node configuration
        configuration.ExclusiveNodeWithSessionOrdering(maxParallelSessions, endpointName);
        
        return configuration;
    }

    /// <summary>
    /// Configure this Azure Service Bus topic subscription to run exclusively on a single node
    /// with parallel message processing. Useful for singleton consumers that still need throughput.
    /// </summary>
    /// <param name="configuration">The Azure Service Bus subscription configuration</param>
    /// <param name="maxParallelism">Maximum number of messages to process in parallel. Default is 10.</param>
    /// <param name="endpointName">Optional endpoint name for identification</param>
    /// <returns>The configuration for method chaining</returns>
    public static AzureServiceBusSubscriptionListenerConfiguration ExclusiveNodeWithParallelism(
        this AzureServiceBusSubscriptionListenerConfiguration configuration,
        int maxParallelism = 10,
        string? endpointName = null)
    {
        configuration.ExclusiveNodeWithParallelism(maxParallelism, endpointName);
        return configuration;
    }
}