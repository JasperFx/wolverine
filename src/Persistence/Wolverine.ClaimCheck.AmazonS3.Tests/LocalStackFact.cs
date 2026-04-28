using System.Net.Sockets;
using Amazon.S3;

namespace Wolverine.ClaimCheck.AmazonS3.Tests;

/// <summary>
/// Probe LocalStack once per process and cache the result. We treat any TCP
/// connect failure on <c>localhost:4566</c> as "not running" and let tests
/// skip cleanly. This mirrors the <c>AzuriteFact</c> pattern used by the
/// Azure Blob backend tests.
/// </summary>
internal static class LocalStack
{
    public const string Host = "localhost";
    public const int Port = 4566;
    public const string ServiceUrl = "http://localhost:4566";

    private static readonly Lazy<bool> _isRunning = new(Probe);

    public static bool IsRunning => _isRunning.Value;

    public const string SkipReason =
        "LocalStack is not running on localhost:4566. " +
        "Start it with `docker compose up -d localstack` from the repo root to enable these tests.";

    public static AmazonS3Client CreateClient()
    {
        var config = new AmazonS3Config
        {
            ServiceURL = ServiceUrl,
            ForcePathStyle = true,
            UseHttp = true
        };

        return new AmazonS3Client("xxx", "xxx", config);
    }

    private static bool Probe()
    {
        try
        {
            using var client = new TcpClient();
            var connect = client.ConnectAsync(Host, Port);
            return connect.Wait(TimeSpan.FromSeconds(2)) && client.Connected;
        }
        catch
        {
            return false;
        }
    }
}

/// <summary>
/// xUnit <see cref="FactAttribute"/> that skips when LocalStack is not
/// reachable on its default port.
/// </summary>
public sealed class LocalStackFactAttribute : FactAttribute
{
    public LocalStackFactAttribute()
    {
        if (!LocalStack.IsRunning)
        {
            Skip = LocalStack.SkipReason;
        }
    }
}
