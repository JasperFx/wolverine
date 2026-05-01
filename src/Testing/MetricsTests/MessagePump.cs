using System.Collections.Immutable;
using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using Wolverine;
using Wolverine.Configuration;
using Wolverine.ErrorHandling;
using Wolverine.Logging;
using Wolverine.Runtime.Agents;
using Wolverine.Runtime.Metrics;
using Wolverine.Runtime.Routing;
using Wolverine.Tracking;
using Wolverine.Transports;

namespace MetricsTests;

public class MessagePump : IAsyncDisposable
{
    private IHost _host = null!;

    public async Task StartHostAsync(WolverineMetricsMode mode)
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Metrics.Mode = mode;
                opts.OnAnyException().RetryTimes(3).Then.MoveToErrorQueue();
            }).StartAsync();

        // Metrics export shifted off the message bus and onto IWolverineObserver
        // in commit d999a2e5 ("Shifted the metrics accumulation publishing to be
        // through the observer instead of through messaging"). The previous
        // approach — `LocalQueueFor<MessageHandlingMetrics>` plus a static handler
        // — no longer fires because nothing publishes that message anymore. Swap
        // the runtime's observer for one that captures MessageHandlingMetricsExported
        // calls into MetricsCollectionHandler.Collected, which is what the tests
        // assert against. Done after StartAsync so the accumulator's background
        // export loop (started during host wire-up) reads the swapped observer
        // on its next sampling tick.
        var runtime = _host.GetRuntime();
        runtime.Observer = new MetricsCapturingObserver();
    }

    public async Task PumpMessagesAsync(WolverineMetricsMode mode, TimeSpan duration)
    {
        if (_host == null)
        {
            await StartHostAsync(mode);
        }
        
        MetricsCollectionHandler.Clear();
        
        var stopwatch = new Stopwatch();
        stopwatch.Start();

        while (stopwatch.ElapsedMilliseconds < duration.TotalMilliseconds)
        {
            var bus = _host!.MessageBus();
            for (int i = 0; i < 100; i++)
            {
                for (int j = 0; j < Random.Shared.Next(1, 5); j++)
                {
                    await bus.PublishAsync(new M1(Guid.CreateVersion7()));
                }
                
                for (int j = 0; j < Random.Shared.Next(1, 5); j++)
                {
                    await bus.PublishAsync(new M2(Guid.CreateVersion7()));
                }
                
                for (int j = 0; j < Random.Shared.Next(1, 5); j++)
                {
                    await bus.PublishAsync(new M3(Guid.CreateVersion7(), Random.Shared.Next(0, 10)));
                }
                
                for (int j = 0; j < Random.Shared.Next(1, 5); j++)
                {
                    await bus.PublishAsync(new M4(Guid.CreateVersion7(), Random.Shared.Next(0, 10)));
                }
                
                for (int j = 0; j < Random.Shared.Next(1, 5); j++)
                {
                    await bus.PublishAsync(new M5(Guid.CreateVersion7()));
                }

            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_host is IAsyncDisposable hostAsyncDisposable)
        {
            await hostAsyncDisposable.DisposeAsync();
        }
        else
        {
            _host.Dispose();
        }
    }
}

public static class MetricsCollectionHandler
{
    private static readonly object _lock = new();

    public static ImmutableArray<MessageHandlingMetrics> Collected { get; private set; }
        = ImmutableArray<MessageHandlingMetrics>.Empty;


    public static void Clear()
    {
        lock (_lock)
        {
            Collected = ImmutableArray<MessageHandlingMetrics>.Empty;
        }
    }

    public static void Handle(MessageHandlingMetrics metrics)
    {
        // The observer's export loop runs on a background task tick; concurrent
        // appenders are possible if a Clear() races with an in-flight tick. Lock
        // the swap to keep ImmutableArray.Add atomic against Clear.
        lock (_lock)
        {
            Collected = Collected.Add(metrics);
        }
    }
}

/// <summary>
/// Test-only IWolverineObserver that forwards MessageHandlingMetricsExported into
/// <see cref="MetricsCollectionHandler.Collected"/> so the tests in this assembly
/// can assert on accumulated metrics. Everything else is a no-op — the test host
/// uses NullMessageStore, so persistence-side observer hooks would either throw
/// or do nothing useful anyway.
/// </summary>
internal class MetricsCapturingObserver : IWolverineObserver
{
    public void MessageHandlingMetricsExported(MessageHandlingMetrics metrics)
    {
        MetricsCollectionHandler.Handle(metrics);
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
    public Task CircuitBreakerTripped(Endpoint endpoint, CircuitBreakerOptions options) => Task.CompletedTask;
    public Task CircuitBreakerReset(Endpoint endpoint) => Task.CompletedTask;
    public void PersistedCounts(Uri storeUri, PersistedCounts counts) { }
}

public record M1(Guid Id);
public record M2(Guid Id);

// Need to retry on errors to see that happen
public record M3(Guid Id, int Fails);
public record M4(Guid Id, int Fails);
public record M5(Guid Id);

public static class MessagesHandler
{
    public static async Task HandleAsync(M1 m)
    {
        await Task.Delay(Random.Shared.Next(25, 100));
    }
    
    public static async Task HandleAsync(M2 m)
    {
        await Task.Delay(Random.Shared.Next(25, 100));
    }
    
    public static async Task HandleAsync(M3 m, Envelope envelope)
    {
        if (m.Fails > envelope.Attempts)
        {
            throw new BadImageFormatException();
        }
        
        await Task.Delay(Random.Shared.Next(25, 100));
    }
    
    public static async Task HandleAsync(M4 m, Envelope envelope)
    {
        if (m.Fails > envelope.Attempts)
        {
            throw new DivideByZeroException();
        }
        
        await Task.Delay(Random.Shared.Next(25, 100));
    }
    
    public static async Task HandleAsync(M4 m)
    {
        await Task.Delay(Random.Shared.Next(25, 100));
    }
}