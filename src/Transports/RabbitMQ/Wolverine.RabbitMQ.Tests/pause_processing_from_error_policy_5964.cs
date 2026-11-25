using System.Collections.Concurrent;
using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using RabbitMQ.Client;
using Shouldly;
using Wolverine.Configuration;
using Wolverine.ErrorHandling;
using Wolverine.Logging;
using Wolverine.Runtime;
using Wolverine.Runtime.Agents;
using Wolverine.Runtime.Metrics;
using Wolverine.Runtime.Routing;
using Wolverine.Tracking;
using Wolverine.Transports;
using Xunit;

namespace Wolverine.RabbitMQ.Tests;

/// <summary>
/// GH-5964. <c>OnException&lt;T&gt;().Requeue().AndPauseProcessing(...)</c> (or <c>.Discard().AndPauseProcessing(...)</c>)
/// on a Rabbit MQ listener left the queue with no consumer for the rest of the process: the agent never reached
/// Paused, nothing restarted it, <c>ListenerResumed</c> was never reported and <c>IHost.StopAsync()</c> hung. The pause
/// runs inside the failing message's own delivery callback, and stopping the listener from there waited on that very
/// callback. <c>error_policy_pause_is_observed_5945</c> pauses a stub listener, which has no consumer to wait on.
/// </summary>
public class pause_processing_from_error_policy_5964 : IAsyncLifetime
{
    private static readonly TimeSpan PauseTime = 2.Seconds();

    private readonly string _queueName = $"pause-5964-{Guid.NewGuid():N}";
    private readonly PauseRecordingObserver theObserver = new();
    private IHost? _host;
    private bool _stopped;

    public ValueTask InitializeAsync()
    {
        PausingPingHandler.Reset();
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (_host == null) return;

        if (!_stopped)
        {
            // Bounded, so a regression fails the test instead of hanging the suite
            try
            {
                await _host.StopAsync().WaitAsync(30.Seconds());
            }
            catch (TimeoutException)
            {
                return;
            }
        }

        _host.Dispose();
    }

