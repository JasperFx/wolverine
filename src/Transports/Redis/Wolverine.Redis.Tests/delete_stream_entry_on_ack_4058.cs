using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using StackExchange.Redis;
using Testcontainers.Redis;
using Wolverine.Redis.Internal;
using Wolverine.Runtime;
using Xunit;

namespace Wolverine.Redis.Tests;

#region message and handler

public record DeleteOnAckMessage(Guid SessionId, int Number);

public static class DeleteOnAckTracking
{
    public static ConcurrentDictionary<Guid, ConcurrentBag<int>> Handled { get; } = new();

    public static ConcurrentBag<int> For(Guid sessionId) => Handled.GetOrAdd(sessionId, _ => new ConcurrentBag<int>());
}

public class DeleteOnAckHandler
{
    public void Handle(DeleteOnAckMessage message) => DeleteOnAckTracking.For(message.SessionId).Add(message.Number);
}

#endregion

/// <summary>
///     GH-4058. <c>DeleteStreamEntryOnAck(true)</c> settles with <c>XACKDEL</c>, which requires Redis 8.2.
///     Against anything older every ack threw, the listener swallowed it as a warning, and nothing was ever
///     acknowledged -- entries piled up in the consumer group's pending list forever.
/// </summary>
public class delete_stream_entry_on_ack_4058
{
    /// <summary>
    ///     The regression test proper. Before the fix this passed the "everything was handled" check and then
    ///     failed here, with all five entries still pending, because every XACKDEL was rejected and swallowed.
    ///     It only goes green on a server that actually implements the command, which is what the image bump
    ///     in docker-compose.yml and RedisContainerFixture is for.
    /// </summary>
    [Fact]
    public async Task entries_are_acknowledged_and_deleted_when_delete_on_ack_is_on()
    {
        var sessionId = Guid.NewGuid();
        var streamKey = $"delete-on-ack-4058-{Guid.NewGuid():N}";
        const string group = "delete-on-ack-group";

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseRedisTransport(RedisContainerFixture.ConnectionString)
                    .AutoProvision()
                    .DeleteStreamEntryOnAck(true);

                opts.PublishMessage<DeleteOnAckMessage>().ToRedisStream(streamKey).SendInline();
                opts.ListenToRedisStream(streamKey, group).StartFromBeginning();

                opts.Discovery.IncludeType(typeof(DeleteOnAckHandler));
            }).StartAsync(TestContext.Current.CancellationToken);

        try
        {
            var bus = host.MessageBus();
            for (var i = 0; i < 5; i++)
            {
                await bus.SendAsync(new DeleteOnAckMessage(sessionId, i));
            }

            await waitUntilAsync(() => DeleteOnAckTracking.For(sessionId).Count == 5);

            DeleteOnAckTracking.For(sessionId).Count.ShouldBe(5);

            var database = databaseFor(host);

            // The actual assertion: the entries were settled for THIS consumer group and removed from the
            // stream. This is what silently never happened on a pre-8.2 server.
            await waitUntilAsync(async () => await pendingCountAsync(database, streamKey, group) == 0);

            (await pendingCountAsync(database, streamKey, group)).ShouldBe(0);
            (await database.StreamLengthAsync(streamKey)).ShouldBe(0);
        }
        finally
        {
            await host.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    /// <summary>
    ///     The XACK path was never broken -- pinning it here so the fix cannot regress the default.
    /// </summary>
    [Fact]
    public async Task entries_are_acknowledged_but_retained_when_delete_on_ack_is_off()
    {
        var sessionId = Guid.NewGuid();
        var streamKey = $"ack-only-4058-{Guid.NewGuid():N}";
        const string group = "ack-only-group";

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseRedisTransport(RedisContainerFixture.ConnectionString).AutoProvision();

                opts.PublishMessage<DeleteOnAckMessage>().ToRedisStream(streamKey).SendInline();
                opts.ListenToRedisStream(streamKey, group).StartFromBeginning();

                opts.Discovery.IncludeType(typeof(DeleteOnAckHandler));
            }).StartAsync(TestContext.Current.CancellationToken);

        try
        {
            var bus = host.MessageBus();
            for (var i = 0; i < 5; i++)
            {
                await bus.SendAsync(new DeleteOnAckMessage(sessionId, i));
            }

            await waitUntilAsync(() => DeleteOnAckTracking.For(sessionId).Count == 5);

            var database = databaseFor(host);

            await waitUntilAsync(async () => await pendingCountAsync(database, streamKey, group) == 0);

            (await pendingCountAsync(database, streamKey, group)).ShouldBe(0);

            // XACK settles the entry without removing it, so the stream itself is untouched
            (await database.StreamLengthAsync(streamKey)).ShouldBe(5);
        }
        finally
        {
            await host.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    private static IDatabase databaseFor(IHost host)
    {
        var transport = host.Services.GetRequiredService<IWolverineRuntime>()
            .Options.Transports.GetOrCreate<RedisTransport>();

        return transport.GetDatabase();
    }

    private static async Task<long> pendingCountAsync(IDatabase database, string streamKey, string group)
    {
        var groups = await database.StreamGroupInfoAsync(streamKey);
        return groups.FirstOrDefault(x => x.Name == group).PendingMessageCount;
    }

    /// <summary>
    ///     Deliberately returns rather than throwing on timeout, so the assertion that follows each call is the
    ///     thing that reports the failure -- "pending should be 0 but was 5" says far more about GH-4058 than a
    ///     bare timeout would.
    /// </summary>
    private static async Task waitUntilAsync(Func<Task<bool>> condition)
    {
        var expiry = DateTimeOffset.UtcNow.AddSeconds(15);
        while (DateTimeOffset.UtcNow < expiry)
        {
            if (await condition()) return;
            await Task.Delay(100);
        }
    }

    private static Task waitUntilAsync(Func<bool> condition)
    {
        return waitUntilAsync(() => Task.FromResult(condition()));
    }
}

