using IntegrationTests;
using JasperFx;
using JasperFx.Documents;
using JasperFx.Resources;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Configuration.Capabilities;
using Wolverine.Marten;
using Wolverine.Persistence.Sagas;
using Wolverine.Tracking;
using Xunit;

namespace MartenTests.Saga;

/// <summary>
/// Integration tests for the Marten implementation of
/// <see cref="ISagaStoreDiagnostics"/>. Stands up a real Marten host so
/// the reflection-based <c>LoadAsync</c> dispatch and
/// <c>Query&lt;TSaga&gt;()</c> path are exercised against a live
/// document store — those code paths can't be exercised by the
/// in-memory aggregator tests in CoreTests because they need a real
/// <c>IDocumentStore</c>.
/// </summary>
public class marten_saga_store_diagnostics_tests : PostgresqlContext, IAsyncLifetime
{
    private IHost _host = null!;

    public async ValueTask InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.Discovery.DisableConventionalDiscovery().IncludeType<DiagSaga>();

                opts.Services.AddMarten(x =>
                {
                    x.Connection(Servers.PostgresConnectionString);
                    x.DatabaseSchemaName = "saga_diag";
                    x.AutoCreateSchemaObjects = AutoCreate.All;
                }).IntegrateWithWolverine();

                opts.Services.AddResourceSetupOnStartup();

