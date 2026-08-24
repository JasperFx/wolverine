using System.Collections.Concurrent;
using JasperFx.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using StackExchange.Redis;
using Wolverine.Configuration;
using Wolverine.Redis.Internal;
using Wolverine.Runtime;
using Xunit;

namespace Wolverine.Redis.Tests;

#region messages and handler

public record RedisNativeAckWork(Guid SessionId, int Number, string Group);

/// <summary>
/// Tracking state is keyed by a session id carried on the message itself rather than living in one static
/// bag. Every test here gets its own stream, its own consumer group AND its own session, because the
/// RabbitMQ suite showed what happens otherwise: a redelivery test's messages arriving late land in the
/// next test's assertions.
/// </summary>
public class RedisNativeAckSession
{
    public ConcurrentBag<int> Handled { get; } = new();

    /// <summary>When set, handlers park here -- standing in for a node that dies mid-flight.</summary>
    public TaskCompletionSource? Block { get; set; }

    /// <summary>Per-message gates, so one message can be held while the ones behind it finish.</summary>
    public ConcurrentDictionary<int, TaskCompletionSource> Gates { get; } = new();

    public ConcurrentDictionary<string, int> ActiveByGroup { get; } = new();
    public int MaxConcurrencyWithinAGroup;
    public int MaxConcurrencyOverall;
    private int _active;

    public TaskCompletionSource GateFor(int number)
    {
        return Gates.GetOrAdd(number, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
    }

    public async Task HandleAsync(RedisNativeAckWork message)
    {
        if (Gates.TryGetValue(message.Number, out var gate))
        {
            await gate.Task;
        }

        var block = Block;
        if (block != null)
        {
            await block.Task;
        }

        var overall = Interlocked.Increment(ref _active);
        var withinGroup = ActiveByGroup.AddOrUpdate(message.Group, 1, (_, current) => current + 1);

        trackMaximum(ref MaxConcurrencyOverall, overall);
        trackMaximum(ref MaxConcurrencyWithinAGroup, withinGroup);

        try
        {
            // Long enough that two messages of the same group WOULD overlap if nothing prevented it
            await Task.Delay(75);
            Handled.Add(message.Number);
        }
        finally
        {
            ActiveByGroup.AddOrUpdate(message.Group, 0, (_, current) => current - 1);
            Interlocked.Decrement(ref _active);
        }
    }

    private static void trackMaximum(ref int target, int candidate)
    {
        var current = Volatile.Read(ref target);
        while (candidate > current)
        {
            var previous = Interlocked.CompareExchange(ref target, candidate, current);
            if (previous == current) return;
            current = previous;
        }
    }

    public void ReleaseEverything()
    {
        Block?.TrySetResult();
        foreach (var gate in Gates.Values)
        {
            gate.TrySetResult();
        }
    }
}

public static class RedisNativeAckTracking
{
    private static readonly ConcurrentDictionary<Guid, RedisNativeAckSession> _sessions = new();

    public static RedisNativeAckSession Start(Guid id)
    {
        var session = new RedisNativeAckSession();
        _sessions[id] = session;
        return session;
    }

    public static RedisNativeAckSession For(Guid id)
    {
        return _sessions.GetOrAdd(id, _ => new RedisNativeAckSession());
    }
}

public class RedisNativeAckWorkHandler
{
    public Task Handle(RedisNativeAckWork message)
    {
        return RedisNativeAckTracking.For(message.SessionId).HandleAsync(message);
    }
}

#endregion

/// <summary>
/// GH-4046. Redis Streams opting into <see cref="EndpointMode.NativeAck"/>. What is under test is the
/// guarantee the mode exists for -- Buffered's throughput and partitioning with Inline's no-loss behaviour --
/// expressed in Redis's own terms: the pending entries list. Nothing is XACKed until a handler succeeds, so
/// "unacked" here is directly observable as an entry still sitting in the group's PEL.
/// </summary>
public class native_ack_mode : IAsyncLifetime
{
    private readonly List<RedisNativeAckSession> _sessions = new();
    private IConnectionMultiplexer _connection = null!;

    public async ValueTask InitializeAsync()
    {
        _connection = await ConnectionMultiplexer.ConnectAsync(RedisContainerFixture.ConnectionString);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var session in _sessions)
        {
            session.ReleaseEverything();
        }

        await _connection.DisposeAsync();
    }

    private RedisNativeAckSession startSession(Guid id)
    {
        var session = RedisNativeAckTracking.Start(id);
        _sessions.Add(session);
        return session;
    }

