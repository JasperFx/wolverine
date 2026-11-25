using System.Collections.Concurrent;
using JasperFx.Core;
using Microsoft.Extensions.Hosting;
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

namespace Wolverine.AzureServiceBus.Tests;

/// <summary>
/// GH-5964 on an Inline Azure Service Bus queue. The error policy's pause runs inside the processor's own message
/// handler, and <c>ServiceBusProcessor.StopProcessingAsync</c> waits for every running handler, so stopping the listener
/// from there waited on itself: the listener never reached Paused, never resumed, and the host would not stop.
/// </summary>
public class pause_processing_from_error_policy_5964 : IAsyncLifetime
{
    private static readonly TimeSpan PauseTime = 2.Seconds();

    private readonly string _queueName = $"pause-5964-{Guid.NewGuid():N}"[..20];
    private readonly AsbPauseRecordingObserver theObserver = new();
    private IHost? _host;
    private bool _stopped;

    public ValueTask InitializeAsync()
    {
        AsbPausingPingHandler.Reset();
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (_host != null)
        {
            if (!_stopped)
            {
                // Bounded, so a regression fails the test instead of hanging the suite
                try
                {
                    await _host.StopAsync().WaitAsync(30.Seconds());
                    _host.Dispose();
                }
                catch (TimeoutException)
                {
                    // Leave it: a host whose stop hung would hang its disposal too
                }
            }
            else
            {
                _host.Dispose();
            }
        }

        await AzureServiceBusTesting.DeleteAllEmulatorObjectsAsync();
    }

    private async Task startHostAsync(Action<WolverineOptions> errorPolicy)
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(AsbPausingPingHandler));

                opts.UseAzureServiceBusTesting().AutoProvision().AutoPurgeOnStartup();
                opts.ListenToAzureServiceBusQueue(_queueName).ProcessInline();
                opts.PublishMessage<AsbPausingPing>().ToAzureServiceBusQueue(_queueName);

                errorPolicy(opts);
            }).StartAsync();

        var runtime = (WolverineRuntime)_host.GetRuntime();
        runtime.Observer = theObserver;

        theAgent().Status.ShouldBe(ListeningStatus.Accepting);
    }

    private IListenerCircuit theAgent()
    {
        return _host!.GetRuntime().Endpoints.FindListeningAgent(new Uri($"asb://queue/{_queueName}"))!;
    }

    [Fact]
    public async Task requeue_and_pause_processing_pauses_resumes_and_handles_both_messages()
    {
        await startHostAsync(opts =>
            opts.OnException<TimeoutException>().Requeue().AndPauseProcessing(PauseTime));

        var failing = Guid.NewGuid();
        await _host!.MessageBus().PublishAsync(new AsbPausingPing(failing, FailOnce: true));

        await pausesThenResumesAsync();

        await waitUntilAsync(() => AsbPausingPingHandler.Handled.ContainsKey(failing), 30.Seconds(),
            "the requeued message should be handled after the listener resumes");

        await aLaterMessageIsHandledAsync();
        await stopsWithinABoundedTimeAsync();

        AsbPausingPingHandler.Handled[failing].ShouldBe(1);
    }

    [Fact]
    public async Task discard_and_pause_processing_pauses_resumes_and_handles_a_later_message()
    {
        await startHostAsync(opts =>
            opts.OnException<TimeoutException>().Discard().AndPauseProcessing(PauseTime));

        var failing = Guid.NewGuid();
        await _host!.MessageBus().PublishAsync(new AsbPausingPing(failing, FailOnce: true));

        await pausesThenResumesAsync();
        await aLaterMessageIsHandledAsync();
        await stopsWithinABoundedTimeAsync();

        // Discarded, so it is never handled
        AsbPausingPingHandler.Handled.ContainsKey(failing).ShouldBeFalse();
    }

    private async Task pausesThenResumesAsync()
    {
        await waitUntilAsync(() => theObserver.Paused.Count == 1, 30.Seconds(),
            "the error policy's pause should be reported to ListenerPaused");

        await waitUntilAsync(() => theAgent().Status == ListeningStatus.Paused, PauseTime,
            "the listener should report Paused while the error policy pause runs");

        await waitUntilAsync(() => theObserver.Resumed.Count == 1, 15.Seconds(),
            "the end of the pause should be reported to ListenerResumed");

        await waitUntilAsync(() => theAgent().Status == ListeningStatus.Accepting, 15.Seconds(),
            "the listener should be Accepting again after the pause");
    }

    private async Task aLaterMessageIsHandledAsync()
    {
        var later = Guid.NewGuid();
        await _host!.MessageBus().PublishAsync(new AsbPausingPing(later, FailOnce: false));

        await waitUntilAsync(() => AsbPausingPingHandler.Handled.ContainsKey(later), 30.Seconds(),
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

    private static async Task waitUntilAsync(Func<bool> condition, TimeSpan timeout, string because)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(50);
        }

        condition().ShouldBeTrue(because);
    }
}

public record AsbPausingPing(Guid Id, bool FailOnce);

public static class AsbPausingPingHandler
{
    private static int _failures;

    public static ConcurrentDictionary<Guid, int> Handled { get; } = new();

    public static void Reset()
    {
        Handled.Clear();
        _failures = 0;
    }

    public static void Handle(AsbPausingPing ping)
    {
        // Fails the first time only
        if (ping.FailOnce && Interlocked.Increment(ref _failures) == 1)
        {
            throw new TimeoutException("the downstream service is slow");
        }

        Handled.AddOrUpdate(ping.Id, 1, (_, count) => count + 1);
    }
}

internal class AsbPauseRecordingObserver : IWolverineObserver
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
