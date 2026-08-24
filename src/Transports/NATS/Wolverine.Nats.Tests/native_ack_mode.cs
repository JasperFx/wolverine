using System.Collections.Concurrent;
using JasperFx.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NATS.Client.Core;
using NATS.Client.JetStream;
using NATS.Client.JetStream.Models;
using NATS.Net;
using Shouldly;
using Wolverine.Configuration;
using Wolverine.Nats.Internal;
using Wolverine.Runtime;
using Wolverine.Transports;
using Xunit;

namespace Wolverine.Nats.Tests;

#region messages and handler

public record NativeAckWork(int Number);

/// <summary>
/// Every execution is recorded WITH THE HOST THAT RAN IT, and each host is parked independently.
/// </summary>
/// <remarks>
/// Both of those exist because the obvious shape of this fixture -- one shared bag of numbers and one shared
/// <c>TaskCompletionSource</c> -- makes the dead-node test vacuous, and it was caught passing under
/// <c>BufferedInMemory()</c>, the very mode it claims to distinguish itself from. <c>IHost.Dispose()</c> does not
/// run <c>StopAsync</c>, so the "dead" host's handlers are still parked in-process; releasing one shared gate
/// then let the ZOMBIE FIRST HOST fill in the numbers the test was attributing to a redelivery on the second.
/// Keyed by service name, "the second host handled it" is a claim the first host cannot satisfy.
/// </remarks>
public static class NativeAckWorkTracking
{
    /// <summary>Every execution, including a duplicate one -- the redelivery tests count entries, not distinct values.</summary>
    public static readonly ConcurrentBag<(string Service, int Number)> Handled = new();

    private static readonly ConcurrentDictionary<string, TaskCompletionSource> Blocks = new();

    /// <summary>Park every handler running in this host until <see cref="Release" /> -- a node that dies mid-flight.</summary>
    public static void Block(string service)
    {
        Blocks[service] = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public static void Release(string service)
    {
        if (Blocks.TryGetValue(service, out var tcs)) tcs.TrySetResult();
    }

    public static Task GateFor(string service) =>
        Blocks.TryGetValue(service, out var tcs) ? tcs.Task : Task.CompletedTask;

    public static void Reset()
    {
        Handled.Clear();
        foreach (var tcs in Blocks.Values) tcs.TrySetResult();
        Blocks.Clear();
    }

    public static int CountOf(int number) => Handled.Count(x => x.Number == number);

    public static IEnumerable<int> DistinctNumbersFor(string service, int start, int count)
    {
        return Handled
            .Where(x => x.Service == service && x.Number >= start && x.Number < start + count)
            .Select(x => x.Number)
            .Distinct()
            .OrderBy(x => x);
    }
}

public class NativeAckWorkHandler
{
    public async Task Handle(NativeAckWork message, IWolverineRuntime runtime)
    {
        var service = runtime.Options.ServiceName;

        await NativeAckWorkTracking.GateFor(service);

        NativeAckWorkTracking.Handled.Add((service, message.Number));
    }
}

#endregion

/// <summary>
/// GH-4053. NATS JetStream opts into <see cref="EndpointMode.NativeAck" />. The guarantee under test is the one
/// the mode exists for -- Buffered's throughput with Inline's no-loss behaviour -- plus the two things that are
/// specific to this transport: <c>AckWait</c> is a real clock that has to be renewed with <c>AckProgress</c>
/// while an envelope waits in a lane (GH-4048), and <c>MaxAckPending</c> is the prefetch that has to cover every
/// lane or the consumer stalls itself.
/// </summary>
/// <remarks>
/// Every test gets its own stream, subject and consumer name. These share one static tracking bag and one
/// broker, and the redelivery tests deliberately produce extra deliveries -- a shared subject would let one
/// test's redelivery land in another test's assertion.
/// </remarks>
[Collection("NATS Integration")]
public class native_ack_mode : IAsyncLifetime
{
    private readonly NatsContainerFixture _fixture;

    public native_ack_mode(NatsContainerFixture fixture)
    {
        _fixture = fixture;
    }

