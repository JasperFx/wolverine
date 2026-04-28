using Shouldly;
using Wolverine.Persistence;
using Xunit;

namespace CoreTests.Persistence.ClaimCheck;

public class FileSystemClaimCheckStoreTests : IDisposable
{
    private readonly string _directory;
    private readonly FileSystemClaimCheckStore _store;

    public FileSystemClaimCheckStoreTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "wolverine-claim-check-tests-" + Guid.NewGuid().ToString("N"));
        _store = new FileSystemClaimCheckStore(_directory);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }
        catch
        {
            // best-effort cleanup
        }
    }

    [Fact]
    public async Task store_load_delete_round_trip()
    {
        var bytes = new byte[] { 1, 2, 3, 4, 5 };
        var token = await _store.StoreAsync(bytes, "application/octet-stream");

        token.ShouldNotBeNull();
        token.Length.ShouldBe(bytes.Length);
        token.ContentType.ShouldBe("application/octet-stream");

        var loaded = await _store.LoadAsync(token);
        loaded.ToArray().ShouldBe(bytes);

        await _store.DeleteAsync(token);

        await Should.ThrowAsync<FileNotFoundException>(() => _store.LoadAsync(token));
    }

    [Fact]
    public async Task token_serialize_round_trip()
    {
        var bytes = new byte[] { 9, 8, 7 };
        var token = await _store.StoreAsync(bytes, "image/png");

        var encoded = token.Serialize();
        var decoded = ClaimCheckToken.Parse(encoded);

        decoded.ShouldBe(token);
    }

    [Fact]
    public async Task store_creates_directory_lazily()
    {
        var dir = Path.Combine(_directory, "child");
        var localStore = new FileSystemClaimCheckStore(dir);
        Directory.Exists(dir).ShouldBeTrue();

        var token = await localStore.StoreAsync(new byte[] { 1 }, "application/octet-stream");
        (await localStore.LoadAsync(token)).ToArray().ShouldBe(new byte[] { 1 });
    }
}
