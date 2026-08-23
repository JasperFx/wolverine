using IntegrationTests;
using JasperFx;
using JasperFx.Core;
using JasperFx.Resources;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using StackExchange.Redis;
using Wolverine.Configuration;
using Wolverine.ErrorHandling;
using Wolverine.Persistence.Durability;
using Wolverine.Postgresql;
using Wolverine.Redis.Internal;
using Wolverine.Runtime;
using Wolverine.Transports;
using Xunit;

namespace Wolverine.Redis.Tests;

/// <summary>
/// GH-4028. <c>UseDurableInbox()</c> on a Redis stream used to mean "skip the inbox and XACK on receipt"
/// because <see cref="RedisStreamEndpoint"/> was <c>IDatabaseBackedEndpoint</c>. These pin the real
/// durable-inbox contract: the message is written to the inbox before the stream entry is acknowledged,
/// the row is <c>Incoming</c> while the handler runs, and a scheduled retry is parked in the inbox rather
/// than in the Redis scheduled sorted set.
/// </summary>
[Collection("durable_inbox_is_real_4028")]
public class durable_inbox_is_real_4028 : IAsyncLifetime
{
    private string _streamKey = null!;
    private IHost _host = null!;
    private IMessageStore _store = null!;
    private RedisStreamEndpoint _endpoint = null!;
    private IDatabase _database = null!;

    public async ValueTask InitializeAsync()
    {
        _streamKey = $"durable-real-{Guid.NewGuid():N}";
        BlockingRedisHandler.Reset();

        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.Durability.ScheduledJobFirstExecution = 100.Milliseconds();
                opts.Durability.ScheduledJobPollingTime = 200.Milliseconds();

                opts.UseRedisTransport(RedisContainerFixture.ConnectionString).AutoProvision();
                opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, "redis_durable_4028");

                opts.PublishMessage<BlockingRedisMessage>().ToRedisStream(_streamKey).SendInline();
                opts.PublishMessage<RetryOnceRedisMessage>().ToRedisStream(_streamKey).SendInline();

                opts.ListenToRedisStream(_streamKey, "durable-real-group")
                    .UseDurableInbox()
                    .StartFromBeginning();

                opts.Policies.OnException<InvalidOperationException>().ScheduleRetry(1.Seconds());

                opts.Discovery.IncludeType(typeof(BlockingRedisHandler));
                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync();

        _store = _host.Services.GetRequiredService<IMessageStore>();
        await _store.Admin.ClearAllAsync();

        var transport = _host.Services.GetRequiredService<IWolverineRuntime>().Options.Transports.GetOrCreate<RedisTransport>();
        _endpoint = transport.StreamEndpoint(_streamKey);
        _database = transport.GetDatabase(database: _endpoint.DatabaseId);
    }

    public async ValueTask DisposeAsync()
    {
        BlockingRedisHandler.Release();
        await _host.StopAsync();
        _host.Dispose();
    }

    private static async Task waitUntil(Func<Task<bool>> condition, TimeSpan timeout)
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

    [Fact]
    public void the_endpoint_is_not_database_backed_and_durable_mode_is_really_durable()
    {
        _endpoint.ShouldNotBeAssignableTo<IDatabaseBackedEndpoint>();
        _endpoint.Mode.ShouldBe(EndpointMode.Durable);
    }

    [Fact]
    public async Task a_message_is_in_the_inbox_and_acked_on_the_stream_before_its_handler_finishes()
    {
        var id = Guid.NewGuid();
        await _host.MessageBus().PublishAsync(new BlockingRedisMessage(id));

        // The handler is parked; the message must already be durable
        await waitUntil(() => Task.FromResult(BlockingRedisHandler.Started.Task.IsCompleted), 30.Seconds());

        var counts = await _store.Admin.FetchCountsAsync();
        counts.Incoming.ShouldBe(1, "the envelope must be in the inbox as Incoming while the handler runs");

        // ...and because it is durable, the stream entry was acknowledged right after the insert, so
        // nothing is left pending on the consumer group
        var pending = await _database.StreamPendingAsync(_streamKey, _endpoint.ConsumerGroup!);
        pending.PendingMessageCount.ShouldBe(0);

        BlockingRedisHandler.Release();

        await waitUntil(async () => (await _store.Admin.FetchCountsAsync()).Incoming == 0, 30.Seconds());
        BlockingRedisHandler.Executions(id).ShouldBe(1);
    }

    [Fact]
    public async Task a_scheduled_retry_is_parked_in_the_inbox_not_in_the_redis_scheduled_set()
    {
        var id = Guid.NewGuid();
        await _host.MessageBus().PublishAsync(new RetryOnceRedisMessage(id));

        // First attempt fails and schedules a retry 1s out: the inbox holds it as Scheduled
        await waitUntil(async () => (await _store.Admin.FetchCountsAsync()).Scheduled >= 1, 30.Seconds());

        // The Redis-native scheduled sorted set is the NON-durable mechanism and must be untouched here
        (await _database.SortedSetLengthAsync(_endpoint.ScheduledMessagesKey)).ShouldBe(0);

        // ...and the retry still happens, from the inbox
        await waitUntil(() => Task.FromResult(BlockingRedisHandler.Executions(id) >= 2), 30.Seconds());
        await waitUntil(async () => (await _store.Admin.FetchCountsAsync()).Scheduled == 0, 30.Seconds());
    }
}

public record BlockingRedisMessage(Guid Id);

public record RetryOnceRedisMessage(Guid Id);

public static class BlockingRedisHandler
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, int> _executions = new();
    private static TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public static TaskCompletionSource Started { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public static int Executions(Guid id)
    {
        return _executions.GetValueOrDefault(id);
    }

    public static void Reset()
    {
        _executions.Clear();
        _gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public static void Release()
    {
        _gate.TrySetResult();
    }

    public static async Task Handle(BlockingRedisMessage message)
    {
        _executions.AddOrUpdate(message.Id, 1, (_, n) => n + 1);
        Started.TrySetResult();
        await _gate.Task;
    }

    public static void Handle(RetryOnceRedisMessage message)
    {
        var attempt = _executions.AddOrUpdate(message.Id, 1, (_, n) => n + 1);
        if (attempt == 1)
        {
            throw new InvalidOperationException("first attempt fails on purpose");
        }
    }
}
