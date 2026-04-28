using System.Net.Sockets;

namespace Wolverine.ClaimCheck.AzureBlobStorage.Tests;

/// <summary>
/// Probe Azurite (the official Azure Storage emulator) once per process and
/// cache the result. We treat any TCP connect failure on
/// <c>127.0.0.1:10000</c> as "not running" and let tests skip cleanly.
/// </summary>
internal static class Azurite
{
    public const string ConnectionString =
        "DefaultEndpointsProtocol=http;" +
        "AccountName=devstoreaccount1;" +
        "AccountKey=Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==;" +
        "BlobEndpoint=http://127.0.0.1:10000/devstoreaccount1;";

    public const string Host = "127.0.0.1";
    public const int Port = 10000;

    private static readonly Lazy<bool> _isRunning = new(Probe);

    public static bool IsRunning => _isRunning.Value;

    public const string SkipReason =
        "Azurite is not running on 127.0.0.1:10000. " +
        "Start it with `azurite --silent --location ./.azurite --debug ./.azurite/debug.log` " +
        "or `docker run -p 10000:10000 mcr.microsoft.com/azure-storage/azurite azurite-blob --blobHost 0.0.0.0` " +
        "to enable these tests.";

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
/// xUnit <see cref="FactAttribute"/> that skips when the Azurite emulator is
/// not reachable on its default port.
/// </summary>
public sealed class AzuriteFactAttribute : FactAttribute
{
    public AzuriteFactAttribute()
    {
        if (!Azurite.IsRunning)
        {
            Skip = Azurite.SkipReason;
        }
    }
}
