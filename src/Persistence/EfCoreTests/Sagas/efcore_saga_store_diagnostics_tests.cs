using IntegrationTests;
using JasperFx.Documents;
using JasperFx.Resources;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.ComplianceTests;
using Wolverine.ComplianceTests.Sagas;
using Wolverine.Configuration.Capabilities;
using Wolverine.EntityFrameworkCore;
using Wolverine.Persistence.Sagas;
using Wolverine.SqlServer;
using Wolverine.Tracking;
using Xunit;

namespace EfCoreTests.Sagas;

/// <summary>
/// Integration tests for the EF Core implementation of
/// <see cref="ISagaStoreDiagnostics"/>. Stands up a real DbContext +
/// SQL Server host so the reflection-based <c>DbContext.Set&lt;T&gt;</c>
/// /<c>FindAsync</c> dispatch and <c>Set&lt;T&gt;().Take(N)</c> path
/// are exercised against a live store. Those code paths can't be hit
/// by the in-memory aggregator tests in CoreTests because they need a
/// real <see cref="DbContext"/>.
/// </summary>
public class efcore_saga_store_diagnostics_tests : IAsyncLifetime
{
    private IHost _host = null!;

    public async ValueTask InitializeAsync()
    {
        _host = await WolverineHost.ForAsync(opts =>
        {
            // Pin discovery to one workflow saga the SagaDbContext
            // maps. The compliance fixtures share a WildcardStart
            // message across Guid/Int/Long/String workflows, so
            // including more than one trips the multiple-handlers-per-
            // saga validation. One Guid-keyed saga is enough to drive
            // every diagnostic path.
            opts.Discovery.DisableConventionalDiscovery()
                .IncludeType<GuidBasicWorkflow>();

            opts.PersistMessagesWithSqlServer(Servers.SqlServerConnectionString);

            opts.Services.AddDbContextWithWolverineIntegration<SagaDbContext>(
                x => x.UseSqlServer(Servers.SqlServerConnectionString));

            opts.UseEntityFrameworkCoreTransactions();
            opts.UseEntityFrameworkCoreWolverineManagedMigrations();
            opts.Services.AddResourceSetupOnStartup();

            opts.PublishAllMessages().Locally();
        });

        await _host.ResetResourceState();
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    [Fact]
    public async Task registered_saga_types_includes_efcore_owned_sagas()
    {
        var diagnostics = _host.GetRuntime().SagaStorage;
        var registered = await diagnostics.GetRegisteredSagasAsync(CancellationToken.None);

        var guidWorkflow = registered.SingleOrDefault(d => d.StateType.FullName == typeof(GuidBasicWorkflow).FullName);
        guidWorkflow.ShouldNotBeNull();
        guidWorkflow.StorageProvider.ShouldBe("EntityFrameworkCore");
        guidWorkflow.Messages
            .Where(m => m.Role == SagaRole.Start || m.Role == SagaRole.StartOrHandle)
            .Select(m => m.MessageType.FullName)
            .ShouldContain(typeof(GuidStart).FullName!);
    }

    [Fact]
    public async Task read_saga_returns_state_for_existing_instance()
    {
        var sagaId = Guid.NewGuid();
        await _host.InvokeMessageAndWaitAsync(new GuidStart { Id = sagaId, Name = "alpha" });

        var diagnostics = _host.GetRuntime().SagaStorage;
        var state = await diagnostics.ReadSagaAsync(typeof(GuidBasicWorkflow).FullName!, sagaId, CancellationToken.None);

        state.ShouldNotBeNull();
        state.IsCompleted.ShouldBeFalse();
        state.State.GetProperty("Name").GetString().ShouldBe("alpha");
    }

    [Fact]
    public async Task read_saga_returns_null_for_missing_instance()
    {
        var diagnostics = _host.GetRuntime().SagaStorage;
        var state = await diagnostics.ReadSagaAsync(
            typeof(GuidBasicWorkflow).FullName!, Guid.NewGuid(), CancellationToken.None);
        state.ShouldBeNull();
    }

    [Fact]
    public async Task list_saga_instances_returns_recent_sagas()
    {
        await _host.InvokeMessageAndWaitAsync(new GuidStart { Id = Guid.NewGuid(), Name = "one" });
        await _host.InvokeMessageAndWaitAsync(new GuidStart { Id = Guid.NewGuid(), Name = "two" });
        await _host.InvokeMessageAndWaitAsync(new GuidStart { Id = Guid.NewGuid(), Name = "three" });

        var diagnostics = _host.GetRuntime().SagaStorage;
        var instances = await diagnostics.ListSagaInstancesAsync(
            typeof(GuidBasicWorkflow).FullName!, 10, CancellationToken.None);

        instances.Count.ShouldBeGreaterThanOrEqualTo(3);
        instances.ShouldAllBe(i => i.SagaTypeName == typeof(GuidBasicWorkflow).FullName);
    }

    [Fact]
    public async Task query_sagas_filters_orders_pages_and_counts()
    {
        var tag = Guid.NewGuid().ToString("N");
        foreach (var n in new[] { "b", "c", "a", "d" })
        {
            await _host.InvokeMessageAndWaitAsync(new GuidStart { Id = Guid.NewGuid(), Name = $"{tag}-{n}" });
        }

        await _host.InvokeMessageAndWaitAsync(new GuidStart { Id = Guid.NewGuid(), Name = "someone-else" });

        var diagnostics = _host.GetRuntime().SagaStorage;
        diagnostics.GetQueryCapabilities(typeof(GuidBasicWorkflow).FullName!).CanFilter.ShouldBeTrue();

        var result = await diagnostics.QuerySagasAsync(typeof(GuidBasicWorkflow).FullName!,
            new DocumentQueryOptions(2, 3)
            {
                Where = "Name.StartsWith(@0)",
                Arguments = [tag],
                OrderBy = "Name desc"
            }, CancellationToken.None);

        result.TotalCount.ShouldBe(4);
        result.Instances.Select(i => i.State.GetProperty("Name").GetString()).ShouldBe([$"{tag}-a"]);
    }

    [Fact]
    public async Task query_sagas_refuses_a_where_that_does_not_parse()
    {
        var diagnostics = _host.GetRuntime().SagaStorage;
        await Should.ThrowAsync<DocumentCriteriaNotSupportedException>(() => diagnostics.QuerySagasAsync(
            typeof(GuidBasicWorkflow).FullName!,
            new DocumentQueryOptions(1, 10) { Where = "NoSuchMember = 1" }, CancellationToken.None));
    }

    [Fact]
    public async Task list_saga_instances_is_ordered_by_identity()
    {
        for (var i = 0; i < 4; i++)
        {
            await _host.InvokeMessageAndWaitAsync(new GuidStart { Id = Guid.NewGuid(), Name = "ordered" });
        }

        var diagnostics = _host.GetRuntime().SagaStorage;
        var instances = await diagnostics.ListSagaInstancesAsync(
            typeof(GuidBasicWorkflow).FullName!, 50, CancellationToken.None);

        var ids = instances.Select(i => (Guid)i.Identity).ToList();
        // SQL Server compares a uniqueidentifier by its own byte order, which SqlGuid reproduces
        ids.ShouldBe(ids.OrderBy(x => new System.Data.SqlTypes.SqlGuid(x)).ToList());
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
