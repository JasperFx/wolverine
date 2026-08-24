using JasperFx.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Wolverine;
using Wolverine.Logging;
using Wolverine.Runtime;
using Wolverine.Transports.Sending;
using Wolverine.Transports.Stub;
using Xunit;

namespace CoreTests.Runtime;

/// <summary>
/// GH-3709. <see cref="BufferedSendingAgent" /> hands out POOLED outgoing envelopes
/// (<c>WolverineRuntime.AcquireOutgoingEnvelope</c>, wolverine#2955) and its <c>storeAndForwardAsync</c> does
/// nothing but post the envelope to an in-memory block. The block's consumer then sends it, succeeds, and
/// returns it to the pool -- <c>Envelope.Reset()</c>, which nulls <c>Destination</c> and <c>MessageType</c>
/// and clears <c>FromPool</c> -- so any read of that envelope after the post races a thread that is actively
/// blanking it.
/// </summary>
/// <remarks>
/// <para>The read that existed was <c>_messageLogger.Sent(envelope)</c> at the end of
/// <c>SendingAgent.StoreAndForwardAsync</c>, and the symptom was an intermittent
/// <see cref="NullReferenceException" /> out of <c>Envelope.ToMetricsHeaders()</c>: <c>Destination</c> passed
/// its own null guard and was null one line later at <c>Destination.ToString()</c>.</para>
///
/// <para>It was found through <see cref="Wolverine.Configuration.EndpointMode.NativeAck" />, which mapped to
/// <see cref="BufferedSendingAgent" /> at the time. GH-4061 has since moved NativeAck onto
/// <see cref="InlineSendingAgent" />, so that particular trigger is gone -- but nothing about the race was
/// ever native-ack specific. The remaining exposure is any BufferedInMemory endpoint, which is what both
/// tests below drive.</para>
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

    private static StubEndpoint bufferedEndpoint() => new("pooled-metrics-3709", new StubTransport());

    private BufferedSendingAgent agentFor(StubEndpoint endpoint, ISender sender, IMessageTracker tracker)
    {
        return new BufferedSendingAgent(NullLogger.Instance, tracker, sender, theRuntime.DurabilitySettings,
            endpoint, theRuntime, null);
    }

    private Envelope pooledEnvelopeFor(BufferedSendingAgent agent, Uri destination, int number)
    {
        var envelope = theRuntime.AcquireOutgoingEnvelope(agent);

        // Only a pooled envelope can be recycled out from under the caller, so a run where the pool gate
        // declined would prove nothing.
        envelope.FromPool.ShouldBeTrue();

        envelope.Message = new PooledMetricsProbe(number);
        envelope.Destination = destination;
        envelope.Sender = agent;

        return envelope;
    }

    /// <summary>
    /// The deterministic statement of the fix: for a pooled envelope the metrics read happens BEFORE the
    /// envelope is handed to the sending block, so the recycle can never get there first.
    /// </summary>
    /// <remarks>
    /// The gate is what makes this deterministic rather than a race the test hopes to lose. The stand-in
    /// tracker spins inside <c>Sent()</c> until the envelope has been recycled -- <c>FromPool</c> is cleared
    /// by <c>Envelope.Reset()</c>, so it is the recycle flag -- or a short deadline passes. With the fix
    /// nothing has been posted yet when <c>Sent()</c> runs, so no recycle is possible, the spin times out and
    /// the envelope is read intact. Without it, <c>Sent()</c> runs after the post and the spin waits for
    /// exactly the recycle it is racing, so <c>Destination</c> is reliably null by the time it is read.
    /// </remarks>
    [Fact]
    public async Task a_pooled_envelope_is_read_for_metrics_before_it_is_handed_to_the_sending_block()
    {
        var endpoint = bufferedEndpoint();
        var tracker = Substitute.For<IMessageTracker>();

        Uri? observedDestination = null;
        var observedAtAll = false;

        tracker.When(x => x.Sent(Arg.Any<Envelope>())).Do(call =>
        {
            var envelope = call.Arg<Envelope>();

            var deadline = DateTimeOffset.UtcNow.Add(2.Seconds());
            while (envelope.FromPool && DateTimeOffset.UtcNow < deadline)
            {
                Thread.Sleep(1);
            }

            observedDestination = envelope.Destination;
            observedAtAll = true;
        });

        var agent = agentFor(endpoint, new ImmediateSender(endpoint.Uri), tracker);

        await agent.StoreAndForwardAsync(pooledEnvelopeFor(agent, endpoint.Uri, 0));

        observedAtAll.ShouldBeTrue("The message tracker was never called at all");
        observedDestination.ShouldBe(endpoint.Uri,
            "The metrics hook observed a recycled envelope -- the pooled read must happen before the handoff");
    }

    /// <summary>
    /// The same invariant against the REAL <see cref="WolverineRuntime" /> message tracker under concurrency,
    /// because <c>Sent()</c> is what actually walks the envelope to build its metric tags. This is the shape
    /// the bug was originally caught in, so it stays -- but it is a probabilistic reproduction that depends on
    /// machine load, which is why the deterministic gate above is the real guard.
    /// </summary>
    [Fact]
    public async Task metrics_never_observe_a_recycled_envelope_under_concurrent_buffered_sends()
    {
        var endpoint = bufferedEndpoint();
        var sender = new ImmediateSender(endpoint.Uri);
        var agent = agentFor(endpoint, sender, theRuntime.MessageTracking);

        await Parallel.ForEachAsync(Enumerable.Range(0, Messages),
            new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount },
            async (i, _) => await agent.StoreAndForwardAsync(pooledEnvelopeFor(agent, endpoint.Uri, i)));

        // Nothing to assert beyond "it did not throw" -- the failure mode is a NullReferenceException raised
        // inside the send, which Parallel.ForEachAsync surfaces straight out of the await above. Confirm the
        // sends really happened so a silently latched agent cannot pass this vacuously.
        await sender.WaitForAtLeastAsync(Messages, 30.Seconds());

        sender.Count.ShouldBeGreaterThanOrEqualTo(Messages);
    }

    public record PooledMetricsProbe(int Number);

    /// <summary>
    /// Sends synchronously and does nothing else, so the block's consumer gets back to
    /// <c>sendWithExplicitHandlingAsync</c> -- and therefore to the pool release -- as fast as possible.
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
