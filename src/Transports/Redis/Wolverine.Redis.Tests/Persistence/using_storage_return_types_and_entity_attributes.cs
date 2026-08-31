using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;
using Wolverine.ComplianceTests;
using Xunit;

namespace Wolverine.Redis.Tests.Persistence;

/// <summary>
/// The shared <see cref="StorageActionCompliance" /> suite, run against Redis. Marten, EF Core,
/// Polecat, RavenDb, S3 and the in-memory provider all answer this same suite, and answering it is what
/// makes "Redis supports the declarative storage return values" a claim with the same meaning it has
/// for every other store.
/// </summary>
/// <remarks>
/// This covers what a hand-written suite keeps missing: <c>Storage.Insert()</c> and
/// <c>Storage.Update()</c> as return values (which reach <c>DetermineInsertFrame</c> and
/// <c>DetermineUpdateFrame</c>), all four <see cref="Wolverine.Persistence.StorageAction" /> arms
/// through the generic path including <c>Nothing</c>, null actions, and <c>[Entity]</c> on Before
/// methods.
/// </remarks>
public class using_storage_return_types_and_entity_attributes : StorageActionCompliance
{
    private readonly string _prefix = RedisPersistenceServer.UniquePrefix("todos");
    private ConnectionMultiplexer _multiplexer = null!;

    protected override void configureWolverine(WolverineOptions opts)
    {
        opts.Durability.Mode = DurabilityMode.Solo;

        // AbortOnConnectFail is off inside Connect(), so this builds a host even with no Redis
        // listening -- which is what lets initialize() below be the one place that skips.
        _multiplexer = RedisPersistenceServer.Connect();
        opts.Services.AddSingleton<IConnectionMultiplexer>(_multiplexer);

        opts.UseRedisPersistence(redis => redis.Store<Todo>(x => x.KeyFor = ctx => keyFor(ctx.Id.ToString()!)));
    }

    // The base class builds the host first and calls this after, and building the host makes no Redis
    // call -- so this is the first place that needs a live server, and the place to skip from. The
    // compliance suite's tests are [Fact], so [RedisFact] is not available to them.
    protected override Task initialize()
    {
        Assert.SkipUnless(RedisPersistenceServer.IsRunning, RedisPersistenceServer.SkipReason);

        Disposables.Add(_multiplexer);

        return Task.CompletedTask;
    }

    public override async Task<Todo?> Load(string id)
    {
        var value = await _multiplexer.GetDatabase().StringGetAsync(keyFor(id));

        return value.IsNull ? null : JsonSerializer.Deserialize<Todo>((byte[])value!, serialization);
    }

    public override Task Persist(Todo todo)
    {
        return _multiplexer.GetDatabase()
            .StringSetAsync(keyFor(todo.Id), JsonSerializer.SerializeToUtf8Bytes(todo, serialization));
    }

    // Read and written straight through StackExchange.Redis rather than through Wolverine, so these
    // have to agree with the mapping's key function and with RedisDocumentSerializer.Default by hand.
    private string keyFor(string id) => $"{_prefix}:{id}";

    private static readonly JsonSerializerOptions serialization = new(JsonSerializerDefaults.Web);
}
