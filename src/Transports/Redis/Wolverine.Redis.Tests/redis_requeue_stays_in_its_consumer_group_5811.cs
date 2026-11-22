using System.Text;
using JasperFx.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using StackExchange.Redis;
using Wolverine.ErrorHandling;
using Wolverine.Redis.Internal;
using Wolverine.Runtime;
using Wolverine.Runtime.Serialization;
using Wolverine.Transports;
using Xunit;

namespace Wolverine.Redis.Tests;

/// <summary>
///     GH-5811. A Redis Streams requeue <c>XADD</c>s a copy to the stream itself, and a native scheduled retry puts
///     its copy back on the stream when it is due. Every consumer group reading the stream used to see that copy
///     as a new entry, so a retry in one group delivered the message again to all the others.
///
///     <para>Now the copy is addressed to the group that requeued it, and a listener in any other group settles it
///     without handling it. An entry without the address is still for every group.</para>
/// </summary>
public class redis_requeue_stays_in_its_consumer_group_5811
{
    private const string StreamKey = "requeue-5811";
    private const string Group = "requeue-5811-group";
    private const string EntryId = "1700000000000-0";

    private readonly IDatabase _database = Substitute.For<IDatabase>();
    private readonly RedisStreamListener _listener;
    private readonly RedisStreamEndpoint _endpoint;

    public redis_requeue_stays_in_its_consumer_group_5811()
    {
        var connection = Substitute.For<IConnectionMultiplexer>();
        connection.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(_database);

        var transport = new RedisTransport(connection);
        _endpoint = transport.StreamEndpoint(StreamKey);
        _endpoint.ConsumerGroup = Group;
        _endpoint.EnvelopeMapper = new RedisEnvelopeMapper(_endpoint);

        var runtime = Substitute.For<IWolverineRuntime>();
        runtime.LoggerFactory.Returns(NullLoggerFactory.Instance);
        runtime.DurabilitySettings.Returns(new DurabilitySettings());
        runtime.Options.Returns(new WolverineOptions { ServiceName = "Requeue5811" });

        _listener = new RedisStreamListener(transport, _endpoint, runtime, Substitute.For<IReceiver>(), connection);
    }

    private static Envelope newEnvelope() => new()
    {
        Id = Guid.NewGuid(),
        Data = Encoding.UTF8.GetBytes("{}"),
        ContentType = "application/json",
        MessageType = "requeue-5811"
    };

    private Envelope receivedEnvelope()
    {
        var fields = new List<NameValueEntry>();
        _endpoint.EnvelopeMapper!.MapEnvelopeToOutgoing(newEnvelope(), fields);

        return _endpoint.EnvelopeMapper.CreateEnvelope(StreamKey, new StreamEntry(EntryId, fields.ToArray()), null);
    }

    private NameValueEntry[] addedFields() => (NameValueEntry[])_database.ReceivedCalls()
        .Single(x => x.GetMethodInfo().Name == "StreamAddAsync")
        .GetArguments()[1]!;

    [Fact]
    public async Task a_requeued_copy_is_addressed_to_the_group_that_requeued_it()
    {
        await _listener.DeferAsync(receivedEnvelope());

        var fields = addedFields();
        fields.Count(x => x.Name == RedisStreamListener.RequeueGroupField).ShouldBe(1);
        RedisStreamListener.RequeueGroupOf(new StreamEntry("1-0", fields)).ShouldBe(Group);
    }

    [Fact]
    public async Task a_native_scheduled_retry_is_addressed_to_the_group_that_scheduled_it()
    {
        var envelope = receivedEnvelope();

        await _listener.MoveToScheduledUntilAsync(envelope, DateTimeOffset.UtcNow.AddMinutes(1));

        var parked = (RedisValue)_database.ReceivedCalls()
            .Single(x => x.GetMethodInfo().Name == "SortedSetAddAsync")
            .GetArguments()[1]!;

        EnvelopeSerializer.Deserialize((byte[])parked!).Headers[RedisStreamListener.RequeueGroupHeader]
            .ShouldBe(Group);
    }

    [Fact]
    public async Task a_due_scheduled_retry_goes_back_on_the_stream_addressed_to_its_group()
    {
        var envelope = newEnvelope();
        envelope.Headers[RedisStreamListener.RequeueGroupHeader] = Group;
        givenScheduled(envelope);

        await _listener.MoveScheduledToReadyStreamAsync(CancellationToken.None);

        RedisStreamListener.RequeueGroupOf(new StreamEntry("1-0", addedFields())).ShouldBe(Group);
    }

    [Fact]
    public async Task a_message_scheduled_by_a_sender_still_goes_to_every_group()
    {
        givenScheduled(newEnvelope());

        await _listener.MoveScheduledToReadyStreamAsync(CancellationToken.None);

        RedisStreamListener.RequeueGroupOf(new StreamEntry("1-0", addedFields())).ShouldBeNull();
    }

