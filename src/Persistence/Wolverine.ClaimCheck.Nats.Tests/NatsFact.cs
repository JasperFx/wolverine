using System.Net.Sockets;

namespace Wolverine.ClaimCheck.Nats.Tests;

/// <summary>
/// Probe NATS once per process and cache the result. Any TCP connect failure on
/// <c>localhost:4222</c> is treated as "not running" so tests skip cleanly. Mirrors
/// the <c>LocalStackFact</c> pattern used by the Amazon S3 backend tests.
/// </summary>
internal static class NatsServer
{
    public const string Host = "localhost";
    public const int Port = 4222;
    public const string Url = "nats://localhost:4222";

    private static readonly Lazy<bool> _isRunning = new(Probe);

    public static bool IsRunning => _isRunning.Value;

    public const string SkipReason =
        "NATS is not running on localhost:4222. " +
        "Start it with `docker compose up -d nats` from the repo root to enable these tests.";

    private static bool Probe()
    {
        try
        {
            using var client = new TcpClient();
            var connect = client.ConnectAsync(Host, Port);
#pragma warning disable VSTHRD002 // Avoid problematic synchronous waits
            return connect.Wait(TimeSpan.FromSeconds(2)) && client.Connected;
#pragma warning restore VSTHRD002 // Avoid problematic synchronous waits
        }
        catch
        {
            return false;
        }
    }
}

/// <summary>
/// xUnit <see cref="FactAttribute"/> that skips when NATS is not reachable on its default port.
/// </summary>
public sealed class NatsFactAttribute : FactAttribute
{
    public NatsFactAttribute()
    {
        if (!NatsServer.IsRunning)
        {
            Skip = NatsServer.SkipReason;
        }
    }
}
