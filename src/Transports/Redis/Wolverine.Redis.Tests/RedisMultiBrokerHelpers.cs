using StackExchange.Redis;
using Testcontainers.Redis;
using Xunit;

namespace Wolverine.Redis.Tests;

/// <summary>
/// Shared helpers for the multi-broker (named broker + broker-per-tenant) Redis integration tests. These
/// tests need a second, genuinely distinct Redis server so "the message landed on the tenant's / named
/// broker's own server and not the shared one" is a provable assertion, so they spin up a second Redis
/// container (server B) alongside the shared container fixture (server A).
/// </summary>
internal static class RedisMultiBrokerHelpers
{
    /// <summary>
    /// Read every entry currently in a stream on the target server via a raw XREAD (no consumer group), so a
    /// test can prove the exact server a message was published to. Returns an empty array when the stream key
    /// does not exist on that server.
    /// </summary>
    public static async Task<StreamEntry[]> ReadStreamAsync(string connectionString, string streamKey)
    {
        var mux = await ConnectionMultiplexer.ConnectAsync(connectionString);
        await using (mux.ConfigureAwait(false))
        {
            var db = mux.GetDatabase();
            return await db.StreamReadAsync(streamKey, "0-0");
        }
    }

    /// <summary>
    /// Poll a stream on the target server until at least one entry is present or the timeout elapses.
    /// </summary>
    public static async Task<StreamEntry[]> WaitForStreamAsync(string connectionString, string streamKey, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var entries = await ReadStreamAsync(connectionString, streamKey);
            if (entries.Length > 0) return entries;
            await Task.Delay(100);
        }

        return await ReadStreamAsync(connectionString, streamKey);
    }
}

/// <summary>
/// An xUnit class fixture that starts a single second Redis server (server B) shared across all the test
/// methods of a class. Sharing one container per class (rather than one per test method) keeps the whole
/// suite's Docker footprint small and avoids broker-init timeouts under load. When Docker is unavailable
/// <see cref="ConnectionString"/> is null so the tests skip. GH-3309.
/// </summary>
public sealed class SecondRedisServerFixture : IAsyncLifetime
{
    private RedisContainer? _container;

    public string? ConnectionString { get; private set; }

    public async Task InitializeAsync()
    {
        try
        {
            _container = new RedisBuilder().WithImage("redis:7-alpine").Build();
            await _container.StartAsync();
            ConnectionString = _container.GetConnectionString();
        }
        catch
        {
            ConnectionString = null;
        }
    }

    public async Task DisposeAsync()
    {
        if (_container != null)
        {
            await _container.DisposeAsync();
        }
    }
}

/// <summary>
/// Message used by the Redis multi-broker integration tests.
/// </summary>
public record RedisBrokerMessage(string Id);

public class RedisBrokerMessageHandler
{
    // No-op: receivers only need this so Wolverine tracking counts the message as "received".
    public void Handle(RedisBrokerMessage message)
    {
    }
}
