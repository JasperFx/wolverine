using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.Persistence.Durability;
using Wolverine.Persistence.Sagas;
using Wolverine.Tracking;
using Xunit;

namespace Wolverine.ComplianceTests.Sagas;

/// <summary>
/// GH-5665. A saga's message history under database-per-tenant message storage: each tenant's rows land in that
/// tenant's message store, written with the tenant's saga, and are read back from it. The storage under test registers
/// the tenants <see cref="Red" /> and <see cref="Blue" />, each with a database of its own.
/// </summary>
public abstract class SagaHistoryMultiTenancyCompliance : IAsyncLifetime
{
    public const string Red = "red";
    public const string Blue = "blue";

    protected IHost Host { get; private set; } = null!;

    /// <summary>Create the tenant databases, before the host starts</summary>
    protected virtual Task CreateTenantDatabasesAsync() => Task.CompletedTask;

    /// <summary>
    /// Configure the saga storage under test, with <see cref="Red" /> and <see cref="Blue" /> in separate databases.
    /// Handler discovery is already restricted to the history sagas, and the host runs in Solo mode.
    /// </summary>
    protected abstract void ConfigureStorage(WolverineOptions opts);

    public async ValueTask InitializeAsync()
    {
        await CreateTenantDatabasesAsync();

        Host = await Microsoft.Extensions.Hosting.Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType<HistorySaga>()
                    .IncludeType<RetainedHistorySaga>()
                    .IncludeType(typeof(HistorySagaAdvancedHandler));
                opts.Durability.Mode = DurabilityMode.Solo;
                ConfigureStorage(opts);
            }).StartAsync(TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await Host.StopAsync();
        Host.Dispose();
    }

    private static string newId() => Guid.NewGuid().ToString();

    private Task invokeAsync(string tenantId, object message)
        => Host.TrackActivity().ExecuteAndWaitAsync(c => c.InvokeForTenantAsync(tenantId, message));

    private Task<SagaHistoryPage> readAsync<TSaga>(string id, string? tenantId) where TSaga : Saga
        => ((ISagaHistoryDiagnostics)Host.GetRuntime().SagaStorage).ReadSagaHistoryAsync(typeof(TSaga).FullName!, id,
            new SagaHistoryPageRequest(TenantId: tenantId), TestContext.Current.CancellationToken);

    /// <summary>The rows for one saga in one tenant's own message store, read from that store and nothing else</summary>
    private async Task<long> rowsInTheStoreOf<TSaga>(string tenantId, string id) where TSaga : Saga
    {
        var stores = await Host.GetRuntime().Stores.FindForTenantAsync(tenantId);
        var store = stores.OfType<ISagaHistoryStore>().ShouldHaveSingleItem();

        // Every tenant's rows, so a row written to the wrong database would show up here
        var rows = await store.ReadSagaHistoryAsync(typeof(TSaga).FullName!, id, null, 0, 100,
            TestContext.Current.CancellationToken);
        return rows.TotalCount;
    }

    private async Task<long> rowsInTheMainStore<TSaga>(string id) where TSaga : Saga
    {
        var main = Host.GetRuntime().Storage;
        if (main is MultiTenantedMessageStore tenanted) main = tenanted.Main;

        if (main is not ISagaHistoryStore store) return 0;

        var rows = await store.ReadSagaHistoryAsync(typeof(TSaga).FullName!, id, null, 0, 100,
            TestContext.Current.CancellationToken);
        return rows.TotalCount;
    }

    [Fact]
    public async Task each_tenants_history_lands_in_that_tenants_message_store()
    {
        var red = newId();
        await invokeAsync(Red, new StartHistorySaga(red));
        await invokeAsync(Red, new AdvanceHistorySaga(red));

        var blue = newId();
        await invokeAsync(Blue, new StartHistorySaga(blue));

        (await rowsInTheStoreOf<HistorySaga>(Red, red)).ShouldBe(2);
        (await rowsInTheStoreOf<HistorySaga>(Blue, red)).ShouldBe(0);
        (await rowsInTheStoreOf<HistorySaga>(Blue, blue)).ShouldBe(1);
        (await rowsInTheStoreOf<HistorySaga>(Red, blue)).ShouldBe(0);
        (await rowsInTheMainStore<HistorySaga>(red)).ShouldBe(0);

        var page = await readAsync<HistorySaga>(red, Red);
        page.TotalCount.ShouldBe(2);
        page.Events.Select(x => x.Data.GetProperty("TenantId").GetString()).ShouldAllBe(x => x == Red);

        (await readAsync<HistorySaga>(red, Blue)).TotalCount.ShouldBe(0);

        // With no tenant named, every message store is read
        (await readAsync<HistorySaga>(red, null)).TotalCount.ShouldBe(2);
    }

    [Fact]
    public async Task the_same_saga_id_in_two_tenants_keeps_two_histories()
    {
        var id = newId();
        await invokeAsync(Red, new StartHistorySaga(id));
        await invokeAsync(Blue, new StartHistorySaga(id));
        await invokeAsync(Blue, new AdvanceHistorySaga(id));

        (await readAsync<HistorySaga>(id, Red)).TotalCount.ShouldBe(1);
        (await readAsync<HistorySaga>(id, Blue)).TotalCount.ShouldBe(2);
    }

    [Fact]
    public async Task completing_a_saga_deletes_only_that_tenants_history()
    {
        var id = newId();
        await invokeAsync(Red, new StartHistorySaga(id));
        await invokeAsync(Blue, new StartHistorySaga(id));

        await invokeAsync(Red, new FinishHistorySaga(id));

        (await readAsync<HistorySaga>(id, Red)).TotalCount.ShouldBe(0);
        (await readAsync<HistorySaga>(id, Blue)).TotalCount.ShouldBe(1);
    }

    [Fact]
    public async Task the_retention_sweep_reaches_every_tenant_database()
    {
        var token = TestContext.Current.CancellationToken;

        var red = newId();
        await invokeAsync(Red, new StartRetainedHistorySaga(red));
        await invokeAsync(Red, new FinishRetainedHistorySaga(red));

        var blue = newId();
        await invokeAsync(Blue, new StartRetainedHistorySaga(blue));

        (await rowsInTheStoreOf<RetainedHistorySaga>(Red, red)).ShouldBe(2);

        await SagaHistory.SweepAsync(Host.GetRuntime(), typeof(RetainedHistorySaga), DateTimeOffset.UtcNow.AddMinutes(1),
            token);

        (await rowsInTheStoreOf<RetainedHistorySaga>(Red, red)).ShouldBe(0);
        (await rowsInTheStoreOf<RetainedHistorySaga>(Blue, blue)).ShouldBe(1);
    }
}
