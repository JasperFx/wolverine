using System.Text.Json;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using IntegrationTests;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Wolverine.AzureBlobStorage.Tests;

/// <summary>
/// One Azurite container and one Wolverine host with blob document persistence registered, shared by
/// the suites that need a real round trip.
/// </summary>
public class AzureBlobStorageFixture : IAsyncLifetime
{
    private static readonly JsonSerializerOptions _serialization = new(JsonSerializerDefaults.Web);

    public BlobServiceClient Client { get; private set; } = null!;
    public IHost Host { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        if (!Azurite.IsRunning)
        {
            return;
        }

        Client = Azurite.CreateClient();

        await EnsureContainerAsync();

        Host = await Microsoft.Extensions.Hosting.Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;

                opts.Discovery.DisableConventionalDiscovery();
                opts.Discovery.IncludeType(typeof(InvoiceHandler));

                opts.Services.AddSingleton(Azurite.CreateClient());

                opts.UseAzureBlobStoragePersistence(blobs =>
                {
                    blobs.Store<InvoiceContent>(x =>
                    {
                        x.ContainerName = InvoiceNames.Container;
                        x.BlobNameFor = InvoiceNames.For;
                    });
                });
            })
            .StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (Host != null)
        {
            await Host.StopAsync();
            Host.Dispose();
        }
    }

    public Task EnsureContainerAsync()
    {
        return Client.GetBlobContainerClient(InvoiceNames.Container).CreateIfNotExistsAsync();
    }

    /// <summary>
    /// Write an invoice straight through the Azure client, so a test can prove Wolverine <em>read</em>
    /// rather than proving Wolverine can read back its own writes.
    /// </summary>
    public Task PutAsync(InvoiceContent invoice, string? tenantId = null)
    {
        return blobFor(invoice.Id, tenantId).UploadAsync(
            BinaryData.FromString(JsonSerializer.Serialize(invoice, _serialization)),
            new BlobUploadOptions());
    }

    public async Task<InvoiceContent?> GetAsync(string id, string? tenantId = null)
    {
        try
        {
            var response = await blobFor(id, tenantId).DownloadContentAsync();

            return JsonSerializer.Deserialize<InvoiceContent>(response.Value.Content.ToMemory().Span, _serialization);
        }
        catch (RequestFailedException e) when (e.Status == 404)
        {
            return null;
        }
    }

    public Task DeleteAsync(string id, string? tenantId = null)
    {
        return blobFor(id, tenantId).DeleteIfExistsAsync();
    }

    private BlobClient blobFor(string id, string? tenantId)
    {
        return Client.GetBlobContainerClient(InvoiceNames.Container)
            .GetBlobClient(InvoiceNames.For(new BlobNameContext(typeof(InvoiceContent), id, tenantId)));
    }
}
