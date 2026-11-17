using System.Collections.Concurrent;
using JasperFx.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Configuration;
using Wolverine.ErrorHandling;
using Wolverine.Logging;
using Wolverine.Persistence.Durability;
using Wolverine.Runtime;
using Wolverine.Runtime.Agents;
using Wolverine.Runtime.Metrics;
using Wolverine.Runtime.Routing;
using Wolverine.Transports;
using Wolverine.Transports.Local;
using Wolverine.Transports.Stub;
using Xunit;

namespace CoreTests.ErrorHandling;

/// <summary>
/// GH-5446. IWolverineObserver declared CircuitBreakerReset beside CircuitBreakerTripped, but nothing ever raised
/// it, so an observer that raises something on a trip (CritterWatch's CircuitBreaker alert) could never clear it
/// from Wolverine's own signal -- whether the breaker recovered on its own after PauseTime or an operator restarted
/// the listener.
/// </summary>
public class circuit_breaker_reset_is_observed_5446 : IAsyncLifetime
{
    private IHost _host = null!;
    private WolverineRuntime theRuntime = null!;
    private IWolverineObserver _originalObserver = null!;

    public async ValueTask InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts => opts.Durability.Mode = DurabilityMode.Solo)
            .StartAsync(TestContext.Current.CancellationToken);

        theRuntime = (WolverineRuntime)_host.Services.GetRequiredService<IWolverineRuntime>();
        _originalObserver = theRuntime.Observer;
    }

    public async ValueTask DisposeAsync()
    {
        theRuntime.Observer = _originalObserver;
        await _host.StopAsync();
        _host.Dispose();
    }

    private async Task<ListeningAgent> startedAgentFor(IWolverineObserver observer, TimeSpan pauseTime,
        string name)
    {
        // The breaker captures the runtime's observer when the agent builds it
        theRuntime.Observer = observer;

        var endpoint = new StubEndpoint(name, new StubTransport())
        {
            Mode = EndpointMode.BufferedInMemory,
            CircuitBreakerOptions = new CircuitBreakerOptions
            {
                MinimumThreshold = 1,
                FailurePercentageThreshold = 10,
                PauseTime = pauseTime
            }
        };
        endpoint.Compile(theRuntime);

        var agent = new ListeningAgent(endpoint, theRuntime);
        await agent.StartAsync();
        agent.Status.ShouldBe(ListeningStatus.Accepting);

        return agent;
    }

    private static async Task tripAsync(ListeningAgent agent)
    {
        await agent.CircuitBreaker!.UpdateTotalsAsync(DateTimeOffset.UtcNow, 10, 10);
        agent.Status.ShouldBe(ListeningStatus.Paused);
    }

    [Fact]
    public async Task an_existing_observer_hears_the_reset_after_the_pause_time_elapses()
    {
        var observer = new LegacyBreakerObserver();
        await using var agent = await startedAgentFor(observer, 250.Milliseconds(), "legacy-5446");

        await tripAsync(agent);
        observer.Tripped.ShouldBe(1);

        await waitUntil(() => agent.Status == ListeningStatus.Accepting);
        await waitUntil(() => observer.Reset == 1);
    }

    [Fact]
    public async Task the_reset_after_the_pause_time_says_so_and_is_raised_once()
    {
        var observer = new ReasonBreakerObserver();
        await using var agent = await startedAgentFor(observer, 250.Milliseconds(), "elapsed-5446");

        await tripAsync(agent);

        await waitUntil(() => observer.Reasons.Count == 1);
        observer.Reasons.Single().ShouldBe(CircuitBreakerResetReason.PauseTimeElapsed);
        agent.CircuitBreaker!.IsAwaitingReset.ShouldBeFalse();

        await Task.Delay(300.Milliseconds(), TestContext.Current.CancellationToken);
        observer.Reasons.Count.ShouldBe(1);
    }

    [Fact]
    public async Task an_explicit_start_while_the_breaker_pause_is_pending_is_a_reset()
    {
        var observer = new ReasonBreakerObserver();
        await using var agent = await startedAgentFor(observer, 1.Hours(), "explicit-5446");

        await tripAsync(agent);
        observer.Reasons.ShouldBeEmpty();

        await agent.StartAsync();

        agent.Status.ShouldBe(ListeningStatus.Accepting);
        observer.Reasons.Single().ShouldBe(CircuitBreakerResetReason.ExplicitStart);

        // Once per trip: a later restart of a healthy listener is not another recovery
        await agent.RestartAsync(force: true);
        observer.Reasons.Count.ShouldBe(1);
    }

    [Fact]
    public async Task a_forced_restart_while_the_breaker_pause_is_pending_is_a_reset()
    {
        var observer = new ReasonBreakerObserver();
        await using var agent = await startedAgentFor(observer, 1.Hours(), "restart-5446");

        await tripAsync(agent);
        await agent.RestartAsync(force: true);

        agent.Status.ShouldBe(ListeningStatus.Accepting);
        observer.Reasons.Single().ShouldBe(CircuitBreakerResetReason.ExplicitStart);
    }

    [Fact]
    public async Task each_trip_gets_its_own_reset()
    {
        var observer = new ReasonBreakerObserver();
        await using var agent = await startedAgentFor(observer, 1.Hours(), "twice-5446");

        await tripAsync(agent);
        await agent.StartAsync();

        await tripAsync(agent);
        await agent.StartAsync();

        observer.Tripped.ShouldBe(2);
        observer.Reasons.Count.ShouldBe(2);
    }

    [Fact]
    public async Task a_pause_the_breaker_did_not_cause_is_never_reported_as_a_reset()
    {
        var observer = new ReasonBreakerObserver();
        await using var agent = await startedAgentFor(observer, 1.Hours(), "operator-5446");

        await agent.PauseAsync(250.Milliseconds());
        agent.Status.ShouldBe(ListeningStatus.Paused);

        await waitUntil(() => agent.Status == ListeningStatus.Accepting);
        await Task.Delay(100.Milliseconds(), TestContext.Current.CancellationToken);

        observer.Tripped.ShouldBe(0);
        observer.Reasons.ShouldBeEmpty();
    }

    [Fact]
    public async Task a_pause_that_supersedes_the_breakers_own_takes_the_reset_with_it()
    {
        var observer = new ReasonBreakerObserver();
        await using var agent = await startedAgentFor(observer, 1.Hours(), "superseded-5446");

        await tripAsync(agent);

        // An operator's pause replaces the breaker's: the start that ends it is the operator's, not a recovery
        await agent.PauseAsync(1.Hours());
        agent.CircuitBreaker!.IsAwaitingReset.ShouldBeFalse();

        await agent.StartAsync();

        agent.Status.ShouldBe(ListeningStatus.Accepting);
        observer.Reasons.ShouldBeEmpty();
    }

    [Fact]
    public async Task an_observer_that_throws_does_not_fail_the_start()
    {
        var observer = new ReasonBreakerObserver { ThrowOnReset = true };
        await using var agent = await startedAgentFor(observer, 1.Hours(), "throwing-5446");

        await tripAsync(agent);
        await agent.StartAsync();

        agent.Status.ShouldBe(ListeningStatus.Accepting);
        observer.Reasons.Count.ShouldBe(1);
    }

    [Fact]
    public async Task a_durable_local_queue_reports_the_reset_too()
    {
        var observer = new ReasonBreakerObserver();
        theRuntime.Observer = observer;

        var endpoint = new LocalQueue("durable-breaker-5446")
        {
            Mode = EndpointMode.Durable,
            CircuitBreakerOptions = new CircuitBreakerOptions
            {
                MinimumThreshold = 1,
                FailurePercentageThreshold = 10,
                PauseTime = 250.Milliseconds()
            }
        };
        endpoint.Compile(theRuntime);

        using var queue = new DurableLocalQueue(endpoint, theRuntime);
        IListenerCircuit circuit = queue;

        await queue.CircuitBreaker!.UpdateTotalsAsync(DateTimeOffset.UtcNow, 10, 10);
        circuit.Status.ShouldBe(ListeningStatus.Paused);

        await waitUntil(() => circuit.Status == ListeningStatus.Accepting);
        await waitUntil(() => observer.Reasons.Count == 1);
        observer.Reasons.Single().ShouldBe(CircuitBreakerResetReason.PauseTimeElapsed);
    }

    private static async Task waitUntil(Func<bool> condition)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (!condition() && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        condition().ShouldBeTrue();
    }
}

