using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using Amazon.S3;

namespace Wolverine.AmazonS3.Tests;

/// <summary>
/// Probe LocalStack once per process and cache the result. We treat any TCP
/// connect failure on <c>localhost:4566</c> as "not running" and let tests
/// skip cleanly. This mirrors the <c>AzuriteFact</c> pattern used by the
/// Azure Blob backend tests -- which still has the IPv6 bug described on
/// <see cref="Probe" />, and is dealt with separately.
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

    /// <summary>
    /// Try every address <see cref="Host" /> resolves to and take the first that connects.
    /// <para>
    /// GH-4160. This used to be a single <c>ConnectAsync(Host, Port)</c> with a two second budget for
    /// the whole thing, which reported "not running" against a LocalStack that was up: docker-compose
    /// publishes the gateway on <c>127.0.0.1:4566</c> only, <c>localhost</c> resolves to <c>::1</c>
    /// first on Windows, and the IPv6 attempt does not always fail inside the budget. The suite then
    /// skipped every test and reported green -- the same silent-skip failure mode as GH-4007.
    /// </para>
    /// </summary>
    private static bool Probe()
    {
        IPAddress[] addresses;
        try
        {
            addresses = Dns.GetHostAddresses(Host);
        }
        catch
        {
            return false;
        }

        foreach (var address in addresses)
        {
            try
            {
                using var client = new TcpClient(address.AddressFamily);
                var connect = client.ConnectAsync(address, Port);
#pragma warning disable VSTHRD002 // Avoid problematic synchronous waits
                if (connect.Wait(TimeSpan.FromSeconds(2)) && client.Connected)
#pragma warning restore VSTHRD002 // Avoid problematic synchronous waits
                {
                    return true;
                }
            }
            catch
            {
                // Try the next address rather than concluding anything from one family failing.
            }
        }

        return false;
    }
}

/// <summary>
/// xUnit <see cref="FactAttribute"/> that skips when LocalStack is not
/// reachable on its default port.
/// </summary>
public sealed class LocalStackFactAttribute : FactAttribute
{
    public LocalStackFactAttribute(
        [CallerFilePath] string? sourceFilePath = null,
        [CallerLineNumber] int sourceLineNumber = -1)
        : base(sourceFilePath, sourceLineNumber)
    {
        if (!LocalStack.IsRunning)
        {
            Skip = LocalStack.SkipReason;
        }
    }
}
