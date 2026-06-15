using System.Collections.Concurrent;
using Alba;
using IntegrationTests;
using JasperFx.Events;
using Marten;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Wolverine.Attributes;
using Wolverine.Marten;
using Wolverine.Runtime;
using Wolverine.Runtime.Agents;

namespace Wolverine.Http.Tests;

public record AppendedThing(System.Guid Id);

public static class AppendEventEndpoint
{
    // Appends an event through the outbox-enrolled Marten session. The appended event goes to the
    // event store, never to the message outbox, so message causation can't see it — the
    // EnableEventAppendTracking hook is what surfaces it to IWolverineObserver.EventsAppended.
    [WolverinePost("/append/event")]
    public static void Append(IDocumentSession session)
    {
        session.Events.StartStream(System.Guid.NewGuid(), new AppendedThing(System.Guid.NewGuid()));
    }
}

// CritterWatch #310 follow-up: store-agnostic event-append observation through Wolverine's
// IWolverineObserver.EventsAppended hook. The store-specific glue lives in Wolverine.Marten
// (NotifyObserverOfAppendedEvents), gated by WolverineOptions.Tracking.EnableEventAppendTracking, so
// observers depend only on Wolverine + JasperFx.Events — no Marten reference, no satellite packages.
public class event_append_observation
{
    private sealed class CapturingObserver : IWolverineObserver
    {
        public ConcurrentBag<IEvent> Events { get; } = new();

        public Task AssumedLeadership() => Task.CompletedTask;
        public Task NodeStarted() => Task.CompletedTask;
        public Task NodeStopped() => Task.CompletedTask;
        public Task AgentStarted(System.Uri agentUri) => Task.CompletedTask;
        public Task AgentStopped(System.Uri agentUri) => Task.CompletedTask;
        public Task AssignmentsChanged(AssignmentGrid grid, AgentCommands commands) => Task.CompletedTask;
        public Task StaleNodes(System.Collections.Generic.IReadOnlyList<WolverineNode> staleNodes) => Task.CompletedTask;
        public Task RuntimeIsFullyStarted() => Task.CompletedTask;
        public void EndpointAdded(Wolverine.Configuration.Endpoint endpoint) { }
        public void MessageRouted(System.Type messageType, Wolverine.Runtime.Routing.IMessageRouter router) { }
        public Task BackPressureTriggered(Wolverine.Configuration.Endpoint endpoint, Wolverine.Transports.IListeningAgent agent) => Task.CompletedTask;
        public Task BackPressureLifted(Wolverine.Configuration.Endpoint endpoint) => Task.CompletedTask;
        public Task ListenerLatched(Wolverine.Configuration.Endpoint endpoint) => Task.CompletedTask;
        public Task CircuitBreakerTripped(Wolverine.Configuration.Endpoint endpoint, Wolverine.ErrorHandling.CircuitBreakerOptions options) => Task.CompletedTask;
        public Task CircuitBreakerReset(Wolverine.Configuration.Endpoint endpoint) => Task.CompletedTask;
        public void PersistedCounts(System.Uri storeUri, Wolverine.Logging.PersistedCounts counts) { }
        public void MessageHandlingMetricsExported(Wolverine.Runtime.Metrics.MessageHandlingMetrics metrics) { }

        public void EventsAppended(IReadOnlyList<IEvent> events)
        {
            foreach (var e in events) Events.Add(e);
        }
    }

    private static async Task<IAlbaHost> buildHostAsync(bool trackingEnabled)
    {
        var schema = "append_" + System.Guid.NewGuid().ToString("N")[..8];
        var builder = WebApplication.CreateBuilder();

        builder.Host.UseWolverine(opts =>
        {
            opts.Tracking.EnableEventAppendTracking = trackingEnabled;
            opts.Discovery.DisableConventionalDiscovery();
            opts.Discovery.IncludeAssembly(typeof(event_append_observation).Assembly);
            opts.Policies.AutoApplyTransactions();

            opts.Services.AddMarten(m =>
            {
                m.Connection(Servers.PostgresConnectionString);
                m.DatabaseSchemaName = schema;
            }).IntegrateWithWolverine();
        });

        builder.Services.AddWolverineHttp();
        return await AlbaHost.For(builder, app => app.MapWolverineEndpoints());
    }

    [Fact]
    public async Task observer_is_notified_of_appended_events_when_tracking_enabled()
    {
        await using var host = await buildHostAsync(trackingEnabled: true);

        var observer = new CapturingObserver();
        host.Services.GetRequiredService<IWolverineRuntime>().Observer = observer;

        await host.Scenario(x =>
        {
            x.Post.Url("/append/event");
            x.StatusCodeShouldBe(204);
        });

        observer.Events.ShouldContain(e => e.EventType == typeof(AppendedThing));
    }

    [Fact]
    public async Task observer_is_not_notified_when_tracking_disabled()
    {
        await using var host = await buildHostAsync(trackingEnabled: false);

        var observer = new CapturingObserver();
        host.Services.GetRequiredService<IWolverineRuntime>().Observer = observer;

        await host.Scenario(x =>
        {
            x.Post.Url("/append/event");
            x.StatusCodeShouldBe(204);
        });

        observer.Events.ShouldBeEmpty();
    }
}
