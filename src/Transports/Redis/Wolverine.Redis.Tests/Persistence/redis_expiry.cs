using Shouldly;
using StackExchange.Redis;
using Wolverine.Redis.Internal;
using Xunit;

namespace Wolverine.Redis.Tests.Persistence;

/// <summary>
/// The one capability Redis has that an object store genuinely does not: <c>EXPIRE</c>. Offered because
/// it is native and one command, rather than emulated with a stored timestamp and a sweeper.
/// </summary>
public class redis_expiry : IAsyncLifetime
{
    private RedisDocumentSession _session = null!;
    private ConnectionMultiplexer _multiplexer = null!;
    private string _prefix = null!;

    public ValueTask InitializeAsync()
    {
        _prefix = RedisPersistenceServer.UniquePrefix("expiry");

        if (RedisPersistenceServer.IsRunning)
        {
            _multiplexer = RedisPersistenceServer.Connect();
            _session = new RedisDocumentSession(_multiplexer, configuration());
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        _multiplexer?.Dispose();
        return ValueTask.CompletedTask;
    }

    private RedisPersistenceConfiguration configuration()
    {
        var configuration = new RedisPersistenceConfiguration();

        configuration.Store<ExpiringQuote>(x =>
        {
            x.KeyFor = ctx => $"{_prefix}:quote:{ctx.Id}";
            x.ExpiresAfter = TimeSpan.FromMinutes(30);
        });

        configuration.Store<PermanentQuote>(x => x.KeyFor = ctx => $"{_prefix}:permanent:{ctx.Id}");

        configuration.Saga<ExpiringSaga>(x =>
        {
            x.KeyFor = ctx => $"{_prefix}:saga:{ctx.Id}";
            x.ExpiresAfter = TimeSpan.FromMinutes(30);
        });

        return configuration;
    }

    [RedisFact]
    public async Task a_document_with_an_expiry_gets_a_ttl()
    {
        await _session.StoreAsync(new ExpiringQuote("one", 42), null, TestContext.Current.CancellationToken);

        var ttl = await _multiplexer.GetDatabase().KeyTimeToLiveAsync($"{_prefix}:quote:one");

        ttl.ShouldNotBeNull();
        ttl.Value.ShouldBeGreaterThan(TimeSpan.FromMinutes(29));
        ttl.Value.ShouldBeLessThanOrEqualTo(TimeSpan.FromMinutes(30));

        // ...and is still readable while it is alive
        (await _session.LoadAsync<ExpiringQuote>("one", null, TestContext.Current.CancellationToken))!
            .Amount.ShouldBe(42);
    }

    [RedisFact]
    public async Task a_document_without_an_expiry_gets_no_ttl()
    {
        await _session.StoreAsync(new PermanentQuote("one", 42), null, TestContext.Current.CancellationToken);

        (await _multiplexer.GetDatabase().KeyTimeToLiveAsync($"{_prefix}:permanent:one")).ShouldBeNull();
    }

    /// <summary>
    /// A saga's expiry is re-applied on every write, so the window slides forward from the last message
    /// rather than from the first. Not a substitute for a timeout message, which lets the saga run code
    /// before it disappears — this one just vanishes.
    /// </summary>
    [RedisFact]
    public async Task a_saga_expiry_slides_forward_on_every_write()
    {
        var saga = new ExpiringSaga { Id = "one" };
        await _session.InsertSagaAsync(saga, null, TestContext.Current.CancellationToken);

        var key = $"{_prefix}:saga:one";
        var database = _multiplexer.GetDatabase();

        (await database.KeyTimeToLiveAsync(key))!.Value.ShouldBeGreaterThan(TimeSpan.FromMinutes(29));

        // Wind the TTL down to something clearly shorter, then write through the saga path
        await database.KeyExpireAsync(key, TimeSpan.FromMinutes(1));

        var state = await _session.LoadSagaAsync<ExpiringSaga>("one", null, TestContext.Current.CancellationToken);
        await _session.UpdateSagaAsync(state.Saga!, state.Version, null, TestContext.Current.CancellationToken);

        (await database.KeyTimeToLiveAsync(key))!.Value.ShouldBeGreaterThan(TimeSpan.FromMinutes(29));
    }
}

public record ExpiringQuote(string Id, int Amount);

public record PermanentQuote(string Id, int Amount);

public class ExpiringSaga : Saga
{
    public string Id { get; set; } = null!;
}
