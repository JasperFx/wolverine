using System.Runtime.CompilerServices;
using System.Net.Sockets;
using Azure.Storage.Blobs;

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

    /// <summary>
    /// The newest blob service version Azurite understands. The Azure SDK defaults to the newest version
    /// it knows about, which runs ahead of the emulator — sending it makes Azurite reject every request
    /// with <c>InvalidHeaderValue</c>, and because this suite skipped everywhere before GH-4007 nobody
    /// ever saw it. Pin the emulator client to a version Azurite supports; production clients keep the
    /// SDK default against real Azure Storage.
    /// </summary>
    public const BlobClientOptions.ServiceVersion MaxSupportedServiceVersion =
        BlobClientOptions.ServiceVersion.V2025_11_05;

    /// <summary>A <see cref="BlobContainerClient"/> pinned to a service version Azurite accepts.</summary>
    public static BlobContainerClient ContainerClient(string containerName)
        => new(ConnectionString, containerName, new BlobClientOptions(MaxSupportedServiceVersion));

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
/// xUnit <see cref="FactAttribute"/> that skips when the Azurite emulator is
/// not reachable on its default port.
/// </summary>
public sealed class AzuriteFactAttribute : FactAttribute
{
    public AzuriteFactAttribute(
        [CallerFilePath] string? sourceFilePath = null,
        [CallerLineNumber] int sourceLineNumber = -1)
        : base(sourceFilePath, sourceLineNumber)
    {
        if (!Azurite.IsRunning)
        {
            Skip = Azurite.SkipReason;
        }
    }
}
