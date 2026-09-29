using System.Collections.Concurrent;
using IntegrationTests;
using JasperFx.Events;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Marten;
using Wolverine.Marten.Publishing;
using Wolverine.Runtime;
using Wolverine.Runtime.Agents;
using Wolverine.Tracking;

namespace MartenTests.Bugs;

// With UseFastEventForwarding, Wolverine forwards the events appended to a Marten session to its handlers.
// That used to happen only for sessions opened by OutboxedSessionFactory, so a session enrolled through
// IMartenOutbox.Enroll() -- the documented way to use the outbox outside of a handler -- silently forwarded
// nothing. Enroll() now wires the same listeners the factory does, and never doubles up on a session that
// already carries them.
public class enrolled_outbox_forwards_events
{
    private static async Task<IHost> startHostAsync(bool useFastEventForwarding, bool trackAppends = false)
    {
        return await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(EnrolledThingHappenedHandler));
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.Tracking.EnableEventAppendTracking = trackAppends;

                opts.Services.AddMarten(m =>
                    {
                        m.Connection(Servers.PostgresConnectionString);
                        m.DatabaseSchemaName = "enrolled_forwarding";
                    })
                    .IntegrateWithWolverine(x => x.UseFastEventForwarding = useFastEventForwarding);
            }).StartAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task a_session_from_the_document_store_forwards_events_once_enrolled()
    {
        using var host = await startHostAsync(true);
        var streamId = Guid.NewGuid();

        var tracked = await host.ExecuteAndWaitAsync(async () =>
        {
            using var scope = host.Services.CreateScope();
            var outbox = scope.ServiceProvider.GetRequiredService<IMartenOutbox>();
            var store = scope.ServiceProvider.GetRequiredService<IDocumentStore>();

            // Straight from the store this is a plain Marten session: no outbox, no forwarding
            await using var session = store.LightweightSession();
            outbox.Enroll(session);

            session.Events.StartStream(streamId, new EnrolledThingHappened(streamId));
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }, 15000);

        tracked.Executed.SingleMessage<IEvent<EnrolledThingHappened>>()
            .Data.StreamId.ShouldBe(streamId);
    }

    [Fact]
    public async Task the_session_of_the_scoped_outbox_forwards_events()
    {
        using var host = await startHostAsync(true);
        var streamId = Guid.NewGuid();

        var tracked = await host.ExecuteAndWaitAsync(async () =>
        {
            // The scoped IMartenOutbox enrolls the scoped IDocumentSession as it's built
            using var scope = host.Services.CreateScope();
            var outbox = scope.ServiceProvider.GetRequiredService<IMartenOutbox>();
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            outbox.Session.ShouldBeSameAs(session);

            session.Events.StartStream(streamId, new EnrolledThingHappened(streamId));
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }, 15000);

        tracked.Executed.SingleMessage<IEvent<EnrolledThingHappened>>()
            .Data.StreamId.ShouldBe(streamId);
    }

    [Fact]
    public async Task an_enrolled_session_forwards_nothing_without_fast_event_forwarding()
    {
        using var host = await startHostAsync(false);
        var streamId = Guid.NewGuid();
        var store = host.Services.GetRequiredService<IDocumentStore>();

        var tracked = await host.ExecuteAndWaitAsync(async () =>
        {
            using var scope = host.Services.CreateScope();
            var outbox = scope.ServiceProvider.GetRequiredService<IMartenOutbox>();

            await using var session = store.LightweightSession();
            outbox.Enroll(session);

            session.Events.StartStream(streamId, new EnrolledThingHappened(streamId));
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }, 15000);

        // The unit of work did commit, it's only that nothing was forwarded
        await using var query = store.QuerySession();
        (await query.Events.FetchStreamAsync(streamId, token: TestContext.Current.CancellationToken))
            .Count.ShouldBe(1);

        tracked.Executed.MessagesOf<IEvent<EnrolledThingHappened>>().ShouldBeEmpty();
    }

    [Fact]
    public async Task enrolling_a_session_that_already_forwards_does_not_forward_twice()
    {
        using var host = await startHostAsync(true);
        var streamId = Guid.NewGuid();

        var tracked = await host.ExecuteAndWaitAsync(async context =>
        {
            // This session is already wired to forward events by the factory
            var factory = host.Services.GetRequiredService<OutboxedSessionFactory>();
            await using var session = factory.OpenSession(context);

            using var scope = host.Services.CreateScope();
            scope.ServiceProvider.GetRequiredService<IMartenOutbox>().Enroll(session);

            session.Events.StartStream(streamId, new EnrolledThingHappened(streamId));
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }, 15000);

        tracked.Executed.MessagesOf<IEvent<EnrolledThingHappened>>().Count().ShouldBe(1);
    }

    [Fact]
    public async Task enrolling_the_same_session_twice_does_not_forward_twice()
    {
        using var host = await startHostAsync(true);
        var streamId = Guid.NewGuid();

        var tracked = await host.ExecuteAndWaitAsync(async () =>
        {
            using var scope = host.Services.CreateScope();
            var outbox = scope.ServiceProvider.GetRequiredService<IMartenOutbox>();
            var store = scope.ServiceProvider.GetRequiredService<IDocumentStore>();

            await using var session = store.LightweightSession();
            outbox.Enroll(session);
            outbox.Enroll(session);

            session.Events.StartStream(streamId, new EnrolledThingHappened(streamId));
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }, 15000);

        tracked.Executed.MessagesOf<IEvent<EnrolledThingHappened>>().Count().ShouldBe(1);
    }

    [Fact]
    public async Task an_enrolled_session_notifies_the_observer_of_appended_events_once()
    {
        using var host = await startHostAsync(false, true);
        var observer = new CapturingObserver();
        host.Services.GetRequiredService<IWolverineRuntime>().Observer = observer;
        var streamId = Guid.NewGuid();

        using var scope = host.Services.CreateScope();
        var outbox = scope.ServiceProvider.GetRequiredService<IMartenOutbox>();
        var store = scope.ServiceProvider.GetRequiredService<IDocumentStore>();

        await using var session = store.LightweightSession();
        outbox.Enroll(session);
        outbox.Enroll(session);

        session.Events.StartStream(streamId, new EnrolledThingHappened(streamId));
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);

        observer.Events.Count(x => x.Data is EnrolledThingHappened).ShouldBe(1);
    }

    [Fact]
    public async Task enrolling_a_session_that_already_notifies_the_observer_does_not_notify_twice()
    {
        using var host = await startHostAsync(false, true);
        var observer = new CapturingObserver();
        host.Services.GetRequiredService<IWolverineRuntime>().Observer = observer;
        var streamId = Guid.NewGuid();

        await host.ExecuteAndWaitAsync(async context =>
        {
            var factory = host.Services.GetRequiredService<OutboxedSessionFactory>();
            await using var session = factory.OpenSession(context);

            using var scope = host.Services.CreateScope();
            scope.ServiceProvider.GetRequiredService<IMartenOutbox>().Enroll(session);

            session.Events.StartStream(streamId, new EnrolledThingHappened(streamId));
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }, 15000);

        observer.Events.Count(x => x.Data is EnrolledThingHappened).ShouldBe(1);
    }

    private sealed class CapturingObserver : IWolverineObserver
    {
        public ConcurrentBag<IEvent> Events { get; } = new();

        public Task AssumedLeadership() => Task.CompletedTask;
        public Task NodeStarted() => Task.CompletedTask;
        public Task NodeStopped() => Task.CompletedTask;
        public Task AgentStarted(Uri agentUri) => Task.CompletedTask;
        public Task AgentStopped(Uri agentUri) => Task.CompletedTask;
        public Task AssignmentsChanged(AssignmentGrid grid, AgentCommands commands) => Task.CompletedTask;
        public Task StaleNodes(IReadOnlyList<WolverineNode> staleNodes) => Task.CompletedTask;
        public Task RuntimeIsFullyStarted() => Task.CompletedTask;
        public void EndpointAdded(Wolverine.Configuration.Endpoint endpoint) { }
        public void MessageRouted(Type messageType, Wolverine.Runtime.Routing.IMessageRouter router) { }
        public Task BackPressureTriggered(Wolverine.Configuration.Endpoint endpoint, Wolverine.Transports.IListeningAgent agent) => Task.CompletedTask;
        public Task BackPressureLifted(Wolverine.Configuration.Endpoint endpoint) => Task.CompletedTask;
        public Task ListenerLatched(Wolverine.Configuration.Endpoint endpoint) => Task.CompletedTask;
        public Task CircuitBreakerTripped(Wolverine.Configuration.Endpoint endpoint, Wolverine.ErrorHandling.CircuitBreakerOptions options) => Task.CompletedTask;
        public Task CircuitBreakerReset(Wolverine.Configuration.Endpoint endpoint) => Task.CompletedTask;
        public void PersistedCounts(Uri storeUri, Wolverine.Logging.PersistedCounts counts) { }
        public void MessageHandlingMetricsExported(Wolverine.Runtime.Metrics.MessageHandlingMetrics metrics) { }

        public void EventsAppended(IReadOnlyList<IEvent> events)
        {
            foreach (var e in events) Events.Add(e);
        }
    }
}

public record EnrolledThingHappened(Guid StreamId);

public static class EnrolledThingHappenedHandler
{
    public static void Handle(IEvent<EnrolledThingHappened> _)
    {
    }
}
