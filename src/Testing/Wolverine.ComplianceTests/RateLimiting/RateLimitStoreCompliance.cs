using JasperFx.Core;
using Shouldly;
using Wolverine.RateLimiting;
using Xunit;

namespace Wolverine.ComplianceTests.RateLimiting;

public abstract class RateLimitStoreCompliance : IAsyncLifetime
{
    protected IRateLimitStore Store { get; private set; } = null!;

    protected abstract Task<IRateLimitStore> BuildStoreAsync();

    protected virtual Task InitializeStoreAsync(IRateLimitStore store)
    {
        return Task.CompletedTask;
    }

    protected virtual Task DisposeStoreAsync(IRateLimitStore store)
    {
        return Task.CompletedTask;
    }

    public async Task InitializeAsync()
    {
        Store = await BuildStoreAsync();
        await InitializeStoreAsync(Store);
    }

    public async Task DisposeAsync()
    {
        await DisposeStoreAsync(Store);

        switch (Store)
        {
            case IAsyncDisposable asyncDisposable:
                await asyncDisposable.DisposeAsync();
                break;
            case IDisposable disposable:
                disposable.Dispose();
                break;
        }
    }

    [Fact]
    public async Task allows_up_to_limit_then_denies()
    {
        var now = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var limit = new RateLimit(2, 1.Minutes());
        var bucket = RateLimitBucket.For(limit, now);
        var key = $"key-{Guid.NewGuid():N}";

        (await Store.TryAcquireAsync(new RateLimitStoreRequest(key, bucket, 1, now), CancellationToken.None))
            .Allowed.ShouldBeTrue();
        (await Store.TryAcquireAsync(new RateLimitStoreRequest(key, bucket, 1, now), CancellationToken.None))
            .Allowed.ShouldBeTrue();
        (await Store.TryAcquireAsync(new RateLimitStoreRequest(key, bucket, 1, now), CancellationToken.None))
            .Allowed.ShouldBeFalse();
    }

    [Fact]
    public async Task allows_again_in_next_bucket()
    {
        var now = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var limit = new RateLimit(1, 1.Minutes());
        var key = $"key-{Guid.NewGuid():N}";

        var bucket = RateLimitBucket.For(limit, now);
        (await Store.TryAcquireAsync(new RateLimitStoreRequest(key, bucket, 1, now), CancellationToken.None))
            .Allowed.ShouldBeTrue();

        var later = now.AddMinutes(1).AddSeconds(1);
        var nextBucket = RateLimitBucket.For(limit, later);
        (await Store.TryAcquireAsync(new RateLimitStoreRequest(key, nextBucket, 1, later), CancellationToken.None))
            .Allowed.ShouldBeTrue();
    }

    [Fact]
    public async Task honors_quantity()
    {
        var now = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var limit = new RateLimit(3, 1.Minutes());
        var bucket = RateLimitBucket.For(limit, now);
        var key = $"key-{Guid.NewGuid():N}";

        (await Store.TryAcquireAsync(new RateLimitStoreRequest(key, bucket, 2, now), CancellationToken.None))
            .Allowed.ShouldBeTrue();
        (await Store.TryAcquireAsync(new RateLimitStoreRequest(key, bucket, 2, now), CancellationToken.None))
            .Allowed.ShouldBeFalse();
    }
}