    public ValueTask InitializeAsync()
    {
        NativeAckWorkTracking.Reset();
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        NativeAckWorkTracking.Reset();
        return ValueTask.CompletedTask;
    }

    private sealed record Topology(string Stream, string Subject, string Consumer);

    private static Topology topologyFor(string label)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        return new Topology($"NACK_{label}_{suffix}".ToUpperInvariant(), $"nack.{label}.{suffix}",
            $"nack-{label}-{suffix}");
    }

    private Task<IHost> startHostAsync(Topology topology, string serviceName, TimeSpan? ackWait = null,
        Action<Wolverine.Nats.Configuration.NatsListenerConfiguration>? configure = null)
    {
        return Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.ServiceName = serviceName;

                opts.UseNats(_fixture.ConnectionString)
                    .AutoProvision()
                    .DefineWorkQueueStream(topology.Stream, _ => { }, topology.Subject);

                opts.Discovery.DisableConventionalDiscovery().IncludeType<NativeAckWorkHandler>();
                opts.Policies.DisableConventionalLocalRouting();

                var listener = opts.ListenToNatsSubject(topology.Subject)
                    .UseJetStream(topology.Stream, topology.Consumer)
                    .ProcessInParallelWithNativeAcks();

                if (ackWait.HasValue)
                {
                    listener.AckWait(ackWait.Value);
                }

                configure?.Invoke(listener);

                // Deliberately NOT SendInline(). Publishing to a subject this host also listens to resolves to
                // the SAME NatsEndpoint, and EndpointMode is one property on it -- so SendInline() would set
                // Mode = Inline and silently downgrade the listener out of NativeAck. It is also unnecessary:
                // NativeAck already sends through the inline sending agent (Endpoint.SendsInline, GH-4073).
                opts.PublishMessage<NativeAckWork>().ToNatsSubject(topology.Subject)
                    .UseJetStream(topology.Stream);
            }).StartAsync();
    }

    [Fact]
    public async Task the_endpoint_really_is_in_native_ack_mode()
    {
        var topology = topologyFor("mode");
        using var host = await startHostAsync(topology, "mode-host");
        var runtime = host.Services.GetRequiredService<IWolverineRuntime>();

        var endpoint = runtime.Endpoints
            .ActiveListeners()
            .Select(x => x.Endpoint)
            .OfType<NatsEndpoint>()
            .Single(x => x.Subject == topology.Subject);

        endpoint.Mode.ShouldBe(EndpointMode.NativeAck);

        // MaxAckPending IS the back pressure for this mode, so no in-process BackPressureAgent
        endpoint.ShouldEnforceBackPressure().ShouldBeFalse();

        // GH-4048: AckWait is a real clock, so this endpoint has to declare the lease -- and having declared it,
        // ListeningAgent.assertLeaseRenewalContract refuses to START a NativeAck listener that cannot renew.
        // The successful bootstrap above is therefore the real assertion that NatsListener implements
        // ISupportLeaseRenewal; these two name the halves so a regression says which one broke.
        endpoint.holdsExpiringLease.ShouldBeTrue();
        typeof(ISupportLeaseRenewal).IsAssignableFrom(typeof(NatsListener)).ShouldBeTrue();
    }

    /// <summary>
    /// MaxAckPending is this transport's prefetch equivalent, and the value has to reach the SERVER -- asserting
    /// the endpoint property alone would pass even if the consumer config never carried it.
    /// </summary>
    [Fact]
    public async Task max_ack_pending_covers_every_lane_and_reaches_the_server()
    {
        var topology = topologyFor("pending");
        using var host = await startHostAsync(topology, "pending-host",
            configure: l => l.MaximumParallelMessages(6).PartitionProcessingByGroupId(PartitionSlots.Five));

        var config = await consumerConfigAsync(topology);

        // Six parallel lanes beats five partition slots, doubled so no lane waits on the next delivery
        config.MaxAckPending.ShouldBe(12);
    }

    [Fact]
    public async Task ack_wait_override_reaches_the_server()
    {
        var topology = topologyFor("ackwait");
        using var host = await startHostAsync(topology, "ackwait-host", 7.Seconds());

        var config = await consumerConfigAsync(topology);
        config.AckWait.ShouldBe(7.Seconds());
    }

    [Fact]
    public async Task messages_are_processed_end_to_end()
    {
        var topology = topologyFor("e2e");
        using var host = await startHostAsync(topology, "e2e-host");
        var bus = host.MessageBus();

        for (var i = 0; i < 10; i++)
        {
            await bus.SendAsync(new NativeAckWork(i));
        }

        await waitForAsync(() => NativeAckWorkTracking.DistinctNumbersFor("e2e-host", 0, 10).Count() >= 10,
            30.Seconds());

        NativeAckWorkTracking.DistinctNumbersFor("e2e-host", 0, 10).ShouldBe(Enumerable.Range(0, 10));
    }

    /// <summary>
    /// GH-4048 for this transport. A NativeAck delivery is held unsettled for lane queue time PLUS handler time,
    /// and JetStream redelivers anything still unacked after <c>AckWait</c>. With <c>AckProgress</c> renewal a
    /// handler that runs for several times <c>AckWait</c> is still exactly one execution; without it the server
    /// hands the same message out again while the first copy is still running.
    /// </summary>
    [Fact]
    public async Task a_delivery_held_far_past_ack_wait_is_not_redelivered()
    {
        var topology = topologyFor("lease");
        NativeAckWorkTracking.Block("lease-host");

        // AckWait of 3s means the renewal tick is 1.5s, and parking for 12s spans four AckWait windows
        using var host = await startHostAsync(topology, "lease-host", 3.Seconds());
        var bus = host.MessageBus();

        await bus.SendAsync(new NativeAckWork(42));

        // Let it reach the parked handler, then hold it there well past several AckWait windows
        await waitForAsync(() => queueDepth(host, topology) > 0 || NativeAckWorkTracking.CountOf(42) > 0,
            15.Seconds());
        await Task.Delay(12.Seconds(), TestContext.Current.CancellationToken);

        // Nothing has run yet -- the handler is still parked, and the lease has been renewed the whole time
        NativeAckWorkTracking.CountOf(42).ShouldBe(0);

        NativeAckWorkTracking.Release("lease-host");

        await waitForAsync(() => NativeAckWorkTracking.CountOf(42) >= 1, 15.Seconds());

        // Give any redelivery the server was going to make time to show up before asserting "exactly one"
        await Task.Delay(5.Seconds(), TestContext.Current.CancellationToken);

        NativeAckWorkTracking.CountOf(42).ShouldBe(1);
    }

    /// <summary>
    /// The whole point of the mode. Under BufferedInMemory these messages are acked the instant they arrive, so a
    /// node that dies before the handler finishes loses every one of them. Under NativeAck nothing is acked until
    /// the handler succeeds, so once the dead node stops renewing, JetStream redelivers all of them at AckWait.
    /// </summary>
    [Fact]
    public async Task nothing_is_acked_until_the_handler_succeeds_so_a_dead_node_loses_nothing()
    {
        var topology = topologyFor("redelivery");

        // The dying node's handlers park FOREVER and are never released. That is what makes the final assertion
        // unfakeable: IHost.Dispose() does not run StopAsync, so those handlers are still sitting in this
        // process, and a shared release gate would let them -- not the second host -- satisfy the assertion.
        NativeAckWorkTracking.Block("dying-host");

        var firstHost = await startHostAsync(topology, "dying-host", 3.Seconds());
        var bus = firstHost.MessageBus();

        for (var i = 0; i < 5; i++)
        {
            await bus.SendAsync(new NativeAckWork(100 + i));
        }

        // Let the deliveries actually reach the parked handlers before killing the node
        await waitForAsync(() => queueDepth(firstHost, topology) > 0, 20.Seconds());
        await Task.Delay(2.Seconds(), TestContext.Current.CancellationToken);

        // The node dies mid-flight, having acked nothing
        firstHost.Dispose();

        NativeAckWorkTracking.Handled.ShouldBeEmpty();

        // A fresh node -- which is NOT parked -- picks up every redelivered message
        using var secondHost = await startHostAsync(topology, "surviving-host", 3.Seconds());

        await waitForAsync(() => NativeAckWorkTracking.DistinctNumbersFor("surviving-host", 100, 5).Count() >= 5,
            60.Seconds());

        NativeAckWorkTracking.DistinctNumbersFor("surviving-host", 100, 5).ShouldBe(Enumerable.Range(100, 5));

        // ...and the dead node never handled any of them, so nothing above can be explained by the zombie
        NativeAckWorkTracking.DistinctNumbersFor("dying-host", 100, 5).ShouldBeEmpty();
    }

    private async Task<ConsumerConfig> consumerConfigAsync(Topology topology)
    {
        await using var connection = new NatsConnection(new NatsOpts { Url = _fixture.ConnectionString });
        await connection.ConnectAsync();

        var js = connection.CreateJetStreamContext();
        var consumer = await js.GetConsumerAsync(topology.Stream, topology.Consumer,
            TestContext.Current.CancellationToken);

        return consumer.Info.Config;
    }

    private static int queueDepth(IHost host, Topology topology)
    {
        var runtime = host.Services.GetRequiredService<IWolverineRuntime>();
        var circuit = runtime.Endpoints.FindListenerCircuit(new Uri($"nats://subject/{topology.Subject}"));
        return circuit?.QueueCount ?? 0;
    }

    private static async Task waitForAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (!condition() && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(100, TestContext.Current.CancellationToken);
        }
    }
}

