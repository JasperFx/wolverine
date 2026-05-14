using Microsoft.Extensions.Hosting;
using Wolverine.Configuration;
using Wolverine.Configuration.Capabilities;
using Wolverine.ErrorHandling;
using Wolverine.Logging;
using Wolverine.Runtime;
using Wolverine.Runtime.Agents;
using Wolverine.Runtime.Metrics;
using Wolverine.Runtime.Routing;
using Wolverine.Tracking;
using Wolverine.Transports;
using Xunit;

namespace CoreTests.Acceptance;

/// <summary>
/// GH-2520: framework-internal message types must not leak into ServiceCapabilities
/// or be reported via IWolverineObserver hooks (MessageRouted / MessageCausedBy).
/// </summary>
public class system_message_type_filtering
{
    [Fact]
    public void IsSystemMessageType_recognizes_IInternalMessage()
    {
        typeof(SampleInternalMessage).IsSystemMessageType().ShouldBeTrue();
    }

    [Fact]
    public void IsSystemMessageType_recognizes_IAgentCommand()
    {
        typeof(SampleAgentCommand).IsSystemMessageType().ShouldBeTrue();
    }

    [Fact]
    public void IsSystemMessageType_recognizes_INotToBeRouted()
    {
        typeof(SampleNotToBeRouted).IsSystemMessageType().ShouldBeTrue();
    }

    [Fact]
    public void IsSystemMessageType_returns_false_for_normal_user_messages()
    {
        typeof(NormalUserMessage).IsSystemMessageType().ShouldBeFalse();
    }

    [Fact]
    public void IsSystemMessageType_returns_false_for_null()
    {
        ((Type?)null).IsSystemMessageType().ShouldBeFalse();
    }

    [Fact]
    public async Task service_capabilities_excludes_system_message_types()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery();
                opts.Discovery.IncludeType<SystemFilteringHandler>();
            })
            .StartAsync();

        var capabilities = await ServiceCapabilities.ReadFrom(host.GetRuntime(), null, CancellationToken.None);

        // Sanity: the normal user message should appear
        capabilities.Messages.ShouldContain(m => m.Type.FullName == typeof(NormalUserMessage).FullName);

        // The three system-marked types must NOT appear
        capabilities.Messages.ShouldNotContain(m => m.Type.FullName == typeof(SampleInternalMessage).FullName);
        capabilities.Messages.ShouldNotContain(m => m.Type.FullName == typeof(SampleAgentCommand).FullName);
        capabilities.Messages.ShouldNotContain(m => m.Type.FullName == typeof(SampleNotToBeRouted).FullName);
    }

    [Fact]
    public async Task observer_does_not_receive_message_routed_for_system_types()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery();
                opts.Discovery.IncludeType<SystemFilteringHandler>();
            })
            .StartAsync();

        var runtime = host.GetRuntime();

        // Swap in a recording observer after startup so we capture exactly the
        // post-startup MessageRouted calls we care about.
        var observer = new RecordingObserver();
        runtime.Observer = observer;

        // PrepopulateRoutingCache (#2769) primes the router cache at startup, so a
        // plain RoutingFor() call would hit the cache and skip the observer hook.
        // Clear each entry first so the cache-miss path runs and the system-type
        // filter around Observer.MessageRouted is exercised.
        runtime.ClearRoutingFor(typeof(NormalUserMessage));
        runtime.ClearRoutingFor(typeof(SampleInternalMessage));
        runtime.ClearRoutingFor(typeof(SampleAgentCommand));
        runtime.ClearRoutingFor(typeof(SampleNotToBeRouted));

        runtime.RoutingFor(typeof(NormalUserMessage));
        runtime.RoutingFor(typeof(SampleInternalMessage));
        runtime.RoutingFor(typeof(SampleAgentCommand));
        runtime.RoutingFor(typeof(SampleNotToBeRouted));

        // Normal type should be reported, system types should be filtered out
        observer.RoutedTypes.ShouldContain(typeof(NormalUserMessage));
        observer.RoutedTypes.ShouldNotContain(typeof(SampleInternalMessage));
        observer.RoutedTypes.ShouldNotContain(typeof(SampleAgentCommand));
        observer.RoutedTypes.ShouldNotContain(typeof(SampleNotToBeRouted));
    }
}

// Fixtures live in the test (user) assembly to prove the per-type filter works
// regardless of which assembly declares the type.
public record NormalUserMessage(string Name);
public record SampleInternalMessage(string Note) : IInternalMessage;
public record SampleNotToBeRouted(string Note) : INotToBeRouted;

public class SampleAgentCommand : IAgentCommand
{
    public Task<AgentCommands> ExecuteAsync(IWolverineRuntime runtime, CancellationToken cancellationToken)
        => Task.FromResult(AgentCommands.Empty);
}

public class SystemFilteringHandler
{
    public void Handle(NormalUserMessage msg) { }
    public void Handle(SampleInternalMessage msg) { }
    public void Handle(SampleNotToBeRouted msg) { }
    public void Handle(SampleAgentCommand cmd) { }
}

internal class RecordingObserver : IWolverineObserver
{
    public List<Type> RoutedTypes { get; } = new();

    public void MessageRouted(Type messageType, IMessageRouter router)
    {
        RoutedTypes.Add(messageType);
    }

    // All other members are no-ops (interface uses default implementations)
    public Task AssumedLeadership() => Task.CompletedTask;
    public Task NodeStarted() => Task.CompletedTask;
    public Task NodeStopped() => Task.CompletedTask;
    public Task AgentStarted(Uri agentUri) => Task.CompletedTask;
    public Task AgentStopped(Uri agentUri) => Task.CompletedTask;
    public Task AssignmentsChanged(AssignmentGrid grid, AgentCommands commands) => Task.CompletedTask;
    public Task StaleNodes(IReadOnlyList<WolverineNode> staleNodes) => Task.CompletedTask;
    public Task RuntimeIsFullyStarted() => Task.CompletedTask;
    public void EndpointAdded(Endpoint endpoint) { }
    public Task BackPressureTriggered(Endpoint endpoint, IListeningAgent agent) => Task.CompletedTask;
    public Task BackPressureLifted(Endpoint endpoint) => Task.CompletedTask;
    public Task ListenerLatched(Endpoint endpoint) => Task.CompletedTask;
    public Task CircuitBreakerTripped(Endpoint endpoint, CircuitBreakerOptions options) => Task.CompletedTask;
    public Task CircuitBreakerReset(Endpoint endpoint) => Task.CompletedTask;
    public void PersistedCounts(Uri storeUri, PersistedCounts counts) { }
    public void MessageHandlingMetricsExported(MessageHandlingMetrics metrics) { }
}
