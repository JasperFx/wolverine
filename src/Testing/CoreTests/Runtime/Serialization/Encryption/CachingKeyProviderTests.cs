using System.Diagnostics;
using Shouldly;
using Wolverine.Runtime;
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

        await caching.GetKeyAsync("k1", TestContext.Current.CancellationToken);
        await caching.GetKeyAsync("k1", TestContext.Current.CancellationToken);
        await caching.GetKeyAsync("k1", TestContext.Current.CancellationToken);

        inner.CallCount.ShouldBe(1);
    }

    [Fact]
    public async Task ttl_expiry_re_fetches()
    {
        var inner = new CountingProvider(new() { ["k1"] = Key32(0x01) }, "k1");
        var caching = new CachingKeyProvider(inner, TimeSpan.FromMilliseconds(50));

        await caching.GetKeyAsync("k1", TestContext.Current.CancellationToken);
        await Task.Delay(80, TestContext.Current.CancellationToken);
        await caching.GetKeyAsync("k1", TestContext.Current.CancellationToken);

        inner.CallCount.ShouldBe(2);
    }

    [Fact]
    public async Task concurrent_requests_for_same_key_deduplicate()
    {
        var inner = new CountingProvider(new() { ["k1"] = Key32(0x01) }, "k1");
        var release = new TaskCompletionSource();
        inner.Hook = () => release.Task;

        var caching = new CachingKeyProvider(inner, TimeSpan.FromMinutes(1));

        var task1 = caching.GetKeyAsync("k1", TestContext.Current.CancellationToken).AsTask();
        var task2 = caching.GetKeyAsync("k1", TestContext.Current.CancellationToken).AsTask();
        var task3 = caching.GetKeyAsync("k1", TestContext.Current.CancellationToken).AsTask();

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

        await caching.GetKeyAsync("k1", TestContext.Current.CancellationToken);
        await caching.GetKeyAsync("k2", TestContext.Current.CancellationToken);

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
        var key = await caching.GetKeyAsync("k1", TestContext.Current.CancellationToken);
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

    [Fact]
    public async Task per_caller_cancellation_does_not_propagate_to_co_waiters()
    {
        using var firstCallerCts = new CancellationTokenSource();
        using var secondCallerCts = new CancellationTokenSource();

        var gate = new TaskCompletionSource<byte[]>();
        var inner = new GatedKeyProvider("k1", gate.Task);
        var sut = new CachingKeyProvider(inner, TimeSpan.FromMinutes(5));

        var first = sut.GetKeyAsync("k1", firstCallerCts.Token).AsTask();
        var second = sut.GetKeyAsync("k1", secondCallerCts.Token).AsTask();

        await firstCallerCts.CancelAsync();

#pragma warning disable VSTHRD003 // Avoid awaiting foreign Tasks
        await Should.ThrowAsync<OperationCanceledException>(() => first);
#pragma warning restore VSTHRD003 // Avoid awaiting foreign Tasks

        var keyBytes = Key32(0x42);
        gate.SetResult(keyBytes);

        var secondResult = await second;
        secondResult.ShouldBe(keyBytes);
    }

    private sealed class GatedKeyProvider : IKeyProvider
    {
        private readonly Task<byte[]> _gate;
        public GatedKeyProvider(string defaultKeyId, Task<byte[]> gate)
        {
            DefaultKeyId = defaultKeyId;
            _gate = gate;
        }

        public string DefaultKeyId { get; }

        public async ValueTask<byte[]> GetKeyAsync(string keyId, CancellationToken cancellationToken)
        {
#pragma warning disable VSTHRD003 // Avoid awaiting foreign Tasks
            return await _gate.ConfigureAwait(false);
#pragma warning restore VSTHRD003 // Avoid awaiting foreign Tasks
        }
    }

    [Fact]
    public async Task cache_evicts_least_recently_used_when_max_entries_exceeded()
    {
        var inner = new MultiKeyCountingProvider("a");
        var sut = new CachingKeyProvider(inner, TimeSpan.FromMinutes(5), maxEntries: 3);

        await sut.GetKeyAsync("a", TestContext.Current.CancellationToken);
        await sut.GetKeyAsync("b", TestContext.Current.CancellationToken);
        await sut.GetKeyAsync("c", TestContext.Current.CancellationToken);
        await sut.GetKeyAsync("a", TestContext.Current.CancellationToken);  // touch 'a' so 'b' becomes oldest
        await sut.GetKeyAsync("d", TestContext.Current.CancellationToken);  // forces eviction of 'b'

        inner.CallsFor("a").ShouldBe(1);
        inner.CallsFor("b").ShouldBe(1);
        inner.CallsFor("c").ShouldBe(1);
        inner.CallsFor("d").ShouldBe(1);

        await sut.GetKeyAsync("b", TestContext.Current.CancellationToken);  // evicted, must re-fetch
        inner.CallsFor("b").ShouldBe(2);

        await sut.GetKeyAsync("a", TestContext.Current.CancellationToken);  // still cached
        inner.CallsFor("a").ShouldBe(1);
    }

    private sealed class MultiKeyCountingProvider : IKeyProvider
    {
        private readonly Dictionary<string, int> _counts = new();
        public MultiKeyCountingProvider(string defaultKeyId) { DefaultKeyId = defaultKeyId; }
        public string DefaultKeyId { get; }
        public ValueTask<byte[]> GetKeyAsync(string keyId, CancellationToken cancellationToken)
        {
            lock (_counts) { _counts[keyId] = _counts.GetValueOrDefault(keyId) + 1; }
            return new ValueTask<byte[]>(Enumerable.Repeat((byte)keyId[0], 32).ToArray());
        }
        public int CallsFor(string keyId)
        {
            lock (_counts) { return _counts.GetValueOrDefault(keyId); }
        }
    }

    [Fact]
    public async Task max_entries_one_evicts_immediately_on_second_distinct_key()
    {
        // Edge case: a 1-slot cache must still satisfy the LRU contract — every
        // distinct key kicks the previous one out, but a repeat of the just-fetched
        // key is still served from cache.
        var inner = new MultiKeyCountingProvider("a");
        var sut = new CachingKeyProvider(inner, TimeSpan.FromMinutes(5), maxEntries: 1);

        await sut.GetKeyAsync("a", TestContext.Current.CancellationToken);
        await sut.GetKeyAsync("a", TestContext.Current.CancellationToken);                 // still cached
        inner.CallsFor("a").ShouldBe(1);

        await sut.GetKeyAsync("b", TestContext.Current.CancellationToken);                 // evicts 'a'
        inner.CallsFor("b").ShouldBe(1);

        await sut.GetKeyAsync("a", TestContext.Current.CancellationToken);                 // 'a' was evicted, must re-fetch
        inner.CallsFor("a").ShouldBe(2);
    }

    [Fact]
    public async Task entry_just_before_ttl_is_still_cached()
    {
        // Boundary opposite to ttl_expiry_re_fetches: prove the entry remains
        // valid up to (but not past) the TTL, so a TTL refactor that flipped
        // > vs >= would be caught.
        var inner = new CountingProvider(new() { ["k1"] = Key32(0x01) }, "k1");
        var caching = new CachingKeyProvider(inner, TimeSpan.FromSeconds(5));

        await caching.GetKeyAsync("k1", TestContext.Current.CancellationToken);
        await Task.Delay(50, TestContext.Current.CancellationToken);                                // well within TTL
        await caching.GetKeyAsync("k1", TestContext.Current.CancellationToken);

        inner.CallCount.ShouldBe(1);
    }

    private sealed class ScriptedKeyProvider : IKeyProvider
    {
        private readonly Queue<Func<ValueTask<byte[]>>> _answers = new();
        public int CallCount;

        public ScriptedKeyProvider(string defaultKeyId) { DefaultKeyId = defaultKeyId; }

        public string DefaultKeyId { get; }

        public ScriptedKeyProvider Returns(byte[] key)
        {
            _answers.Enqueue(() => ValueTask.FromResult(key));
            return this;
        }

        public ScriptedKeyProvider Throws(Exception ex)
        {
            _answers.Enqueue(() => ValueTask.FromException<byte[]>(ex));
            return this;
        }

        public ScriptedKeyProvider WaitsFor(Task<byte[]> answer)
        {
#pragma warning disable VSTHRD003 // Avoid awaiting foreign Tasks
            _answers.Enqueue(() => new ValueTask<byte[]>(answer));
#pragma warning restore VSTHRD003
            return this;
        }

        public ValueTask<byte[]> GetKeyAsync(string keyId, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref CallCount);
            return _answers.Dequeue()();
        }
    }

    private static readonly TimeSpan ShortTtl = TimeSpan.FromMilliseconds(50);

    private static Task WaitPastShortTtl() => Task.Delay(80, TestContext.Current.CancellationToken);

    [Fact]
    public async Task refresh_failure_after_ttl_still_throws_by_default()
    {
        var inner = new ScriptedKeyProvider("k1")
            .Returns(Key32(0x01))
            .Throws(new TimeoutException("kms throttled"));
        var caching = new CachingKeyProvider(inner, ShortTtl);

        await caching.GetKeyAsync("k1", TestContext.Current.CancellationToken);
        await WaitPastShortTtl();

        await Should.ThrowAsync<TimeoutException>(async () =>
            await caching.GetKeyAsync("k1", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task transient_refresh_failure_after_ttl_serves_the_last_known_key()
    {
        var inner = new ScriptedKeyProvider("k1")
            .Returns(Key32(0x01))
            .Throws(new TimeoutException("kms throttled"));
        var caching = new CachingKeyProvider(inner, ShortTtl, serveLastKnownKeyOnRefreshFailure: true);

        await caching.GetKeyAsync("k1", TestContext.Current.CancellationToken);
        await WaitPastShortTtl();

        var key = await caching.GetKeyAsync("k1", TestContext.Current.CancellationToken);

        key.ShouldBe(Key32(0x01));
        inner.CallCount.ShouldBe(2);
    }

    [Fact]
    public async Task inner_timeout_cancellation_counts_as_transient()
    {
        // The inner provider is called without a token, so a cancellation can only be its own timeout
        var inner = new ScriptedKeyProvider("k1")
            .Returns(Key32(0x01))
            .Throws(new TaskCanceledException("token request timed out"));
        var caching = new CachingKeyProvider(inner, ShortTtl, serveLastKnownKeyOnRefreshFailure: true);

        await caching.GetKeyAsync("k1", TestContext.Current.CancellationToken);
        await WaitPastShortTtl();

        (await caching.GetKeyAsync("k1", TestContext.Current.CancellationToken)).ShouldBe(Key32(0x01));
    }

    [Fact]
    public async Task a_key_served_from_last_known_bytes_is_refreshed_on_the_next_call()
    {
        var inner = new ScriptedKeyProvider("k1")
            .Returns(Key32(0x01))
            .Throws(new TimeoutException("kms throttled"))
            .Throws(new TimeoutException("kms still throttled"))
            .Returns(Key32(0x01));
        var caching = new CachingKeyProvider(inner, TimeSpan.FromMilliseconds(200),
            serveLastKnownKeyOnRefreshFailure: true);

        await caching.GetKeyAsync("k1", TestContext.Current.CancellationToken);
        await Task.Delay(250, TestContext.Current.CancellationToken);

        // Each call while the inner provider fails retries it and falls back again...
        (await caching.GetKeyAsync("k1", TestContext.Current.CancellationToken)).ShouldBe(Key32(0x01));
        (await caching.GetKeyAsync("k1", TestContext.Current.CancellationToken)).ShouldBe(Key32(0x01));
        inner.CallCount.ShouldBe(3);

        // ...until a refresh succeeds, which is cached for a full TTL again
        (await caching.GetKeyAsync("k1", TestContext.Current.CancellationToken)).ShouldBe(Key32(0x01));
        (await caching.GetKeyAsync("k1", TestContext.Current.CancellationToken)).ShouldBe(Key32(0x01));
        inner.CallCount.ShouldBe(4);
    }

    [Fact]
    public async Task key_not_found_on_refresh_throws_and_evicts_the_last_known_key()
    {
        var inner = new ScriptedKeyProvider("k1")
            .Returns(Key32(0x01))
            .Throws(new KeyNotFoundException("key version disabled"))
            .Throws(new TimeoutException("kms throttled"));
        var caching = new CachingKeyProvider(inner, ShortTtl, serveLastKnownKeyOnRefreshFailure: true);

        await caching.GetKeyAsync("k1", TestContext.Current.CancellationToken);
        await WaitPastShortTtl();

        await Should.ThrowAsync<KeyNotFoundException>(async () =>
            await caching.GetKeyAsync("k1", TestContext.Current.CancellationToken));

        // The key is gone, so a later transient failure has nothing to fall back on
        await Should.ThrowAsync<TimeoutException>(async () =>
            await caching.GetKeyAsync("k1", TestContext.Current.CancellationToken));
        inner.CallCount.ShouldBe(3);
    }

    [Fact]
    public async Task first_fetch_failure_still_throws_with_nothing_to_fall_back_on()
    {
        var inner = new ScriptedKeyProvider("k1")
            .Throws(new TimeoutException("kms throttled"))
            .Returns(Key32(0x01));
        var caching = new CachingKeyProvider(inner, TimeSpan.FromMinutes(1), serveLastKnownKeyOnRefreshFailure: true);

        await Should.ThrowAsync<TimeoutException>(async () =>
            await caching.GetKeyAsync("k1", TestContext.Current.CancellationToken));

        (await caching.GetKeyAsync("k1", TestContext.Current.CancellationToken)).ShouldBe(Key32(0x01));
        inner.CallCount.ShouldBe(2);
    }

    [Fact]
    public async Task concurrent_callers_during_a_failing_refresh_share_one_inner_call_and_the_last_known_key()
    {
        var refresh = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var inner = new ScriptedKeyProvider("k1")
            .Returns(Key32(0x01))
            .WaitsFor(refresh.Task);
        var caching = new CachingKeyProvider(inner, ShortTtl, serveLastKnownKeyOnRefreshFailure: true);

        await caching.GetKeyAsync("k1", TestContext.Current.CancellationToken);
        await WaitPastShortTtl();

        var callers = Enumerable.Range(0, 5)
            .Select(_ => caching.GetKeyAsync("k1", TestContext.Current.CancellationToken).AsTask())
            .ToArray();

        refresh.SetException(new TimeoutException("kms throttled"));

        var keys = await Task.WhenAll(callers);

        keys.ShouldAllBe(key => key.SequenceEqual(Key32(0x01)));
        inner.CallCount.ShouldBe(2);
    }

    [Fact]
    public async Task caller_cancellation_during_a_failing_refresh_does_not_stop_co_waiters_getting_the_last_known_key()
    {
        using var firstCallerCts = new CancellationTokenSource();
        var refresh = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var inner = new ScriptedKeyProvider("k1")
            .Returns(Key32(0x01))
            .WaitsFor(refresh.Task);
        var caching = new CachingKeyProvider(inner, ShortTtl, serveLastKnownKeyOnRefreshFailure: true);

        await caching.GetKeyAsync("k1", TestContext.Current.CancellationToken);
        await WaitPastShortTtl();

        var first = caching.GetKeyAsync("k1", firstCallerCts.Token).AsTask();
        var second = caching.GetKeyAsync("k1", TestContext.Current.CancellationToken).AsTask();

        await firstCallerCts.CancelAsync();
#pragma warning disable VSTHRD003 // Avoid awaiting foreign Tasks
        await Should.ThrowAsync<OperationCanceledException>(() => first);
#pragma warning restore VSTHRD003

        refresh.SetException(new TimeoutException("kms throttled"));

        (await second).ShouldBe(Key32(0x01));
    }

    [Fact]
    public async Task cold_key_inner_cancellation_is_evicted_and_retried()
    {
        var inner = new ScriptedKeyProvider("k1")
            .Throws(new TaskCanceledException("kms token timeout"))
            .Returns(Key32(0x01));
        var caching = new CachingKeyProvider(inner, TimeSpan.FromMinutes(1));

        await Should.ThrowAsync<TaskCanceledException>(async () =>
            await caching.GetKeyAsync("k1", TestContext.Current.CancellationToken));

        (await caching.GetKeyAsync("k1", TestContext.Current.CancellationToken)).ShouldBe(Key32(0x01));
        inner.CallCount.ShouldBe(2);
    }

    [Fact]
    public async Task inner_cancellation_on_refresh_is_evicted_and_retried()
    {
        var inner = new ScriptedKeyProvider("k1")
            .Returns(Key32(0x01))
            .Throws(new TaskCanceledException("kms token timeout"))
            .Returns(Key32(0x01));
        var caching = new CachingKeyProvider(inner, ShortTtl);

        await caching.GetKeyAsync("k1", TestContext.Current.CancellationToken);
        await WaitPastShortTtl();

        await Should.ThrowAsync<TaskCanceledException>(async () =>
            await caching.GetKeyAsync("k1", TestContext.Current.CancellationToken));

        (await caching.GetKeyAsync("k1", TestContext.Current.CancellationToken)).ShouldBe(Key32(0x01));
        inner.CallCount.ShouldBe(3);
    }

    [Fact]
    public async Task a_refresh_still_in_flight_past_its_ttl_keeps_the_last_known_key_for_the_next_refresh()
    {
        var slowRefresh = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var inner = new ScriptedKeyProvider("k1")
            .Returns(Key32(0x01))
            .WaitsFor(slowRefresh.Task)
            .Throws(new TimeoutException("kms throttled"));
        var caching = new CachingKeyProvider(inner, ShortTtl, serveLastKnownKeyOnRefreshFailure: true);

        await caching.GetKeyAsync("k1", TestContext.Current.CancellationToken);
        await WaitPastShortTtl();

        var waitingOnSlowRefresh = caching.GetKeyAsync("k1", TestContext.Current.CancellationToken).AsTask();
        await WaitPastShortTtl();

        // The slow refresh has not completed, so only the bytes it started with can be served
        (await caching.GetKeyAsync("k1", TestContext.Current.CancellationToken)).ShouldBe(Key32(0x01));
        inner.CallCount.ShouldBe(3);

        slowRefresh.SetResult(Key32(0x01));
#pragma warning disable VSTHRD003 // Avoid awaiting foreign Tasks
        (await waitingOnSlowRefresh).ShouldBe(Key32(0x01));
#pragma warning restore VSTHRD003
    }

    [Fact]
    public async Task a_fallback_adds_the_last_known_served_event_to_the_refreshing_activity()
    {
        using var activity = new Activity("test").Start();
        var inner = new ScriptedKeyProvider("k1")
            .Returns(Key32(0x01))
            .Throws(new TimeoutException("kms throttled"));
        var caching = new CachingKeyProvider(inner, ShortTtl, serveLastKnownKeyOnRefreshFailure: true);

        await caching.GetKeyAsync("k1", TestContext.Current.CancellationToken);
        await WaitPastShortTtl();
        await caching.GetKeyAsync("k1", TestContext.Current.CancellationToken);

        var served = activity.Events.ShouldHaveSingleItem();
        served.Name.ShouldBe(WolverineTracing.EncryptionKeyLastKnownServed);
        served.Tags.ShouldContain(t => t.Key == WolverineTracing.EncryptionKeyId && (string?)t.Value == "k1");
        served.Tags.ShouldContain(t => t.Key == "exception.type" && (string?)t.Value == typeof(TimeoutException).FullName);
    }
}
