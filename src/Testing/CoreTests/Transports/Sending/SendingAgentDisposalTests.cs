using JasperFx.Core;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Wolverine.ComplianceTests;
using Wolverine.Logging;
using Wolverine.Transports.Local;
using Wolverine.Transports.Sending;
using Xunit;

namespace CoreTests.Transports.Sending;

public class SendingAgentDisposalTests
{
    [Fact]
    public async Task circuit_watcher_dispose_stops_the_ping_loop()
    {
        using var completed = new ManualResetEvent(false);
        var circuit = new StubCircuit(int.MaxValue, completed) { RetryInterval = 20.Milliseconds() };

        var watcher = new CircuitWatcher(circuit, default);

        await waitUntilAsync(() => circuit.CallCount > 0);

        watcher.Dispose();

        // Dispose() cancels the loop but does not wait for an already-in-flight tick to observe
        // that cancellation, so settle first before taking the baseline -- otherwise a tick that
        // was already running when Dispose() was called could tick over during the assertion
        // window below and cause a spurious failure.
        await Task.Delay(200.Milliseconds());
        var countAtDispose = circuit.CallCount;
        await Task.Delay(200.Milliseconds());

        // Before the fix, Dispose() only released the Task wrapper -- pingUntilConnectedAsync kept
        // running against the caller's (still live) token, so this count kept climbing forever.
        circuit.CallCount.ShouldBe(countAtDispose);
    }

    [Fact]
    public async Task disposing_a_sending_agent_stops_its_circuit_watcher()
    {
        var sender = new AlwaysFailingSender();
        var endpoint = new LocalQueue("disposing-a-sending-agent-stops-its-circuit-watcher")
        {
            FailuresBeforeCircuitBreaks = 1,
            PingIntervalForCircuitResume = 20.Milliseconds()
        };

        var agent = new BufferedSendingAgent(
            NullLogger.Instance,
            Substitute.For<IMessageTracker>(),
            sender,
            new DurabilitySettings(),
            endpoint);

        // The one guaranteed failure trips the circuit breaker and starts the CircuitWatcher.
        await agent.EnqueueOutgoingAsync(ObjectMother.Envelope());

        await waitUntilAsync(() => sender.PingCount > 0);

        await agent.DisposeAsync();

        // See circuit_watcher_dispose_stops_the_ping_loop above: settle before taking the baseline
        // so an already-in-flight ping can't tick over during the assertion window below.
        await Task.Delay(200.Milliseconds());
        var pingCountAtDispose = sender.PingCount;
        await Task.Delay(200.Milliseconds());

        // Before the fix, SendingAgent.DisposeAsync() never touched the CircuitWatcher, so a sender
        // pointed at a permanently unreachable destination (e.g. Kafka against a dead broker) kept
        // pinging forever in the background -- keeping the owning process alive indefinitely.
        sender.PingCount.ShouldBe(pingCountAtDispose);
    }

    private static async Task waitUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 200; i++)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(10);
        }

        throw new TimeoutException("Condition was never met");
    }
}

internal class AlwaysFailingSender : ISender
{
    private int _pingCount;

    public int PingCount => _pingCount;

    public bool SupportsNativeScheduledSend => false;
    public Uri Destination { get; } = "local://always-failing".ToUri();

    public Task<bool> PingAsync()
    {
        Interlocked.Increment(ref _pingCount);
        return Task.FromResult(false);
    }

    public ValueTask SendAsync(Envelope envelope) => throw new Exception("Nope!");
}
