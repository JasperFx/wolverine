using JasperFx.Core;
using JasperFx.Documents;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Raven.Client.Documents;
using Raven.Client.Documents.Operations.Indexes;
using Shouldly;
using Wolverine;
using Wolverine.ComplianceTests.Sagas;
using Wolverine.Persistence.Sagas;
using Wolverine.RavenDb;
using Wolverine.Tracking;

namespace RavenDbTests;

/// <summary>
/// GH-5676. RavenDB answers a saga query from an index that catches up with each write after the write returns, so
/// a query that does not wait for that index sees the saga as it was before its last change. The open-saga query
/// once counted a saga it had just seen completed as still open, which made
/// saga_retention_5572.queries_leave_completed_sagas_out_unless_asked fail now and then. Here indexing is paused
/// across the change and resumed a moment later, so a query that does not wait reads the stale index every time.
/// </summary>
[Collection("raven")]
public class saga_queries_wait_for_the_index_5676 : IAsyncLifetime
{
    private static readonly string SagaTypeName = typeof(RetainedSaga).FullName!;

    private readonly DatabaseFixture _driver = new();
    private IDocumentStore _store = null!;
    private IHost _host = null!;

    public async ValueTask InitializeAsync()
    {
        _store = _driver.StartRavenStore();

        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery().IncludeType<RetainedSaga>();
                opts.Durability.Mode = DurabilityMode.Solo;

                opts.Services.AddSingleton(_store);
                opts.UseRavenDbPersistence();
            }).StartAsync(TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
        _store.Dispose();
        _driver.Dispose();
    }

    private ISagaStoreDiagnostics sagas => _host.GetRuntime().SagaStorage;

    private async Task<string> startAsync()
    {
        var id = Guid.NewGuid().ToString();
        await _host.InvokeMessageAndWaitAsync(new StartRetainedSaga(id));
        return id;
    }

    // Writes while indexing is paused, then resumes indexing half a second after the query starts
    private async Task<T> queryAfterAnUnindexedChangeAsync<T>(Func<Task> change, Func<Task<T>> query)
    {
        await _store.Maintenance.SendAsync(new StopIndexingOperation());
        await change();

        var resume = Task.Run(async () =>
        {
            await Task.Delay(500.Milliseconds());
            await _store.Maintenance.SendAsync(new StartIndexingOperation());
        });

        try
        {
            return await query();
        }
        finally
        {
            await resume;
        }
    }

    [Fact]
    public async Task the_open_saga_query_leaves_out_a_completion_the_index_has_not_caught_up_with()
    {
        var token = TestContext.Current.CancellationToken;
        var options = new DocumentQueryOptions(1, 10);

        await startAsync();
        var finishing = await startAsync();

        // Builds the index the open-saga query reads
        (await sagas.QuerySagasAsync(SagaTypeName, options, token)).TotalCount.ShouldBe(2);

        var open = await queryAfterAnUnindexedChangeAsync(
            () => _host.InvokeMessageAndWaitAsync(new FinishRetainedSaga(finishing)),
            () => sagas.QuerySagasAsync(SagaTypeName, options, token));

        open.TotalCount.ShouldBe(1);
        open.Instances.ShouldNotContain(x => x.Identity.Equals(finishing));
    }

    [Fact]
    public async Task the_idle_count_leaves_out_a_completion_the_index_has_not_caught_up_with()
    {
        var token = TestContext.Current.CancellationToken;
        var idle = (ISagaIdleDiagnostics)sagas;
        var cutoff = DateTimeOffset.UtcNow.AddMinutes(5);

        await startAsync();
        var finishing = await startAsync();

        (await idle.CountIdleSagasAsync(SagaTypeName, cutoff, null, token)).ShouldBe(2);

        var count = await queryAfterAnUnindexedChangeAsync(
            () => _host.InvokeMessageAndWaitAsync(new FinishRetainedSaga(finishing)),
            () => idle.CountIdleSagasAsync(SagaTypeName, cutoff, null, token));

        count.ShouldBe(1);
    }

    [Fact]
    public async Task the_sweep_finds_a_completion_the_index_has_not_caught_up_with()
    {
        var token = TestContext.Current.CancellationToken;
        var sweeper = _host.Services.GetServices<ISagaStoreDiagnostics>().OfType<ISagaRetentionSweeper>()
            .Single(x => x.SagaTypesForRetention().Contains(typeof(RetainedSaga)));

        var open = await startAsync();
        var finishing = await startAsync();

        // Builds the index the sweep reads
        (await sweeper.DeleteCompletedSagasAsync(typeof(RetainedSaga), DateTimeOffset.UtcNow.AddMinutes(1), token))
            .ShouldBe(0);

        var deleted = await queryAfterAnUnindexedChangeAsync(
            () => _host.InvokeMessageAndWaitAsync(new FinishRetainedSaga(finishing)),
            () => sweeper.DeleteCompletedSagasAsync(typeof(RetainedSaga), DateTimeOffset.UtcNow.AddMinutes(1), token));

        deleted.ShouldBe(1);
        (await sagas.ReadSagaAsync(SagaTypeName, finishing, token)).ShouldBeNull();
        (await sagas.ReadSagaAsync(SagaTypeName, open, token)).ShouldNotBeNull();
    }
}
