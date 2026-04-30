using Shouldly;
using Wolverine.Runtime.Serialization.Encryption;
using Xunit;

namespace CoreTests.Runtime.Serialization.Encryption;

public class CachingKeyProviderTests
{
    private static byte[] Key32(byte fill) => Enumerable.Repeat(fill, 32).ToArray();

    private sealed class CountingProvider : IKeyProvider
    {
        private readonly Dictionary<string, byte[]> _keys;
        public int CallCount;
        public Func<Task>? Hook;

        public CountingProvider(Dictionary<string, byte[]> keys, string defaultKeyId)
        {
            _keys = keys;
            DefaultKeyId = defaultKeyId;
        }

        public string DefaultKeyId { get; }

        public async ValueTask<byte[]> GetKeyAsync(string keyId, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref CallCount);
            if (Hook is not null) await Hook().ConfigureAwait(false);
            return _keys[keyId];
        }
    }

    [Fact]
    public async Task first_call_hits_inner_then_cache_serves_subsequent_calls()
    {
        var inner = new CountingProvider(new() { ["k1"] = Key32(0x01) }, "k1");
        var caching = new CachingKeyProvider(inner, TimeSpan.FromMinutes(1));

        await caching.GetKeyAsync("k1", default);
        await caching.GetKeyAsync("k1", default);
        await caching.GetKeyAsync("k1", default);

        inner.CallCount.ShouldBe(1);
    }

    [Fact]
    public async Task ttl_expiry_re_fetches()
    {
        var inner = new CountingProvider(new() { ["k1"] = Key32(0x01) }, "k1");
        var caching = new CachingKeyProvider(inner, TimeSpan.FromMilliseconds(50));

        await caching.GetKeyAsync("k1", default);
        await Task.Delay(80);
        await caching.GetKeyAsync("k1", default);

        inner.CallCount.ShouldBe(2);
    }

    [Fact]
    public void default_key_id_forwards_without_caching()
    {
        var inner = new CountingProvider(new() { ["k1"] = Key32(0x01) }, "k1");
        var caching = new CachingKeyProvider(inner, TimeSpan.FromMinutes(1));

        caching.DefaultKeyId.ShouldBe("k1");
        inner.CallCount.ShouldBe(0);
    }

    [Fact]
    public async Task concurrent_requests_for_same_key_deduplicate()
    {
        var inner = new CountingProvider(new() { ["k1"] = Key32(0x01) }, "k1");
        var release = new TaskCompletionSource();
        inner.Hook = () => release.Task;

        var caching = new CachingKeyProvider(inner, TimeSpan.FromMinutes(1));

        var task1 = caching.GetKeyAsync("k1", default).AsTask();
        var task2 = caching.GetKeyAsync("k1", default).AsTask();
        var task3 = caching.GetKeyAsync("k1", default).AsTask();

        // GetOrAdd is synchronous, so dedup has already happened — no need to
        // sleep before releasing. Only ONE call has even reached Hook().
        release.SetResult();

        await Task.WhenAll(task1, task2, task3);
        inner.CallCount.ShouldBe(1);
    }

    [Fact]
    public async Task different_keys_do_not_block_each_other()
    {
        var inner = new CountingProvider(
            new() { ["k1"] = Key32(0x01), ["k2"] = Key32(0x02) },
            "k1");
        var caching = new CachingKeyProvider(inner, TimeSpan.FromMinutes(1));

        await caching.GetKeyAsync("k1", default);
        await caching.GetKeyAsync("k2", default);

        inner.CallCount.ShouldBe(2);
    }

    [Fact]
    public void ttl_must_be_positive()
    {
        var inner = new CountingProvider(new() { ["k1"] = Key32(0x01) }, "k1");
        Should.Throw<ArgumentOutOfRangeException>(() =>
            new CachingKeyProvider(inner, TimeSpan.Zero));
    }

    [Fact]
    public async Task faulted_fetch_is_evicted_so_next_caller_retries()
    {
        // First call: provider throws. Second call (after eviction): provider succeeds.
        var attempt = 0;
        var provider = new ThrowingThenSucceedingProvider(_ =>
        {
            attempt++;
            if (attempt == 1) throw new InvalidOperationException("transient");
            return Key32(0x01);
        }, "k1");

        var caching = new CachingKeyProvider(provider, TimeSpan.FromMinutes(1));

        await Should.ThrowAsync<InvalidOperationException>(async () =>
            await caching.GetKeyAsync("k1", default));

        // The faulted entry should have been evicted; second call must hit the inner
        // provider again and succeed.
        var key = await caching.GetKeyAsync("k1", default);
        key.ShouldBe(Key32(0x01));
        attempt.ShouldBe(2);
    }

    private sealed class ThrowingThenSucceedingProvider : IKeyProvider
    {
        private readonly Func<string, byte[]> _resolve;

        public ThrowingThenSucceedingProvider(Func<string, byte[]> resolve, string defaultKeyId)
        {
            _resolve = resolve;
            DefaultKeyId = defaultKeyId;
        }

        public string DefaultKeyId { get; }

        public ValueTask<byte[]> GetKeyAsync(string keyId, CancellationToken cancellationToken)
            => ValueTask.FromResult(_resolve(keyId));
    }
}
