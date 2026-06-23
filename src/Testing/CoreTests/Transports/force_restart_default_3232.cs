using Wolverine;
using Wolverine.Configuration;
using Wolverine.Transports;
using Shouldly;
using Xunit;

namespace CoreTests.Transports;

// GH-3232: IListenerCircuit.RestartAsync has a default interface implementation that delegates to StartAsync, so
// circuits without a real transport listener (in-memory local queues) get the gentle behavior for free. Only
// ListeningAgent overrides it with the force teardown+rebuild (covered by RabbitMQ integration tests).
public class force_restart_default_3232
{
    [Fact]
    public async Task default_restart_delegates_to_start_async()
    {
        IListenerCircuit circuit = new FakeCircuit();

        await circuit.RestartAsync();        // default param force: true
        await circuit.RestartAsync(force: false);

        ((FakeCircuit)circuit).StartCount.ShouldBe(2);
    }

    private class FakeCircuit : IListenerCircuit
    {
        public int StartCount;

        public ListeningStatus Status => ListeningStatus.Stopped;
        public Endpoint Endpoint => throw new NotSupportedException();
        public int QueueCount => 0;
        public ValueTask PauseAsync(TimeSpan pauseTime) => ValueTask.CompletedTask;
        public ValueTask PauseWithDrainAsync(TimeSpan pauseTime) => ValueTask.CompletedTask;

        public ValueTask StartAsync()
        {
            StartCount++;
            return ValueTask.CompletedTask;
        }

        public Task EnqueueDirectlyAsync(IEnumerable<Envelope> envelopes) => Task.CompletedTask;
    }
}
