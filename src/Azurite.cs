using System.Runtime.CompilerServices;
using Azure.Storage.Blobs;

namespace IntegrationTests;

/// <summary>
/// Where Azurite lives, and whether it is up. Tests skip cleanly when it is not.
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

    /// <summary>A <see cref="BlobServiceClient"/> pinned to a service version Azurite accepts.</summary>
    public static BlobServiceClient CreateClient()
        => new(ConnectionString, new BlobClientOptions(MaxSupportedServiceVersion));

    public const string Host = "127.0.0.1";
    public const int Port = 10000;

    public static bool IsRunning => EmulatorProbe.IsListening(Host, Port);

    public const string SkipReason =
        "Azurite is not running on 127.0.0.1:10000. " +
        "Start it with `docker compose up -d azurite` from the repo root to enable these tests.";
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
