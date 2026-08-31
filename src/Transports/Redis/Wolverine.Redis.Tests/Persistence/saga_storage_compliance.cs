using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using StackExchange.Redis;
using Wolverine.ComplianceTests.Sagas;
using Wolverine.Redis.Internal;
using Xunit;

namespace Wolverine.Redis.Tests.Persistence;

/// <summary>
/// The shared saga compliance suites, run against Redis. Marten, EF Core, RavenDb, CosmosDB and the
/// relational stores all answer these, and answering them is what makes "Redis can keep saga state" a
/// claim with the same meaning it has for every other store.
/// </summary>
/// <remarks>
/// These are entirely sequential and prove nothing about concurrency — see
/// <see cref="saga_optimistic_concurrency" /> for that.
/// </remarks>
public class RedisSagaHost : ISagaHost, IDisposable
{
    private readonly string _prefix = RedisPersistenceServer.UniquePrefix("saga");
    private ConnectionMultiplexer? _multiplexer;

    public Task<IHost> BuildHostAsync<TSaga>()
    {
        _multiplexer ??= RedisPersistenceServer.Connect();

        return Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;

                opts.Services.AddSingleton<IConnectionMultiplexer>(_multiplexer);
                opts.UseRedisPersistence(redis =>
                    redis.Saga(typeof(TSaga), x => x.KeyFor = ctx => keyFor(ctx.Id)));

                // Narrowed to the saga under test on purpose. Pulling in the whole compliance assembly
                // brings TodoHandler with it, whose Storage.Store<Todo>() cannot resolve against a
                // selective provider that was never asked to store a Todo -- and that fails codegen for
                // the whole host rather than for the one handler.
                opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(TSaga));
            }).StartAsync();
    }

    public Task<T?> LoadState<T>(Guid id) where T : Saga => loadAsync<T>(id);

    public Task<T?> LoadState<T>(int id) where T : Saga => loadAsync<T>(id);

    public Task<T?> LoadState<T>(long id) where T : Saga => loadAsync<T>(id);

    public Task<T?> LoadState<T>(string id) where T : Saga => loadAsync<T>(id);

    public void Dispose()
    {
        _multiplexer?.Dispose();
    }

    // Read straight through StackExchange.Redis rather than through Wolverine, so this has to agree
    // with the key function above and with RedisDocumentSerializer.Default by hand.
    private async Task<T?> loadAsync<T>(object id) where T : Saga
    {
        var value = await _multiplexer!.GetDatabase()
            .HashGetAsync(keyFor(id), RedisSagaScripts.DataField);

        return value.IsNull
            ? null
            : JsonSerializer.Deserialize<T>((byte[])value!, new JsonSerializerOptions(JsonSerializerDefaults.Web));
    }

    private string keyFor(object id) => $"{_prefix}:{id}";
}

// The skip has to happen in the CONSTRUCTOR, not inside BuildHostAsync. Several of the inherited specs
// wrap their whole body in Should.ThrowAsync<IndeterminateSagaStateIdException>(), which happily catches
// xUnit's skip exception too and reports "wrong exception type" -- so a skip raised from inside the test
// body turns those specs red rather than skipped.
public class string_identified_saga_compliance : StringIdentifiedSagaComplianceSpecs<RedisSagaHost>
{
    public string_identified_saga_compliance() => RedisPersistenceServer.SkipUnlessRunning();
}

public class guid_identified_saga_compliance : GuidIdentifiedSagaComplianceSpecs<RedisSagaHost>
{
    public guid_identified_saga_compliance() => RedisPersistenceServer.SkipUnlessRunning();
}

public class int_identified_saga_compliance : IntIdentifiedSagaComplianceSpecs<RedisSagaHost>
{
    public int_identified_saga_compliance() => RedisPersistenceServer.SkipUnlessRunning();
}

public class long_identified_saga_compliance : LongIdentifiedSagaComplianceSpecs<RedisSagaHost>
{
    public long_identified_saga_compliance() => RedisPersistenceServer.SkipUnlessRunning();
}
