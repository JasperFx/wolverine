using System.Text.Json;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using IntegrationTests;
using Microsoft.Extensions.DependencyInjection;
using Wolverine.ComplianceTests;

namespace Wolverine.AzureBlobStorage.Tests;

/// <summary>
/// The shared <see cref="StorageActionCompliance" /> suite, run against a container. Marten, EF Core,
/// Polecat, RavenDb, S3 and the in-memory provider all answer this same suite, and answering it is
/// what makes "Azure Blob Storage supports the declarative storage return values" a claim with the
/// same meaning it has for every other store.
/// </summary>
/// <remarks>
/// This covers what a hand-written suite kept missing: <c>Storage.Insert()</c> and
/// <c>Storage.Update()</c> as return values (which reach <c>DetermineInsertFrame</c> and
/// <c>DetermineUpdateFrame</c> — untouched by any other test here), all four
/// <see cref="Wolverine.Persistence.StorageAction" /> arms through the generic path including
/// <c>Nothing</c>, null actions, and <c>[Entity]</c> on Before methods.
/// </remarks>
public class using_storage_return_types_and_entity_attributes : StorageActionCompliance
{
    public const string Container = "wolverine-blob-compliance";

    private BlobContainerClient _container = null!;

    protected override void configureWolverine(WolverineOptions opts)
    {
        opts.Durability.Mode = DurabilityMode.Solo;

        opts.Services.AddSingleton(Azurite.CreateClient());

        opts.UseAzureBlobStoragePersistence(blobs =>
        {
            blobs.Store<Todo>(x =>
            {
                x.ContainerName = Container;
                x.BlobNameFor = ctx => $"todos/{ctx.Id}.json";
            });
        });
    }

    // The base class builds the host first and calls this after, and building the host makes no blob
    // call -- so this is the first place that needs Azurite, and the place to skip from. The compliance
    // suite's tests are [Fact], so [AzuriteFact] is not available to them.
    protected override async Task initialize()
    {
        Assert.SkipUnless(Azurite.IsRunning, Azurite.SkipReason);

        _container = Azurite.ContainerClient(Container);
        await _container.CreateIfNotExistsAsync();
    }

    public override async Task<Todo?> Load(string id)
    {
        try
        {
            var response = await _container.GetBlobClient(nameFor(id)).DownloadContentAsync();

            return JsonSerializer.Deserialize<Todo>(response.Value.Content.ToMemory().Span, serialization);
        }
        catch (RequestFailedException e) when (e.Status == 404)
        {
            return null;
        }
    }

    public override Task Persist(Todo todo)
    {
        return _container.GetBlobClient(nameFor(todo.Id)).UploadAsync(
            BinaryData.FromString(JsonSerializer.Serialize(todo, serialization)),
            new BlobUploadOptions());
    }

    // Read and written straight through the Azure client rather than through Wolverine, so these have
    // to agree with the mapping's blob name function and with BlobDocumentSerializer.Default by hand.
    private static string nameFor(string id) => $"todos/{id}.json";

    private static readonly JsonSerializerOptions serialization = new(JsonSerializerDefaults.Web);
}
