using JasperFx.Core;
using Shouldly;
using Wolverine.Persistence;
using Xunit;

namespace CoreTests.Persistence.ClaimCheck;

/// <summary>
/// GH-3509: coverage for <see cref="IClaimCheckStoreWithExpiration"/> against the file-system backend.
/// The database backends run the same shape of assertions in their own suites.
/// </summary>
public class payload_expiration : IDisposable
{
    private readonly string _directory;
    private readonly FileSystemClaimCheckStore _store;

    public payload_expiration()
    {
        _directory = Path.Combine(Path.GetTempPath(), "wolverine-claim-check-ttl-" + Guid.NewGuid().ToString("N"));
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

    private async Task<ClaimCheckToken> storeAged(TimeSpan age)
    {
        var token = await _store.StoreAsync(new byte[] { 1, 2, 3 }, "application/octet-stream",
            TestContext.Current.CancellationToken);

        var path = Path.Combine(_directory, token.Id + ".bin");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow - age);

        return token;
    }

    [Fact]
    public async Task deletes_aged_payloads_and_leaves_recent_ones()
    {
        var old = await storeAged(2.Hours());
        var recent = await storeAged(1.Minutes());

        var deleted = await _store.DeleteExpiredPayloadsAsync(DateTimeOffset.UtcNow - 1.Hours(), 100,
            TestContext.Current.CancellationToken);

        deleted.ShouldBe(1);

        await Should.ThrowAsync<FileNotFoundException>(() => _store.LoadAsync(old));

        // The recent payload must survive untouched -- an over-eager sweep is worse than no sweep.
        (await _store.LoadAsync(recent, TestContext.Current.CancellationToken)).ToArray()
            .ShouldBe(new byte[] { 1, 2, 3 });
    }

    [Fact]
    public async Task deletes_the_sidecar_metadata_file_too()
    {
        var token = await storeAged(2.Hours());

        await _store.DeleteExpiredPayloadsAsync(DateTimeOffset.UtcNow - 1.Hours(), 100,
            TestContext.Current.CancellationToken);

        File.Exists(Path.Combine(_directory, token.Id + ".bin")).ShouldBeFalse();
        File.Exists(Path.Combine(_directory, token.Id + ".meta")).ShouldBeFalse();
    }

    [Fact]
    public async Task honors_the_max_count()
    {
        for (var i = 0; i < 5; i++)
        {
            await storeAged(2.Hours());
        }

        var deleted = await _store.DeleteExpiredPayloadsAsync(DateTimeOffset.UtcNow - 1.Hours(), 2,
            TestContext.Current.CancellationToken);

        deleted.ShouldBe(2);
        Directory.GetFiles(_directory, "*.bin").Length.ShouldBe(3);
    }

    [Fact]
    public async Task second_sweep_over_an_empty_store_is_a_no_op()
    {
        await storeAged(2.Hours());

        var cutoff = DateTimeOffset.UtcNow - 1.Hours();
        (await _store.DeleteExpiredPayloadsAsync(cutoff, 100, TestContext.Current.CancellationToken)).ShouldBe(1);

        // Idempotence matters because the sweeper runs on every node, so overlapping passes are expected.
        (await _store.DeleteExpiredPayloadsAsync(cutoff, 100, TestContext.Current.CancellationToken)).ShouldBe(0);
    }

    [Fact]
    public async Task a_non_positive_max_count_deletes_nothing()
    {
        await storeAged(2.Hours());

        (await _store.DeleteExpiredPayloadsAsync(DateTimeOffset.UtcNow - 1.Hours(), 0,
            TestContext.Current.CancellationToken)).ShouldBe(0);

        Directory.GetFiles(_directory, "*.bin").Length.ShouldBe(1);
    }
}
