using JasperFx;
using JasperFx.Events;
using JasperFx.Events.Projections;
using Marten;
using Marten.Exceptions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Xunit;

namespace MartenTests.AggregateHandlerWorkflow;

// GH-3021 investigation (Phase 1): appending events for a tenant that was NEVER registered via
// AddMartenManagedTenantsAsync, under Events.UseTenantPartitionedEvents.
//
// Conclusion: this is NOT a silent misroute. Marten's managed-partition trigger raises MT002
// ("Tenant 'x' has no registered partition") and Wolverine propagates it as a MartenCommandException;
// nothing is written to any partition.
//
// The original "did not surface MT002 — possible silent misroute" observation was a downstream
// symptom of GH-3025: the foundational harness's StartTally handler returns a single MartenOps.StartStream,
// which pre-3025 was silently dropped (no SaveChangesAsync), so the append never reached Postgres and
// MT002 never fired. With GH-3025 fixed (6.4.4) the append executes and MT002 surfaces correctly. This
// fixture pins that behavior so a regression in either the single-IMartenOp persistence (3025) or the
// managed-partition guard would fail loudly.
public class tenant_partitioned_unregistered_tenant : PostgresqlContext, IAsyncLifetime
{
    private IHost theHost = null!;
    private IDocumentStore theStore = null!;

    public async Task InitializeAsync()
    {
        // PartitionedTenancyHost registers tenant1 / tenant2 / *DEFAULT* only — "ghost" is never registered.
        theHost = await PartitionedTenancyHost.StartAsync(StreamIdentity.AsString,
            "tpe_unreg_" + Guid.NewGuid().ToString("N"),
            m =>
            {
                m.Schema.For<TenantTally>().MultiTenanted();
                m.Projections.Snapshot<TenantTally>(SnapshotLifecycle.Inline);
            },
            typeof(TenantTallyHandler));

        theStore = theHost.Services.GetRequiredService<IDocumentStore>();
    }

    public async Task DisposeAsync()
    {
        await theHost.StopAsync();
        theHost.Dispose();
    }

    [Fact]
    public async Task append_for_an_unregistered_tenant_raises_MT002_and_persists_nothing()
    {
        var id = "ghost-" + Guid.NewGuid().ToString("N");

        // The StartTally handler returns a single MartenOps.StartStream (the GH-3025 path). Invoked for
        // an unregistered tenant it must fail loudly at SaveChanges, not silently no-op or misroute.
        var ex = await Should.ThrowAsync<MartenCommandException>(() =>
            theHost.MessageBus().InvokeForTenantAsync("ghost", new StartTally(id)));

        // The friendly managed-partition guard, not a raw row-routing CHECK violation.
        ex.ToString().ShouldContain("MT002");
        ex.ToString().ShouldContain("has no registered partition");

        // And nothing leaked into any partition — neither the unregistered tenant, the default, nor a
        // registered sibling.
        (await Loaded("ghost", id)).ShouldBeNull();
        (await Loaded(StorageConstants.DefaultTenantId, id)).ShouldBeNull();
        (await Loaded("tenant1", id)).ShouldBeNull();
    }

    private async Task<TenantTally?> Loaded(string tenant, string id)
    {
        await using var session = theStore.LightweightSession(tenant);
        return await session.LoadAsync<TenantTally>(id);
    }
}
