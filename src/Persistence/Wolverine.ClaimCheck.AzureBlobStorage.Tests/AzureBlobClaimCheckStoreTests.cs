using System.Text;
using Azure.Storage.Blobs;
using Shouldly;
using Wolverine.Persistence;

namespace Wolverine.ClaimCheck.AzureBlobStorage.Tests;

public class AzureBlobClaimCheckStoreTests : IAsyncLifetime
{
    // Each test class gets its own container so parallel test classes / test
    // re-runs cannot collide on blob ids.
    private readonly string _containerName = "claim-check-tests-" + Guid.NewGuid().ToString("N");
    private BlobContainerClient _container = null!;
    private AzureBlobClaimCheckStore _store = null!;

    public async Task InitializeAsync()
    {
        if (!Azurite.IsRunning)
        {
            return;
        }

        _container = new BlobContainerClient(Azurite.ConnectionString, _containerName);
        await _container.CreateIfNotExistsAsync();
        _store = new AzureBlobClaimCheckStore(_container);
    }

    public async Task DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DeleteIfExistsAsync();
        }
    }

    [AzuriteFact]
    public async Task round_trip_store_load_delete()
    {
        var payload = Encoding.UTF8.GetBytes("hello, claim check world");

        var token = await _store.StoreAsync(payload, "text/plain");

        token.Id.ShouldNotBeNullOrWhiteSpace();
        token.ContentType.ShouldBe("text/plain");
        token.Length.ShouldBe(payload.Length);

        var loaded = await _store.LoadAsync(token);
        loaded.ToArray().ShouldBe(payload);

        await _store.DeleteAsync(token);

        // After delete, the blob should not exist any more.
        var blob = _container.GetBlobClient(token.Id);
        (await blob.ExistsAsync()).Value.ShouldBeFalse();
    }

    [AzuriteFact]
    public async Task uploads_use_supplied_content_type()
    {
        var payload = new byte[] { 1, 2, 3, 4, 5 };
        const string contentType = "application/x-wolverine-test";

        var token = await _store.StoreAsync(payload, contentType);

        var blob = _container.GetBlobClient(token.Id);
        var properties = await blob.GetPropertiesAsync();
        properties.Value.ContentType.ShouldBe(contentType);
    }

    [AzuriteFact]
    public async Task delete_is_idempotent()
    {
        var token = new ClaimCheckToken("does-not-exist-" + Guid.NewGuid().ToString("N"), "text/plain", 0);

        // Should not throw even though the blob was never created.
        await _store.DeleteAsync(token);
    }

    [AzuriteFact]
    public async Task connection_string_constructor_works()
    {
        await using var _ = new DisposableContainer(_containerName + "-cs");
        var altStore = new AzureBlobClaimCheckStore(Azurite.ConnectionString, _containerName + "-cs");
        await altStore.ContainerClient.CreateIfNotExistsAsync();

        var token = await altStore.StoreAsync(new byte[] { 9, 8, 7 }, "application/octet-stream");
        var loaded = await altStore.LoadAsync(token);

        loaded.ToArray().ShouldBe(new byte[] { 9, 8, 7 });
    }

    private sealed class DisposableContainer : IAsyncDisposable
    {
        private readonly BlobContainerClient _client;
        public DisposableContainer(string name)
        {
            _client = new BlobContainerClient(Azurite.ConnectionString, name);
        }

        public async ValueTask DisposeAsync()
        {
            await _client.DeleteIfExistsAsync();
        }
    }
}