/// <summary>
///     GH-4058. These deliberately run against a Redis 7 container of their own, so the guard against a server
///     that cannot honor <c>DeleteStreamEntryOnAck(true)</c> stays exercised even though the rest of the suite
///     has moved to an 8.2 image.
/// </summary>
public class delete_stream_entry_on_ack_on_an_old_server_4058 : IAsyncLifetime
{
    private RedisContainer _container = null!;

    public async ValueTask InitializeAsync()
    {
        _container = new RedisBuilder()
            .WithImage("redis:7-alpine")
            .WithLogger(NullLogger.Instance)
            .Build();

        await _container.StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _container.DisposeAsync();
    }

    /// <summary>
    ///     The capability probe itself: Redis 7 does not know XACKDEL, the fixture's server does.
    /// </summary>
    [Fact]
    public async Task the_capability_probe_tells_the_two_servers_apart()
    {
        await using var old = await ConnectionMultiplexer.ConnectAsync(_container.GetConnectionString());
        (await RedisStreamCapabilities.SupportsXackDelAsync(old.GetDatabase())).ShouldBe(false);

        await using var current =
            await ConnectionMultiplexer.ConnectAsync(RedisContainerFixture.ConnectionString);
        (await RedisStreamCapabilities.SupportsXackDelAsync(current.GetDatabase())).ShouldBe(true);
    }

    /// <summary>
    ///     Before the fix this host started perfectly happily and then silently acknowledged nothing.
    /// </summary>
    [Fact]
    public async Task refuses_to_start_a_listener_the_server_cannot_acknowledge()
    {
        var streamKey = $"old-server-4058-{Guid.NewGuid():N}";

        var ex = await Should.ThrowAsync<InvalidOperationException>(async () =>
        {
            using var host = await Host.CreateDefaultBuilder()
                .UseWolverine(opts =>
                {
                    opts.UseRedisTransport(_container.GetConnectionString())
                        .AutoProvision()
                        .DeleteStreamEntryOnAck(true);

                    opts.ListenToRedisStream(streamKey, "old-server-group").StartFromBeginning();
                    opts.Discovery.IncludeType(typeof(DeleteOnAckHandler));
                }).StartAsync(TestContext.Current.CancellationToken);
        });

        ex.Message.ShouldContain("XACKDEL");
        ex.Message.ShouldContain("DeleteStreamEntryOnAck");
        ex.Message.ShouldContain("8.2");
    }

    /// <summary>
    ///     The same old server is perfectly usable with the default XACK path -- the guard must not punish a
    ///     configuration that works.
    /// </summary>
    [Fact]
    public async Task starts_and_acknowledges_normally_on_the_same_server_without_delete_on_ack()
    {
        var sessionId = Guid.NewGuid();
        var streamKey = $"old-server-ok-4058-{Guid.NewGuid():N}";

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseRedisTransport(_container.GetConnectionString()).AutoProvision();

                opts.PublishMessage<DeleteOnAckMessage>().ToRedisStream(streamKey).SendInline();
                opts.ListenToRedisStream(streamKey, "old-server-ok-group").StartFromBeginning();
                opts.Discovery.IncludeType(typeof(DeleteOnAckHandler));
            }).StartAsync(TestContext.Current.CancellationToken);

        try
        {
            await host.MessageBus().SendAsync(new DeleteOnAckMessage(sessionId, 1));

            var expiry = DateTimeOffset.UtcNow.AddSeconds(15);
            while (DateTimeOffset.UtcNow < expiry && DeleteOnAckTracking.For(sessionId).Count < 1)
            {
                await Task.Delay(100, TestContext.Current.CancellationToken);
            }

            DeleteOnAckTracking.For(sessionId).Count.ShouldBe(1);
        }
        finally
        {
            await host.StopAsync(TestContext.Current.CancellationToken);
        }
    }
}