/// <summary>
/// An observer written before GH-5446, implementing only the one-argument CircuitBreakerReset. It has to keep
/// working untouched -- CritterWatch's observer is exactly this shape.
/// </summary>
internal class LegacyBreakerObserver : IWolverineObserver
{
    private int _tripped;
    private int _reset;

    public int Tripped => Volatile.Read(ref _tripped);
    public int Reset => Volatile.Read(ref _reset);

    public Task CircuitBreakerTripped(Endpoint endpoint, CircuitBreakerOptions options)
    {
        Interlocked.Increment(ref _tripped);
        return Task.CompletedTask;
    }

    public Task CircuitBreakerReset(Endpoint endpoint)
    {
        Interlocked.Increment(ref _reset);
        return Task.CompletedTask;
    }

    public Task AssumedLeadership() => Task.CompletedTask;
    public Task NodeStarted() => Task.CompletedTask;
    public Task NodeStopped() => Task.CompletedTask;
    public Task AgentStarted(Uri agentUri) => Task.CompletedTask;
    public Task AgentStopped(Uri agentUri) => Task.CompletedTask;
    public Task AssignmentsChanged(AssignmentGrid grid, AgentCommands commands) => Task.CompletedTask;
    public Task StaleNodes(IReadOnlyList<WolverineNode> staleNodes) => Task.CompletedTask;
    public Task RuntimeIsFullyStarted() => Task.CompletedTask;
    public void EndpointAdded(Endpoint endpoint) { }
    public void MessageRouted(Type messageType, IMessageRouter router) { }
    public Task BackPressureTriggered(Endpoint endpoint, IListeningAgent agent) => Task.CompletedTask;
    public Task BackPressureLifted(Endpoint endpoint) => Task.CompletedTask;
    public Task ListenerLatched(Endpoint endpoint) => Task.CompletedTask;
    public void PersistedCounts(Uri storeUri, PersistedCounts counts) { }
    public void MessageHandlingMetricsExported(MessageHandlingMetrics metrics) { }
}

/// <summary>An observer written against the GH-5446 overload, which carries the reason.</summary>
internal class ReasonBreakerObserver : LegacyBreakerObserver, IWolverineObserver
{
    public ConcurrentQueue<CircuitBreakerResetReason> Reasons { get; } = new();

    public bool ThrowOnReset { get; set; }

    public Task CircuitBreakerReset(Endpoint endpoint, CircuitBreakerResetReason reason)
    {
        Reasons.Enqueue(reason);

        if (ThrowOnReset)
        {
            throw new InvalidOperationException("the alert service is down");
        }

        return Task.CompletedTask;
    }
}