    private void givenScheduled(Envelope envelope)
    {
        RedisValue serialized = EnvelopeSerializer.Serialize(envelope);
        _database.SortedSetRangeByScoreWithScoresAsync(default).ReturnsForAnyArgs([new SortedSetEntry(serialized, 1)]);
        _database.SortedSetRemoveAsync(default, default(RedisValue)).ReturnsForAnyArgs(true);
    }
}

/// <summary>
///     GH-5811 against a real Redis: two services, each with its own consumer group on one stream. A retry in
///     group A is handled again in A and never reaches B.
/// </summary>
[Collection("requeue_group_5811")]
public class redis_requeue_stays_in_its_consumer_group_5811_integration(ITestOutputHelper output) : IAsyncLifetime
{
    private readonly ConditionPoller _poller = new(output, maxRetries: 40, retryDelay: 250.Milliseconds());
    private readonly string _streamKey = $"requeue-5811-{Guid.NewGuid():N}";
    private readonly RequeueGroupTracker5811 _trackerA = new() { FailCount = 1 };
    private readonly RequeueGroupTracker5811 _trackerB = new();
    private IHost? _hostA;
    private IHost? _hostB;

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        foreach (var host in new[] { _hostA, _hostB })
        {
            if (host == null) continue;
            await host.StopAsync();
            host.Dispose();
        }

        using var connection = await ConnectionMultiplexer.ConnectAsync(RedisContainerFixture.ConnectionString);
        await connection.GetDatabase().KeyDeleteAsync([_streamKey, $"{_streamKey}:scheduled"]);
    }

    private Task<IHost> startAsync(string group, RequeueGroupTracker5811 tracker, Action<WolverineOptions> retry,
        bool publishes)
    {
        return Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.ServiceName = $"Requeue5811{group}";
                opts.Durability.ScheduledJobFirstExecution = 100.Milliseconds();
                opts.Durability.ScheduledJobPollingTime = 200.Milliseconds();

                opts.UseRedisTransport(RedisContainerFixture.ConnectionString).AutoProvision();

                if (publishes)
                {
                    opts.PublishMessage<RequeueGroup5811>().ToRedisStream(_streamKey);
                }

                opts.ListenToRedisStream(_streamKey, group).BufferedInMemory().StartFromBeginning();

                retry(opts);

                opts.Discovery.IncludeType<RequeueGroup5811Handler>();
                opts.Services.AddSingleton(tracker);
            }).StartAsync();
    }

    private async Task retryInGroupAIsNotDeliveredToGroupB(Action<WolverineOptions> retry)
    {
        _hostA = await startAsync("groupA", _trackerA, retry, publishes: true);
        _hostB = await startAsync("groupB", _trackerB, _ => { }, publishes: false);

        await _hostA.MessageBus().PublishAsync(new RequeueGroup5811(Guid.NewGuid()));

        await _poller.WaitForAsync("group A handled the retry", () => _trackerA.Attempts == 2);
        _trackerA.Attempts.ShouldBe(2, "group A fails once, then handles its retry");

        // Give group B every chance to see the copy, and to settle it
        using var connection = await ConnectionMultiplexer.ConnectAsync(RedisContainerFixture.ConnectionString);
        var database = connection.GetDatabase();
        await _poller.WaitForAsync("group B settled everything it read", async () =>
            (await database.StreamPendingAsync(_streamKey, "groupB")).PendingMessageCount == 0 &&
            (await database.StreamGroupInfoAsync(_streamKey)).Single(x => x.Name == "groupB").EntriesRead >= 2);
        await Task.Delay(1.Seconds());

        _trackerB.Attempts.ShouldBe(1, "group B handles the original once and never the retry copy");
        (await database.StreamPendingAsync(_streamKey, "groupB")).PendingMessageCount.ShouldBe(0);
    }

    [Fact]
    public Task a_requeue_in_one_group_is_not_delivered_to_another() =>
        retryInGroupAIsNotDeliveredToGroupB(opts =>
            opts.Policies.OnException<RequeueGroup5811Exception>().Requeue());

    [Fact]
    public Task a_native_scheduled_retry_in_one_group_is_not_delivered_to_another() =>
        retryInGroupAIsNotDeliveredToGroupB(opts =>
            opts.Policies.OnException<RequeueGroup5811Exception>().ScheduleRetry(500.Milliseconds()));
}

public record RequeueGroup5811(Guid Id);

public class RequeueGroup5811Exception : Exception;

public class RequeueGroup5811Handler(RequeueGroupTracker5811 tracker)
{
    public void Handle(RequeueGroup5811 message)
    {
        if (tracker.Record() <= tracker.FailCount)
        {
            throw new RequeueGroup5811Exception();
        }
    }
}

public class RequeueGroupTracker5811
{
    private int _attempts;

    public int FailCount { get; init; }

    public int Attempts => Volatile.Read(ref _attempts);

    public int Record() => Interlocked.Increment(ref _attempts);
}