/// <summary>
/// GH-4053. Core NATS is out of scope for this mode and has to say so. A core subscription delivers a message
/// once and forgets it -- there is no unacknowledged delivery to hold and nothing to hand back when a node dies
/// -- so a "native ack" core listener would be BufferedInMemory with a false no-loss promise.
/// </summary>
[Collection("NATS Integration")]
public class core_nats_rejects_native_ack
{
    private readonly NatsContainerFixture _fixture;

    public core_nats_rejects_native_ack(NatsContainerFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task a_core_nats_listener_refuses_the_mode_at_bootstrap()
    {
        var ex = await Should.ThrowAsync<InvalidListenerConfigurationException>(async () =>
        {
            using var host = await Host.CreateDefaultBuilder()
                .UseWolverine(opts =>
                {
                    opts.UseNats(_fixture.ConnectionString);
                    opts.Policies.DisableConventionalLocalRouting();

                    // No UseJetStream() -- this is a plain core NATS subject
                    opts.ListenToNatsSubject("core.native.ack").ProcessInParallelWithNativeAcks();
                }).StartAsync();
        });

        ex.Message.ShouldContain("JetStream");
        ex.Message.ShouldContain("Core NATS");
    }

    /// <summary>
    /// The refusal has to survive the fluent calls being written in either order. Both <c>UseJetStream()</c> and
    /// <c>ProcessInParallelWithNativeAcks()</c> are applied as delayed configuration, so answering the JetStream
    /// question inside the <c>Mode</c> setter's predicate would make it order-dependent -- accepting one spelling
    /// and refusing the identical other. GH-4047's validateModeConfiguration hook runs over the FINAL state,
    /// which is why this passes both ways round.
    /// </summary>
    [Fact]
    public async Task native_acks_are_accepted_whichever_order_the_fluent_calls_are_written_in()
    {
        foreach (var nativeAcksFirst in new[] { true, false })
        {
            var suffix = Guid.NewGuid().ToString("N")[..8];
            var subject = $"nack.order.{suffix}";
            var stream = $"NACK_ORDER_{suffix}".ToUpperInvariant();

            using var host = await Host.CreateDefaultBuilder()
                .UseWolverine(opts =>
                {
                    opts.UseNats(_fixture.ConnectionString)
                        .AutoProvision()
                        .DefineWorkQueueStream(stream, _ => { }, subject);

                    opts.Policies.DisableConventionalLocalRouting();

                    var listener = opts.ListenToNatsSubject(subject);
                    if (nativeAcksFirst)
                    {
                        listener.ProcessInParallelWithNativeAcks().UseJetStream(stream, $"nack-order-{suffix}");
                    }
                    else
                    {
                        listener.UseJetStream(stream, $"nack-order-{suffix}").ProcessInParallelWithNativeAcks();
                    }
                }).StartAsync(TestContext.Current.CancellationToken);

            var runtime = host.Services.GetRequiredService<IWolverineRuntime>();
            runtime.Endpoints.ActiveListeners()
                .Select(x => x.Endpoint)
                .OfType<NatsEndpoint>()
                .Single(x => x.Subject == subject)
                .Mode.ShouldBe(EndpointMode.NativeAck);
        }
    }

    [Fact]
    public void a_core_nats_endpoint_declares_no_expiring_lease()
    {
        using var host = Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseNats(_fixture.ConnectionString);
                opts.Policies.DisableConventionalLocalRouting();
                opts.ListenToNatsSubject("core.no.lease");
            }).Build();

