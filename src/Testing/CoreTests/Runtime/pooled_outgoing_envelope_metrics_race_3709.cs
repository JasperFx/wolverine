using JasperFx.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Wolverine;
using Wolverine.Runtime;
using Wolverine.Transports.Sending;
using Wolverine.Transports.Stub;
using Xunit;

namespace CoreTests.Runtime;

/// <summary>
/// GH-3709. <see cref="BufferedSendingAgent" /> hands out POOLED outgoing envelopes
/// (<c>WolverineRuntime.AcquireOutgoingEnvelope</c>, wolverine#2955) and its <c>storeAndForwardAsync</c> does
/// nothing but post the envelope to an in-memory block. The block's consumer then sends it, succeeds, and
/// returns it to the pool -- <c>Envelope.Reset()</c>, which nulls <c>Destination</c> and <c>MessageType</c> --
/// which means any read of that envelope after the post is a data race with a thread that is actively
/// blanking it.
/// </summary>
/// <remarks>
/// The read that existed was <c>_messageLogger.Sent(envelope)</c> at the end of
/// <c>SendingAgent.StoreAndForwardAsync</c>, and the symptom was an intermittent
/// <see cref="NullReferenceException" /> out of <c>Envelope.ToMetricsHeaders()</c>: <c>Destination</c> passed
/// its own null guard and was null one line later at <c>Destination.ToString()</c>. It reproduced roughly one
/// run in four in the GH-3709 RabbitMQ suites, because <see cref="Wolverine.Configuration.EndpointMode.NativeAck" />
/// maps to <see cref="BufferedSendingAgent" />, but nothing about it is native-ack specific -- any
/// BufferedInMemory endpoint under concurrent publishing could hit it.
/// </remarks>
public class pooled_outgoing_envelope_metrics_race_3709 : IAsyncLifetime
{
    private const int Messages = 2000;

    private IHost _host = null!;
    private WolverineRuntime theRuntime = null!;

    public async ValueTask InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts => opts.Discovery.DisableConventionalDiscovery())
            .StartAsync(TestContext.Current.CancellationToken);

        theRuntime = (WolverineRuntime)_host.Services.GetRequiredService<IWolverineRuntime>();
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    /// <summary>
    /// The real <see cref="WolverineRuntime" /> message tracker is deliberately used rather than a
    /// substitute: <c>Sent()</c> is what walks the envelope to build its metric tags, so it is the thing
    /// that actually observes a half-blanked envelope.
    /// </summary>
    [Fact]
    public async Task metrics_never_observe_a_recycled_envelope_under_concurrent_buffered_sends()
    {
        var endpoint = new StubEndpoint("pooled-metrics-3709", new StubTransport());
        var sender = new ImmediateSender(endpoint.Uri);

        var agent = new BufferedSendingAgent(NullLogger.Instance, theRuntime.MessageTracking, sender,
            theRuntime.DurabilitySettings, endpoint, theRuntime, null);

        await Parallel.ForEachAsync(Enumerable.Range(0, Messages),
            new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount },
            async (i, _) =>
            {
                var envelope = theRuntime.AcquireOutgoingEnvelope(agent);

                // Only a pooled envelope can be recycled out from under the caller, so a run where the pool
                // gate declined would prove nothing.
                envelope.FromPool.ShouldBeTrue();

                envelope.Message = new PooledMetricsProbe(i);
                envelope.Destination = endpoint.Uri;
                envelope.Sender = agent;

                await agent.StoreAndForwardAsync(envelope);
            });

        // Nothing to assert beyond "it did not throw" -- the failure mode is a NullReferenceException raised
        // inside the send, which Parallel.ForEachAsync surfaces straight out of the await above. Confirm the
        // sends really happened so a silently latched agent cannot pass this vacuously.
        await sender.WaitForAtLeastAsync(Messages, 30.Seconds());

        sender.Count.ShouldBeGreaterThanOrEqualTo(Messages);
    }

    public record PooledMetricsProbe(int Number);

    /// <summary>
    /// Sends synchronously and does nothing else, so the block's consumer gets back to
    /// <c>sendWithExplicitHandlingAsync</c> -- and therefore to the pool release -- as fast as possible. That
    /// is what makes the race wide enough to catch.
    /// </summary>
    private class ImmediateSender : ISender
    {
        private int _count;

        public ImmediateSender(Uri destination)
        {
            Destination = destination;
        }

        public int Count => Volatile.Read(ref _count);

        public bool SupportsNativeScheduledSend => false;
        public Uri Destination { get; }

        public Task<bool> PingAsync() => Task.FromResult(true);

        public ValueTask SendAsync(Envelope envelope)
        {
            Interlocked.Increment(ref _count);
            return ValueTask.CompletedTask;
        }

        public async Task WaitForAtLeastAsync(int count, TimeSpan timeout)
        {
            var deadline = DateTimeOffset.UtcNow.Add(timeout);
            while (Count < count && DateTimeOffset.UtcNow < deadline)
            {
                await Task.Delay(25);
            }
        }
    }
}
