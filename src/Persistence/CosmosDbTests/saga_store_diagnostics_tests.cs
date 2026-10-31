using JasperFx.Documents;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.CosmosDb;
using Wolverine.Persistence.Sagas;
using Wolverine.Tracking;

namespace CosmosDbTests;

/// <summary>
///     GH-5300. The CosmosDB <see cref="ISagaStoreDiagnostics" />: registration, point reads, listing and
///     <c>QuerySagasAsync</c> through the shared saga criteria path. Every saga shares the one container, so
///     queries here narrow to their own documents with a per-test tag in a <c>Where</c>.
/// </summary>
[Collection("cosmosdb")]
public class saga_store_diagnostics_tests : IAsyncLifetime
{
    private readonly AppFixture _fixture;
    private IHost _host = null!;

    public saga_store_diagnostics_tests(AppFixture fixture)
    {
        _fixture = fixture;
    }

    public async ValueTask InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;

                opts.Services.AddSingleton(_fixture.Client);
                opts.UseCosmosDbPersistence(AppFixture.DatabaseName);

                opts.Discovery.DisableConventionalDiscovery().IncludeType<CosmosDiagSaga>();
            }).StartAsync(TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    private ISagaStoreDiagnostics diagnostics => _host.GetRuntime().SagaStorage;

    [Fact]
    public async Task registered_saga_types_include_the_cosmos_saga()
    {
        var registered = await diagnostics.GetRegisteredSagasAsync(CancellationToken.None);

        var descriptor = registered.Single(d => d.StateType.FullName == typeof(CosmosDiagSaga).FullName);
        descriptor.StorageProvider.ShouldBe("CosmosDb");
    }

    [Fact]
    public async Task read_saga_returns_state_for_existing_instance_and_null_when_missing()
    {
        var id = Guid.NewGuid().ToString();
        await _host.InvokeMessageAndWaitAsync(new StartCosmosDiagSaga(id, "alpha"));

        var state = await diagnostics.ReadSagaAsync(typeof(CosmosDiagSaga).FullName!, id, CancellationToken.None);
        state.ShouldNotBeNull();
        state.IsCompleted.ShouldBeFalse();
        state.State.GetProperty("Note").GetString().ShouldBe("alpha");

        (await diagnostics.ReadSagaAsync(typeof(CosmosDiagSaga).FullName!, Guid.NewGuid().ToString(),
            CancellationToken.None)).ShouldBeNull();
    }

    [Fact]
    public async Task read_saga_finds_a_partitioned_saga()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.Services.AddSingleton(_fixture.Client);
                opts.UseCosmosDbPersistence(AppFixture.DatabaseName, c => c.PartitionSagasById());
                opts.Discovery.DisableConventionalDiscovery().IncludeType<CosmosDiagSaga>();
            }).StartAsync(TestContext.Current.CancellationToken);

        var id = Guid.NewGuid().ToString();
        await host.InvokeMessageAndWaitAsync(new StartCosmosDiagSaga(id, "partitioned"));

        var state = await host.GetRuntime().SagaStorage
            .ReadSagaAsync(typeof(CosmosDiagSaga).FullName!, id, CancellationToken.None);
        state.ShouldNotBeNull();
        state.State.GetProperty("Note").GetString().ShouldBe("partitioned");
    }

    [Fact]
    public void cosmos_can_filter_and_order_sagas()
    {
        var capabilities = diagnostics.GetQueryCapabilities(typeof(CosmosDiagSaga).FullName!);

        capabilities.CanQuery.ShouldBeTrue();
        capabilities.CanFilter.ShouldBeTrue();
        capabilities.CanOrder.ShouldBeTrue();
    }

    [Fact]
    public async Task query_sagas_filters_orders_pages_and_counts()
    {
        var tag = Guid.NewGuid().ToString("N");
        foreach (var n in new[] { "b", "c", "a", "d" })
        {
            await _host.InvokeMessageAndWaitAsync(new StartCosmosDiagSaga(Guid.NewGuid().ToString(), $"{tag}-{n}"));
        }

        await _host.InvokeMessageAndWaitAsync(new StartCosmosDiagSaga(Guid.NewGuid().ToString(), "someone-else"));

        var result = await diagnostics.QuerySagasAsync(typeof(CosmosDiagSaga).FullName!,
            new DocumentQueryOptions(2, 3)
            {
                Where = "Note.StartsWith(@0)",
                Arguments = [tag],
                OrderBy = "Note desc"
            }, CancellationToken.None);

        result.TotalCount.ShouldBe(4);
        result.PageNumber.ShouldBe(2);
        result.PageSize.ShouldBe(3);
        result.Instances.Select(i => i.State.GetProperty("Note").GetString()).ShouldBe([$"{tag}-a"]);
    }

    [Fact]
    public async Task list_saga_instances_returns_instances_of_the_saga_type()
    {
        await _host.InvokeMessageAndWaitAsync(new StartCosmosDiagSaga(Guid.NewGuid().ToString(), "listed"));

        var instances = await diagnostics.ListSagaInstancesAsync(typeof(CosmosDiagSaga).FullName!, 5,
            CancellationToken.None);

        instances.ShouldNotBeEmpty();
        instances.ShouldAllBe(i => i.SagaTypeName == typeof(CosmosDiagSaga).FullName);
    }

    [Fact]
    public async Task query_sagas_refuses_a_where_that_does_not_parse()
    {
        await Should.ThrowAsync<DocumentCriteriaNotSupportedException>(() => diagnostics.QuerySagasAsync(
            typeof(CosmosDiagSaga).FullName!,
            new DocumentQueryOptions(1, 10) { Where = "NoSuchMember = 1" }, CancellationToken.None));
    }

    [Fact]
    public async Task query_sagas_refuses_criteria_that_mean_nothing_for_a_saga()
    {
        await Should.ThrowAsync<DocumentCriteriaNotSupportedException>(() => diagnostics.QuerySagasAsync(
            typeof(CosmosDiagSaga).FullName!,
            new DocumentQueryOptions(1, 10) { CorrelationId = "x" }, CancellationToken.None));
    }

    [Fact]
    public async Task query_sagas_clamps_the_page_size()
    {
        var result = await diagnostics.QuerySagasAsync(typeof(CosmosDiagSaga).FullName!,
            new DocumentQueryOptions(1, 100_000), CancellationToken.None);

        result.PageSize.ShouldBe(SagaQueryCriteria.MaxPageSize);
    }

    [Fact]
    public async Task unknown_saga_type_returns_null_and_empty()
    {
        (await diagnostics.ReadSagaAsync("Some.Unknown.Saga", "anything", CancellationToken.None)).ShouldBeNull();
        (await diagnostics.ListSagaInstancesAsync("Some.Unknown.Saga", 10, CancellationToken.None)).ShouldBeEmpty();
    }
}

public record StartCosmosDiagSaga(string Id, string Note);

public class CosmosDiagSaga : Saga
{
    public string Id { get; set; } = string.Empty;
    public string Note { get; set; } = string.Empty;

    public void Start(StartCosmosDiagSaga command)
    {
        Id = command.Id;
        Note = command.Note;
    }
}