        var options = host.Services.GetRequiredService<WolverineOptions>();
        var endpoint = options.Transports.AllEndpoints()
            .OfType<NatsEndpoint>()
            .Single(x => x.Subject == "core.no.lease");

        endpoint.UseJetStream.ShouldBeFalse();

        // There is no clock on a core NATS delivery, so there is nothing to renew and nothing to assert about
        endpoint.holdsExpiringLease.ShouldBeFalse();
    }
}

/// <summary>
/// GH-4053. Evidence for the DoubleAck decision in <c>NatsListener.RenewLeasesAsync</c>, taken straight against
/// the NATS client rather than through Wolverine.
///
/// <para>
/// <c>ISupportLeaseRenewal.RenewLeasesAsync</c> has to return the subset it could NOT renew. A bare
/// <c>AckProgressAsync</c> cannot ever produce that subset: it publishes to the message's reply subject and
/// returns, so it succeeds identically whether the consumer is alive or was deleted a minute ago. With
/// <c>DoubleAck</c> the same call becomes a request the server has to answer, and a lease that no longer exists
/// surfaces as an exception.
/// </para>
/// </summary>
[Collection("NATS Integration")]
public class jetstream_ack_progress_semantics
{
    private readonly NatsContainerFixture _fixture;

    public jetstream_ack_progress_semantics(NatsContainerFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task double_ack_is_what_makes_a_dead_lease_observable()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var stream = $"WPI_{suffix}".ToUpperInvariant();
        var subject = $"wpi.{suffix}";
        var consumerName = $"wpi-{suffix}";

        var token = TestContext.Current.CancellationToken;

        await using var connection = new NatsConnection(new NatsOpts { Url = _fixture.ConnectionString });
        await connection.ConnectAsync();
        var js = connection.CreateJetStreamContext();

        await js.CreateStreamAsync(new StreamConfig(stream, [subject]), token);
        await js.CreateOrUpdateConsumerAsync(stream, new ConsumerConfig
        {
            Name = consumerName,
            DurableName = consumerName,
            AckPolicy = ConsumerConfigAckPolicy.Explicit,
            AckWait = 5.Minutes()
        }, token);

        await js.PublishAsync(subject, new byte[] { 1, 2, 3 }, cancellationToken: token);

        var consumer = await js.GetConsumerAsync(stream, consumerName, token);
        var message = await consumer.NextAsync<byte[]>(cancellationToken: token);
        message.ShouldNotBeNull();

        // While the consumer is alive, a DoubleAck AckProgress round trips cleanly
        await Should.NotThrowAsync(async () =>
            await message!.AckProgressAsync(new AckOpts { DoubleAck = true }, token));

        // Now the lease is gone -- there is no consumer to extend AckWait on any more
        await js.DeleteConsumerAsync(stream, consumerName, token);

        // Fire-and-forget cannot tell. This is the assertion that rules the cheaper option out: it is not a
        // weaker signal, it is NO signal, and RenewLeasesAsync would report an empty refusal list forever.
        await Should.NotThrowAsync(async () => await message!.AckProgressAsync(cancellationToken: token));

        // DoubleAck can tell, which is the whole reason NatsListener pays for the round trip
        await Should.ThrowAsync<Exception>(async () =>
            await message!.AckProgressAsync(new AckOpts { DoubleAck = true }, token));

        await js.DeleteStreamAsync(stream, token);
    }
}