                opts.PublishAllMessages().Locally();
            })
            .StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    [Fact]
    public async Task registered_saga_types_includes_marten_owned_saga()
    {
        var diagnostics = _host.GetRuntime().SagaStorage;
        var registered = await diagnostics.GetRegisteredSagasAsync(CancellationToken.None);

        var diag = registered.SingleOrDefault(d => d.StateType.FullName == typeof(DiagSaga).FullName);
        diag.ShouldNotBeNull();
        diag.StorageProvider.ShouldBe("Marten");
        diag.Messages
            .Where(m => m.Role == SagaRole.Start || m.Role == SagaRole.StartOrHandle)
            .Select(m => m.MessageType.FullName)
            .ShouldContain(typeof(StartDiagSaga).FullName!);
    }

    [Fact]
    public async Task read_saga_returns_state_for_existing_instance()
    {
        var sagaId = Guid.NewGuid();
        await _host.InvokeMessageAndWaitAsync(new StartDiagSaga(sagaId, "alpha"));

        var diagnostics = _host.GetRuntime().SagaStorage;
        var state = await diagnostics.ReadSagaAsync(typeof(DiagSaga).FullName!, sagaId, CancellationToken.None);

        state.ShouldNotBeNull();
        state.IsCompleted.ShouldBeFalse();
        state.State.GetProperty("Note").GetString().ShouldBe("alpha");
    }

    [Fact]
    public async Task read_saga_returns_null_for_missing_instance()
    {
        var diagnostics = _host.GetRuntime().SagaStorage;
        var state = await diagnostics.ReadSagaAsync(typeof(DiagSaga).FullName!, Guid.NewGuid(), CancellationToken.None);
        state.ShouldBeNull();
    }

    [Fact]
    public async Task list_saga_instances_returns_recent_sagas()
    {
        // Three sagas in flight — the diagnostic peek should surface
        // all three (or at least the requested top-N).
        await _host.InvokeMessageAndWaitAsync(new StartDiagSaga(Guid.NewGuid(), "one"));
        await _host.InvokeMessageAndWaitAsync(new StartDiagSaga(Guid.NewGuid(), "two"));
        await _host.InvokeMessageAndWaitAsync(new StartDiagSaga(Guid.NewGuid(), "three"));

        var diagnostics = _host.GetRuntime().SagaStorage;
        var instances = await diagnostics.ListSagaInstancesAsync(typeof(DiagSaga).FullName!, 10, CancellationToken.None);

        instances.Count.ShouldBeGreaterThanOrEqualTo(3);
        instances.ShouldAllBe(i => i.SagaTypeName == typeof(DiagSaga).FullName);
    }

    [Fact]
    public void marten_can_filter_and_order_sagas()
    {
        var capabilities = _host.GetRuntime().SagaStorage.GetQueryCapabilities(typeof(DiagSaga).FullName!);

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
            await _host.InvokeMessageAndWaitAsync(new StartDiagSaga(Guid.NewGuid(), $"{tag}-{n}"));
        }

        await _host.InvokeMessageAndWaitAsync(new StartDiagSaga(Guid.NewGuid(), "someone-else"));

        var diagnostics = _host.GetRuntime().SagaStorage;
        var result = await diagnostics.QuerySagasAsync(typeof(DiagSaga).FullName!,
            new DocumentQueryOptions(2, 3)
            {
                Where = "Note.StartsWith(@0)",
                Arguments = [tag],
                OrderBy = "Note desc"
            }, CancellationToken.None);

        result.TotalCount.ShouldBe(4);
        result.PageNumber.ShouldBe(2);
        result.PageSize.ShouldBe(3);
        result.Instances.Select(i => i.State.GetProperty("Note").GetString())
            .ShouldBe([$"{tag}-a"]);
    }

    [Fact]
    public async Task query_sagas_refuses_a_where_that_does_not_parse()
    {
        var diagnostics = _host.GetRuntime().SagaStorage;
        await Should.ThrowAsync<DocumentCriteriaNotSupportedException>(() => diagnostics.QuerySagasAsync(
            typeof(DiagSaga).FullName!,
            new DocumentQueryOptions(1, 10) { Where = "NoSuchMember = 1" }, CancellationToken.None));
    }

    [Fact]
    public async Task query_sagas_refuses_criteria_that_mean_nothing_for_a_saga()
    {
        var diagnostics = _host.GetRuntime().SagaStorage;
        await Should.ThrowAsync<DocumentCriteriaNotSupportedException>(() => diagnostics.QuerySagasAsync(
            typeof(DiagSaga).FullName!,
            new DocumentQueryOptions(1, 10) { CorrelationId = "x" }, CancellationToken.None));
    }

    [Fact]
    public async Task query_sagas_clamps_the_page_size()
    {
        var diagnostics = _host.GetRuntime().SagaStorage;
        var result = await diagnostics.QuerySagasAsync(typeof(DiagSaga).FullName!,
            new DocumentQueryOptions(1, 100_000), CancellationToken.None);

        result.PageSize.ShouldBe(SagaQueryCriteria.MaxPageSize);
    }

    [Fact]
    public async Task list_saga_instances_is_ordered_by_identity()
    {
        for (var i = 0; i < 4; i++)
        {
            await _host.InvokeMessageAndWaitAsync(new StartDiagSaga(Guid.NewGuid(), "ordered"));
        }

        var diagnostics = _host.GetRuntime().SagaStorage;
        var instances = await diagnostics.ListSagaInstancesAsync(typeof(DiagSaga).FullName!, 50, CancellationToken.None);

        var ids = instances.Select(i => (Guid)i.Identity).ToList();
        ids.ShouldBe(ids.OrderBy(x => x).ToList());
    }

    [Fact]
    public async Task unknown_saga_type_returns_null_and_empty()
    {
        var diagnostics = _host.GetRuntime().SagaStorage;
        var read = await diagnostics.ReadSagaAsync("Some.Unknown.Saga", Guid.NewGuid(), CancellationToken.None);
        var list = await diagnostics.ListSagaInstancesAsync("Some.Unknown.Saga", 10, CancellationToken.None);

        read.ShouldBeNull();
        list.ShouldBeEmpty();
    }
}

public record StartDiagSaga(Guid Id, string Note);

public class DiagSaga : Wolverine.Saga
{
    public Guid Id { get; set; }
    public string Note { get; set; } = "";

    public void Start(StartDiagSaga cmd)
    {
        Id = cmd.Id;
        Note = cmd.Note;
    }
}