    private Task<IHost> startHostAsync(string streamKey, string consumerGroup,
        Action<RedisListenerConfiguration>? configure = null, string? consumerName = null)
    {
        return Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseRedisTransport(RedisContainerFixture.ConnectionString).AutoProvision();

                opts.Discovery.DisableConventionalDiscovery().IncludeType<RedisNativeAckWorkHandler>();

                // Group id for the partitioned lanes comes off the message body
                opts.MessagePartitioning.ByMessage<RedisNativeAckWork>(x => x.Group);

                var listener = opts.ListenToRedisStream(streamKey, consumerGroup)
                    .Named(streamKey)
                    .ProcessInParallelWithNativeAcks()
                    .BlockTimeout(100.Milliseconds())
                    .StartFromBeginning();

                if (consumerName != null)
                {
                    listener.ConsumerName(consumerName);
                }

                configure?.Invoke(listener);

                // NOTE: deliberately NOT SendInline(). Listener and subscriber share one Endpoint object here,
                // so SendInline() would set Mode = Inline on the very endpoint this suite is testing.
                opts.PublishMessage<RedisNativeAckWork>().ToRedisStream(streamKey);
            }).StartAsync();
    }

    [Fact]
    public async Task the_endpoint_really_is_in_native_ack_mode_with_a_native_ack_receiver()
    {
        var streamKey = $"native-ack-4046-mode-{Guid.NewGuid():N}";
        using var host = await startHostAsync(streamKey, "mode-group");
        var runtime = host.Services.GetRequiredService<IWolverineRuntime>();

        var endpoint = runtime.Endpoints.EndpointByName(streamKey).ShouldNotBeNull().ShouldBeOfType<RedisStreamEndpoint>();
        endpoint.Mode.ShouldBe(EndpointMode.NativeAck);

        // Back pressure is the execution block plus the read batch, not a BackPressureAgent
        endpoint.ShouldEnforceBackPressure().ShouldBeFalse();

        // ...and the read batch, this transport's prefetch equivalent, has to cover every lane
        endpoint.BatchSize.ShouldBe(endpoint.MaxDegreeOfParallelism * 2);
    }

    [Fact]
    public void batch_size_default_covers_every_lane_that_can_be_busy()
    {
        var transport = new RedisTransport(RedisContainerFixture.ConnectionString);

        var buffered = transport.StreamEndpoint("batch-size-buffered");
        buffered.Mode.ShouldBe(EndpointMode.BufferedInMemory);
        buffered.BatchSize.ShouldBe(10);

        // Not partitioned: the lanes are MaxDegreeOfParallelism
        var parallel = transport.StreamEndpoint("batch-size-parallel");
        parallel.MaxDegreeOfParallelism = 6;
        parallel.Mode = EndpointMode.NativeAck;
        parallel.BatchSize.ShouldBe(12);

        // Partitioned: the lanes are the slots, and GH-3899's exempt types still run at
        // MaxDegreeOfParallelism, so the batch covers whichever is larger
        var partitioned = transport.StreamEndpoint("batch-size-partitioned");
        partitioned.MaxDegreeOfParallelism = 3;
        partitioned.GroupShardingSlotNumber = PartitionSlots.Nine;
        partitioned.Mode = EndpointMode.NativeAck;
        partitioned.BatchSize.ShouldBe(18);

        partitioned.MaxDegreeOfParallelism = 20;
        partitioned.BatchSize.ShouldBe(40);

        // An explicit setting always wins over the mode default
        var explicitly = transport.StreamEndpoint("batch-size-explicit");
        explicitly.Mode = EndpointMode.NativeAck;
        explicitly.BatchSize = 3;
        explicitly.BatchSize.ShouldBe(3);
    }

    [Fact]
    public async Task messages_are_processed_end_to_end()
    {
        var sessionId = Guid.NewGuid();
        var session = startSession(sessionId);
        var streamKey = $"native-ack-4046-e2e-{Guid.NewGuid():N}";

        using var host = await startHostAsync(streamKey, "e2e-group");
        var bus = host.MessageBus();

        for (var i = 0; i < 10; i++)
        {
            await bus.SendAsync(new RedisNativeAckWork(sessionId, i, "e2e"));
        }

        await waitFor(() => session.Handled.Distinct().Count() >= 10, 30.Seconds());

        session.Handled.Distinct().OrderBy(x => x).ShouldBe(Enumerable.Range(0, 10));

        // Every delivery settled: the pending entries list is what "acked" means on this transport
        await waitFor(async () => await pendingCountAsync(streamKey, "e2e-group") == 0, 15.Seconds());
    }

    /// <summary>
    /// The per-entry settlement property that lets this transport qualify at all. XACK names one entry id, so
    /// a message whose handler finishes early settles ITSELF and nothing else -- the entry ahead of it in the
    /// stream stays pending. A cumulative settle model (a Kafka offset commit, a RabbitMQ ack with
    /// multiple:true) would have silently acked the still-running message here, which is exactly the loss the
    /// mode is supposed to prevent.
    /// </summary>
    [Fact]
    public async Task only_the_completed_entry_is_settled_when_completion_is_out_of_order()
    {
        var sessionId = Guid.NewGuid();
        var session = startSession(sessionId);
        var streamKey = $"native-ack-4046-ooo-{Guid.NewGuid():N}";
        const string group = "ooo-group";

        // Message 0 -- the FIRST entry in the stream -- is held while 1 and 2 sail past it
        var gate = session.GateFor(0);

        using var host = await startHostAsync(streamKey, group);
        var bus = host.MessageBus();

        for (var i = 0; i < 3; i++)
        {
            await bus.SendAsync(new RedisNativeAckWork(sessionId, i, "ooo"));
        }

        await waitFor(() => session.Handled.Contains(1) && session.Handled.Contains(2), 30.Seconds());

        // The two later entries are settled; the earlier one is still held, and still pending
        await waitFor(async () => await pendingCountAsync(streamKey, group) == 1, 15.Seconds());
        session.Handled.ShouldNotContain(0);

        gate.TrySetResult();

        await waitFor(() => session.Handled.Contains(0), 30.Seconds());
        await waitFor(async () => await pendingCountAsync(streamKey, group) == 0, 15.Seconds());
    }

    /// <summary>
    /// The whole point of the mode. Under BufferedInMemory these entries are XACKed the instant they are read,
    /// so a node that dies before the handler finishes loses every one of them. Under NativeAck nothing is
    /// acked until the handler succeeds, so they are all still in the consumer group's pending entries list
    /// when the node dies, and XAUTOCLAIM hands them to the node that replaces it.
    /// </summary>
    [Fact]
    public async Task nothing_is_acked_until_the_handler_succeeds_so_a_dead_node_loses_nothing()
    {
        var sessionId = Guid.NewGuid();
        var session = startSession(sessionId);
        var streamKey = $"native-ack-4046-redelivery-{Guid.NewGuid():N}";
        const string group = "redelivery-group";

        // Every handler on the first node parks here and never comes back -- a node that dies mid-flight
        session.Block = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var firstHost = await startHostAsync(streamKey, group, consumerName: "node-1");
        var bus = firstHost.MessageBus();

        for (var i = 0; i < 5; i++)
        {
            await bus.SendAsync(new RedisNativeAckWork(sessionId, 100 + i, $"g{i}"));
        }

        // Let the entries actually reach the parked handlers before killing the node
        await waitFor(async () => await pendingCountAsync(streamKey, group) == 5, 30.Seconds());

        // The node dies mid-flight, having acked nothing
        firstHost.Dispose();

        session.Handled.ShouldBeEmpty();

        // Still unacked with the node gone -- nothing was settled on receipt, and node 1's handlers stay
        // parked forever so they can never settle anything either
        (await pendingCountAsync(streamKey, group)).ShouldBe(5);

        // New handler invocations on the replacement node do not park
        session.Block = null;

        // A fresh node claims every pending entry out of the dead consumer's PEL. XAUTOCLAIM is the
        // redelivery mechanism here -- XREADGROUP ">" only ever returns never-delivered entries.
        using var secondHost = await startHostAsync(streamKey, group,
            listener => listener.EnableAutoClaim(250.Milliseconds(), 250.Milliseconds()),
            consumerName: "node-2");

        await waitFor(() => session.Handled.Distinct().Count() >= 5, 60.Seconds());

        session.Handled.Distinct().OrderBy(x => x).ShouldBe(Enumerable.Range(100, 5));
        await waitFor(async () => await pendingCountAsync(streamKey, group) == 0, 30.Seconds());
    }

    /// <summary>
    /// The hard guarantee of the mode: messages sharing a group id never execute concurrently, while the
    /// endpoint as a whole still runs several at once. Original delivery order is explicitly NOT promised.
    /// </summary>
    [Fact]
    public async Task messages_sharing_a_group_id_never_execute_concurrently()
    {
        var sessionId = Guid.NewGuid();
        var session = startSession(sessionId);
        var streamKey = $"native-ack-4046-partitioned-{Guid.NewGuid():N}";
        const string group = "partitioned-group";

        using var host = await startHostAsync(streamKey, group,
            listener => listener.PartitionProcessingByGroupId(PartitionSlots.Nine));

        var bus = host.MessageBus();

        var number = 0;
        for (var g = 0; g < 6; g++)
        {
            for (var i = 0; i < 4; i++)
            {
                await bus.SendAsync(new RedisNativeAckWork(sessionId, number++, $"group-{g}"));
            }
        }

        await waitFor(() => session.Handled.Distinct().Count() >= 24, 60.Seconds());

        session.MaxConcurrencyWithinAGroup.ShouldBe(1);
        session.MaxConcurrencyOverall.ShouldBeGreaterThan(1);

        await waitFor(async () => await pendingCountAsync(streamKey, group) == 0, 30.Seconds());
    }

    private async Task<long> pendingCountAsync(string streamKey, string consumerGroup)
    {
        var pending = await _connection.GetDatabase().StreamPendingAsync(streamKey, consumerGroup);
        return pending.PendingMessageCount;
    }

    private static Task waitFor(Func<bool> condition, TimeSpan timeout)
    {
        return waitFor(() => Task.FromResult(condition()), timeout);
    }

    private static async Task waitFor(Func<Task<bool>> condition, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow.Add(timeout);
        while (!await condition())
        {
            if (DateTimeOffset.UtcNow > deadline)
            {
                throw new TimeoutException("Condition was not met in time");
            }

            await Task.Delay(100, TestContext.Current.CancellationToken);
        }
    }
}
