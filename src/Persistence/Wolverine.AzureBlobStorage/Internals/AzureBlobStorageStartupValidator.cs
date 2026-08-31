using Azure.Storage.Blobs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Wolverine.AzureBlobStorage.Internals;

/// <summary>
/// Refuses to start an application whose blob document registration cannot work, where the client is
/// resolvable and the mappings are known, rather than letting a missing BlobServiceClient surface as a
/// container failure inside the first handler that happens to touch a document.
/// </summary>
internal class AzureBlobStorageStartupValidator : IHostedService
{
    private readonly AzureBlobStorageConfiguration _configuration;
    private readonly ILogger<AzureBlobStorageStartupValidator> _logger;
    private readonly IServiceProvider _services;

    public AzureBlobStorageStartupValidator(AzureBlobStorageConfiguration configuration, IServiceProvider services,
        ILogger<AzureBlobStorageStartupValidator> logger)
    {
        _configuration = configuration;
        _services = services;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (_configuration.Mappings.Count == 0)
        {
            _logger.LogWarning(
                "UseAzureBlobStoragePersistence() was called but no document or saga types were registered with Store<T>() or Saga<T>(), so nothing will resolve to Azure Blob Storage");

            return Task.CompletedTask;
        }

        if (_services.GetService<BlobServiceClient>() == null)
        {
            throw new InvalidOperationException(
                $"No BlobServiceClient is registered, but {_configuration.Mappings.Count} document or saga type(s) are registered to be stored in Azure Blob Storage. Register a client before UseAzureBlobStoragePersistence() -- services.AddSingleton(new BlobServiceClient(...)) or services.AddAzureClients(x => x.AddBlobServiceClient(...)).");
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}