    private async Task startHostAsync(Action<WolverineOptions> errorPolicy)
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.ServiceName = "Pause5964";
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(PausingPingHandler));

                opts.UseRabbitMq().AutoProvision().AutoPurgeOnStartup().DisableDeadLetterQueueing();
                opts.ListenToRabbitQueue(_queueName);
                opts.PublishMessage<PausingPing>().ToRabbitQueue(_queueName);

                errorPolicy(opts);
            }).StartAsync();

        var runtime = (WolverineRuntime)_host.GetRuntime();
        runtime.Observer = theObserver;

        theAgent().Status.ShouldBe(ListeningStatus.Accepting);
        (await consumerCountAsync()).ShouldBe(1u);
    }

    private IListenerCircuit theAgent()
    {
        return _host!.GetRuntime().Endpoints.FindListeningAgent(new Uri($"rabbitmq://queue/{_queueName}"))!;
    }

    [Fact]
    public async Task requeue_and_pause_processing_pauses_resumes_and_handles_both_messages()
    {
        await startHostAsync(opts =>
            opts.OnException<TimeoutException>().Requeue().AndPauseProcessing(PauseTime));

        var failing = Guid.NewGuid();
        await _host!.MessageBus().PublishAsync(new PausingPing(failing, FailOnce: true));

        await pausesThenResumesAsync();

        // Requeued, so the message that failed comes round again once the listener is back
        await waitUntilAsync(() => Task.FromResult(PausingPingHandler.Handled.ContainsKey(failing)), 30.Seconds(),
            "the requeued message should be handled after the listener resumes");

        await aLaterMessageIsHandledAsync();
        await stopsWithinABoundedTimeAsync();

        PausingPingHandler.Handled[failing].ShouldBe(1);
    }

    [Fact]
    public async Task discard_and_pause_processing_pauses_resumes_and_handles_a_later_message()
    {
        await startHostAsync(opts =>
            opts.OnException<TimeoutException>().Discard().AndPauseProcessing(PauseTime));

        var failing = Guid.NewGuid();
        await _host!.MessageBus().PublishAsync(new PausingPing(failing, FailOnce: true));

        await pausesThenResumesAsync();

        await aLaterMessageIsHandledAsync();

        // Discarded, so it is never handled
        PausingPingHandler.Handled.ContainsKey(failing).ShouldBeFalse();

        await stopsWithinABoundedTimeAsync();
    }

    private async Task pausesThenResumesAsync()
    {
        await waitUntilAsync(() => Task.FromResult(theObserver.Paused.Count == 1), 15.Seconds(),
            "the error policy's pause should be reported to ListenerPaused");

        await waitUntilAsync(() => Task.FromResult(theAgent().Status == ListeningStatus.Paused), PauseTime,
            "the listener should report Paused while the error policy pause runs");

        (await consumerCountAsync()).ShouldBe(0u, "nothing should consume from the queue during the pause");
        PausingPingHandler.Handled.ShouldBeEmpty("nothing should be handled during the pause");

        await waitUntilAsync(() => Task.FromResult(theObserver.Resumed.Count == 1), 15.Seconds(),
            "the end of the pause should be reported to ListenerResumed");

        await waitUntilAsync(() => Task.FromResult(theAgent().Status == ListeningStatus.Accepting), 15.Seconds(),
            "the listener should be Accepting again after the pause");

        await waitUntilAsync(async () => await consumerCountAsync() == 1u, 15.Seconds(),
            "the restarted listener should be consuming from the queue again");
    }

    private async Task aLaterMessageIsHandledAsync()
    {
        var later = Guid.NewGuid();
        await _host!.MessageBus().PublishAsync(new PausingPing(later, FailOnce: false));

        await waitUntilAsync(() => Task.FromResult(PausingPingHandler.Handled.ContainsKey(later)), 30.Seconds(),
            "a message published after the pause should be handled");
    }

    private async Task stopsWithinABoundedTimeAsync()
    {
        var stopping = _host!.StopAsync();
        var finished = await Task.WhenAny(stopping, Task.Delay(30.Seconds()));
        finished.ShouldBeSameAs(stopping, "IHost.StopAsync() should not hang after an error policy pause");
        await stopping;
        _stopped = true;
    }

    /// <summary>The number of consumers the broker sees on the queue, read over a connection of its own.</summary>
    private async Task<uint> consumerCountAsync()
    {
        var factory = new ConnectionFactory { HostName = "localhost" };
        await using var connection = await factory.CreateConnectionAsync();
        await using var channel = await connection.CreateChannelAsync();
        var result = await channel.QueueDeclarePassiveAsync(_queueName);
        return result.ConsumerCount;
    }

    private static async Task waitUntilAsync(Func<Task<bool>> condition, TimeSpan timeout, string because)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await condition()) return;
            await Task.Delay(50);
        }

        (await condition()).ShouldBeTrue(because);
    }
}

public record PausingPing(Guid Id, bool FailOnce);

public static class PausingPingHandler
{
    private static int _failures;

    public static ConcurrentDictionary<Guid, int> Handled { get; } = new();

    public static void Reset()
    {
        Handled.Clear();
        _failures = 0;
    }

    public static void Handle(PausingPing ping)
    {
        // Fails the first time only
        if (ping.FailOnce && Interlocked.Increment(ref _failures) == 1)
        {
            throw new TimeoutException("the downstream service is slow");
        }

        Handled.AddOrUpdate(ping.Id, 1, (_, count) => count + 1);
    }
}

internal class PauseRecordingObserver : IWolverineObserver
{
    public ConcurrentQueue<(Endpoint Endpoint, TimeSpan PauseTime, Exception? Exception)> Paused { get; } = new();
    public ConcurrentQueue<Endpoint> Resumed { get; } = new();

    public Task ListenerPaused(Endpoint endpoint, TimeSpan pauseTime, Exception? exception)
    {
        Paused.Enqueue((endpoint, pauseTime, exception));
        return Task.CompletedTask;
    }

    public Task ListenerResumed(Endpoint endpoint)
    {
        Resumed.Enqueue(endpoint);
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
    public Task CircuitBreakerTripped(Endpoint endpoint, CircuitBreakerOptions options) => Task.CompletedTask;
    public Task CircuitBreakerReset(Endpoint endpoint) => Task.CompletedTask;
    public void PersistedCounts(Uri storeUri, PersistedCounts counts) { }
    public void MessageHandlingMetricsExported(MessageHandlingMetrics metrics) { }
}
