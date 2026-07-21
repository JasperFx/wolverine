using System.Net.Sockets;

namespace Wolverine.ClaimCheck.GoogleCloudStorage.Tests;

/// <summary>
/// Probe the fake-gcs-server emulator once per process and cache the result. Any TCP connect
/// failure on <c>localhost:4443</c> is treated as "not running" so tests skip cleanly. Mirrors
/// the <c>LocalStackFact</c> pattern used by the Amazon S3 backend tests.
/// </summary>
internal static class FakeGcs
{
    public const string Host = "localhost";
    public const int Port = 4443;
    public const string EmulatorHost = "http://localhost:4443";

    private static readonly Lazy<bool> _isRunning = new(Probe);

    public static bool IsRunning => _isRunning.Value;

    public const string SkipReason =
        "The fake-gcs-server emulator is not running on localhost:4443. " +
        "Start it with `docker compose up -d fake-gcs-server` from the repo root to enable these tests.";

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
/// xUnit <see cref="FactAttribute"/> that skips when the fake-gcs-server emulator is not reachable.
/// </summary>
public sealed class FakeGcsFactAttribute : FactAttribute
{
    public FakeGcsFactAttribute()
    {
        if (!FakeGcs.IsRunning)
        {
            Skip = FakeGcs.SkipReason;
        }
    }
}
