using System.Text;
using JasperFx.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Configuration;
using Wolverine.Runtime;
using Wolverine.Runtime.Partitioning;
using Wolverine.Runtime.WorkerQueues;
using Wolverine.Transports;
using Wolverine.Transports.Stub;
using Xunit;

namespace CoreTests.Runtime.WorkerQueues;

/// <summary>
///     GH-5443. On a group-partitioned NativeAck endpoint, deserialization runs upstream of the slot lanes in
///     ShardedExecutionBlock.DeserializeFirst. An envelope that fails there (unknown message type, unreadable
///     payload) is settled by its continuation and never reaches executeAsync -- which is the only place the
///     lease tracker (and the in-memory idempotency guard) are ever released.
/// </summary>
public class native_ack_partitioned_deserialize_failure_5443 : IAsyncLifetime
{
    private IHost _host = null!;
    private IWolverineRuntime theRuntime = null!;

    public async ValueTask InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts => opts.Discovery.IncludeType<LeasePingHandler>())
            .StartAsync(TestContext.Current.CancellationToken);

        theRuntime = _host.Services.GetRequiredService<IWolverineRuntime>();
        LeasePingHandler.Handled.Clear();
        LeasePingHandler.Gate = null;
        LeasePingHandler.Entered = null;
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    private (NativeAckReceiver Receiver, Endpoint Endpoint) receiverFor(bool partitioned, bool idempotency = false)
    {
        var endpoint = new LeasedStubEndpoint("leased", new StubTransport());
        if (partitioned)
        {
            endpoint.GroupShardingSlotNumber = PartitionSlots.Five;
        }

        if (idempotency)
        {
            endpoint.InMemoryIdempotency = new InMemoryIdempotencySettings();
        }

        endpoint.Mode = EndpointMode.NativeAck;
        endpoint.Compile(theRuntime);

        var receiver = new NativeAckReceiver(endpoint, theRuntime,
            new HandlerPipeline((WolverineRuntime)theRuntime, (WolverineRuntime)theRuntime, endpoint));

        return (receiver, endpoint);
    }

    // A message type this application has never heard of: the NodeRuntimeMetricsReported case from the issue
    private static Envelope unknownTypeEnvelope()
    {
        return new Envelope
        {
            Id = Guid.NewGuid(),
            MessageType = "Some.Newer.Client.NodeRuntimeMetricsReported",
            ContentType = EnvelopeConstants.JsonContentType,
            Data = Encoding.UTF8.GetBytes("{\"Name\":\"whatever\"}")
        };
    }

    [Theory]
    [InlineData(false)] // control: deserialization happens inside executeAsync
    [InlineData(true)]  // GH-5443: deserialization happens in DeserializeFirst, upstream of executeAsync
    public async Task an_unknown_message_type_is_settled_and_stops_being_tracked(bool partitioned)
    {
        var (receiver, _) = receiverFor(partitioned);
        var listener = new FakeLeaseListener();

        await receiver.ReceivedAsync(listener, unknownTypeEnvelope());

        receiver.Leases.ShouldNotBeNull();
        var tracker = receiver.Leases!;

        // The no-handler continuation completes the delivery at the broker...
        await waitUntil(() => listener.Completed.Count == 1);

        // ...so nothing should still be holding a lease on it
        await waitUntil(() => tracker.InFlightCount == 0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task a_settled_unknown_message_type_releases_its_idempotency_claim(bool partitioned)
    {
        var (receiver, endpoint) = receiverFor(partitioned, idempotency: true);
        var guard = (GenerationalIdempotencyGuard)endpoint.IdempotencyGuard!;
        var listener = new FakeLeaseListener();

        await receiver.ReceivedAsync(listener, unknownTypeEnvelope());

        await waitUntil(() => listener.Completed.Count == 1);

        // Settled, so the id leaves the in-flight set -- and because the broker will not redeliver a completed
        // delivery, it is remembered as processed rather than released
        await waitUntil(() => guard.InFlightCount == 0);
        guard.TrackedCount.ShouldBe(1);
    }

    [Fact]
    public async Task a_continuation_that_throws_still_releases_the_lease_and_the_claim()
    {
        var endpoint = new LeasedStubEndpoint("leased", new StubTransport())
        {
            GroupShardingSlotNumber = PartitionSlots.Five,
            InMemoryIdempotency = new InMemoryIdempotencySettings()
        };
        endpoint.Mode = EndpointMode.NativeAck;
        endpoint.Compile(theRuntime);

        var guard = (GenerationalIdempotencyGuard)endpoint.IdempotencyGuard!;
        var receiver = new NativeAckReceiver(endpoint, theRuntime, new ThrowingContinuationPipeline());
        var listener = new FakeLeaseListener();

        await receiver.ReceivedAsync(listener, unknownTypeEnvelope());

        receiver.Leases.ShouldNotBeNull();
        var tracker = receiver.Leases!;

        // Nothing settled it, so nothing may keep renewing it either: letting the lease lapse is how the broker
        // gets it back. And the claim is RELEASED, not remembered, so that redelivery is allowed to run.
        await waitUntil(() => tracker.InFlightCount == 0);
        await waitUntil(() => guard.InFlightCount == 0);
        guard.TrackedCount.ShouldBe(0);
        listener.Completed.ShouldBeEmpty();
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
///     Deserialization "fails" and the resulting continuation throws while trying to settle the delivery -- the
///     one way out of DeserializeFirst that neither settles the envelope nor posts it to a lane.
/// </summary>
internal class ThrowingContinuationPipeline : IHandlerPipeline
{
    public Task InvokeAsync(Envelope envelope, IChannelCallback channel)
    {
        throw new NotSupportedException("the envelope should never reach a lane");
    }

    public Task InvokeAsync(Envelope envelope, IChannelCallback channel, System.Diagnostics.Activity activity)
    {
        return InvokeAsync(envelope, channel);
    }

    public ValueTask<IContinuation> TryDeserializeEnvelope(Envelope envelope)
    {
        return ValueTask.FromResult<IContinuation>(new ThrowingContinuation());
    }

    private class ThrowingContinuation : IContinuation
    {
        public ValueTask ExecuteAsync(IEnvelopeLifecycle lifecycle, IWolverineRuntime runtime, DateTimeOffset now,
            System.Diagnostics.Activity? activity)
        {
            throw new TimeoutException("the broker is not answering");
        }
    }
}
