using IntegrationTests;
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

    public async Task InitializeAsync()
    {
        _host = WolverineHost.For(opts =>
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

    public async Task DisposeAsync()
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
    public async Task unknown_saga_type_returns_null_and_empty()
    {
        var diagnostics = _host.GetRuntime().SagaStorage;
        var read = await diagnostics.ReadSagaAsync("Some.Unknown.Saga", Guid.NewGuid(), CancellationToken.None);
        var list = await diagnostics.ListSagaInstancesAsync("Some.Unknown.Saga", 10, CancellationToken.None);

        read.ShouldBeNull();
        list.ShouldBeEmpty();
    }
}
