using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using NATS.Client.Core;

namespace Wolverine.Nats.Tests;

/// <summary>
/// Shared helpers for the NATS integration tests. The whole NATS suite resolves the broker from the
/// NATS_URL environment variable (set by <see cref="NatsContainerFixture"/> when Testcontainers is used),
/// falling back to the docker-compose broker on localhost:4222.
/// </summary>
internal static class NatsTestHelpers
{
    public static string ResolveUrl()
    {
        return Environment.GetEnvironmentVariable("NATS_URL") ?? "nats://localhost:4222";
    }

    /// <summary>
    /// Probe the broker by spinning up a throwaway Wolverine host. Returns false (so the caller can skip)
    /// when no NATS server is reachable, mirroring the guard used across the existing NATS integration tests.
    /// </summary>
    public static async Task<bool> IsNatsAvailable(string natsUrl)
    {
        try
        {
            using var testHost = await Host.CreateDefaultBuilder()
                .UseWolverine(opts => opts.UseNats(natsUrl))
                .StartAsync();

            await testHost.StopAsync();
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Subscribe a raw NATS client to an exact subject. <c>SubscribeCoreAsync</c> registers the subscription
    /// synchronously (the SUB is written before it returns) and the follow-up ping flushes it to the server,
    /// so there is no subscribe-before-publish race. Used to prove the exact concrete subject a message was
    /// published to — something a wildcard Wolverine listener can't (the receiving pipeline overwrites
    /// <c>Envelope.Destination</c> with the listener's own address).
    /// </summary>
    public static async Task<RawSubscription> SubscribeRawAsync(string url, string subject)
    {
        var connection = new NatsConnection(new NatsOpts { Url = url });
        await connection.ConnectAsync();
        var sub = await connection.SubscribeCoreAsync<byte[]>(subject);
        await connection.PingAsync();
        return new RawSubscription(connection, sub);
    }
}

/// <summary>
/// A raw NATS subscription plus its dedicated connection, disposed together.
/// </summary>
internal sealed class RawSubscription : IAsyncDisposable
{
    private readonly NatsConnection _connection;
    private readonly INatsSub<byte[]> _sub;

    public RawSubscription(NatsConnection connection, INatsSub<byte[]> sub)
    {
        _connection = connection;
        _sub = sub;
    }

    /// <summary>
    /// Read the next message, returning null if none arrives within <paramref name="timeout"/>.
    /// </summary>
    public async Task<NatsMsg<byte[]>?> ReadAsync(TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            return await _sub.Msgs.ReadAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _sub.DisposeAsync();
        await _connection.DisposeAsync();
    }
}

/// <summary>
/// Message used by the dynamic-subject and dedup tests. The <see cref="OrderId"/> doubles as a stable
/// domain identity for JetStream deduplication (see <c>DeduplicateUsing</c>).
/// </summary>
public record OrderPlaced(string OrderId);

public class OrderPlacedHandler
{
    // No-op: receivers only need this so Wolverine tracking counts the message as "received".
    public void Handle(OrderPlaced message)
    {
    }
}
