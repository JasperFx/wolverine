using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolverine.AzureBlobStorage.Internals;
using Wolverine.Persistence.Sagas;

namespace Wolverine.AzureBlobStorage;

public static class WolverineAzureBlobStorageExtensions
{
    /// <summary>
    /// Store the named document and saga types as blobs in Azure Blob Storage, so a plain
    /// <c>[Entity]</c> parameter and the declarative <c>Storage.Store()</c> / <c>Storage.Delete()</c>
    /// return values resolve against a container.
    /// </summary>
    /// <remarks>
    /// Requires an <see cref="Azure.Storage.Blobs.BlobServiceClient" /> in the service container, left
    /// to the application so it keeps its own credential pipeline, endpoint and retry policy. This does
    /// not make Blob Storage the message store: the transactional inbox and outbox stay with whichever
    /// database the application uses.
    /// </remarks>
    /// <example>
    /// <code>
    /// opts.UseAzureBlobStoragePersistence(blobs =&gt;
    /// {
    ///     blobs.Store&lt;InvoiceContent&gt;(x =&gt;
    ///     {
    ///         x.ContainerName = "invoice-content";
    ///         x.BlobNameFor = ctx =&gt; $"invoices/v7/{ctx.TenantId}/{ctx.Id}.json";
    ///     });
    /// });
    /// </code>
    /// </example>
    public static WolverineOptions UseAzureBlobStoragePersistence(this WolverineOptions options,
        Action<AzureBlobStorageConfiguration> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        var configuration = new AzureBlobStorageConfiguration();
        configure(configuration);

        // Read back by BlobPersistenceFrameProvider when the frames are generated.
        options.Services.AddSingleton(configuration);

        options.Services.AddScoped<IBlobDocumentSession, BlobDocumentSession>();
        options.Services.AddSingleton<IHostedService, AzureBlobStorageStartupValidator>();

        options.CodeGeneration.InsertFirstPersistenceStrategy<BlobPersistenceFrameProvider>();

        // Without this the generated code does not compile: it references BlobStorageActionApplier.
        options.CodeGeneration.ReferenceAssembly(typeof(WolverineAzureBlobStorageExtensions).Assembly);

        return options;
    }
}
