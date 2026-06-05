using IntegrationTests;
using JasperFx;
using JasperFx.Events;
using JasperFx.MultiTenancy;
using JasperFx.Resources;
using Marten;
using Marten.Events;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Marten;
using Wolverine.Tracking;
using Xunit;

namespace MartenTests.AncillaryStores;

// Phase 1 of #3021 (ancillary stores slice): an ANCILLARY Marten store (AddMartenStore<T>) configured
// with Conjoined + Quick + UseTenantPartitionedEvents. A handler tagged [MartenStore(typeof(IPartTenantStore))]
// must get an ancillary session scoped to the invocation's tenant, so its append lands in that tenant's
// partition of the ancillary store and stays isolated from other tenants.
public class tenant_partitioned_ancillary_store : PostgresqlContext, IAsyncLifetime
{
    private IHost theHost = null!;
    private IPartTenantStore theStore = null!;
    private readonly string theMain = "anc_main_" + Guid.NewGuid().ToString("N");
    private readonly string theThings = "anc_things_" + Guid.NewGuid().ToString("N");

    public async Task InitializeAsync()
    {
        theHost = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.Durability.MessageStorageSchemaName = "wolverine";
                opts.Policies.AutoApplyTransactions();

                // Main store (required for the modular-monolith ancillary setup).
                opts.Services.AddMarten(m =>
                {
                    m.Connection(Servers.PostgresConnectionString);
                    m.DatabaseSchemaName = theMain;
                    m.DisableNpgsqlLogging = true;
                }).IntegrateWithWolverine();

                // Ancillary store with per-tenant event partitioning.
                opts.Services.AddMartenStore<IPartTenantStore>(m =>
                {
                    m.Connection(Servers.PostgresConnectionString);
                    m.DatabaseSchemaName = theThings;
                    m.DisableNpgsqlLogging = true;

                    m.Events.StreamIdentity = StreamIdentity.AsString;
                    m.Events.TenancyStyle = TenancyStyle.Conjoined;
                    m.Events.AppendMode = EventAppendMode.Quick;
                    m.Events.UseTenantPartitionedEvents = true;
                }).IntegrateWithWolverine();

                opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(PartTenantHandler));
                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync();

        theStore = theHost.Services.GetRequiredService<IPartTenantStore>();
        await theStore.Advanced.AddMartenManagedTenantsAsync(default, new Dictionary<string, string>
        {
            ["tenant1"] = "tenant1",
            ["tenant2"] = "tenant2",
            [StorageConstants.DefaultTenantId] = "default"
        });
    }

    public async Task DisposeAsync()
    {
        await theHost.StopAsync();
        theHost.Dispose();
    }

    [Fact]
    public async Task ancillary_store_append_lands_in_the_routed_tenant_partition()
    {
        var id = "thing-" + Guid.NewGuid().ToString("N");
        await theHost.MessageBus().InvokeForTenantAsync("tenant1", new RecordPartThing(id, 3));

        await using (var s1 = theStore.LightweightSession("tenant1"))
        {
            (await s1.Events.FetchStreamAsync(id)).Count.ShouldBe(1);
        }

        // Isolated: the same stream id is absent from tenant2's partition of the ancillary store.
        await using (var s2 = theStore.LightweightSession("tenant2"))
        {
            (await s2.Events.FetchStreamAsync(id)).Count.ShouldBe(0);
        }
    }
}

public interface IPartTenantStore : IDocumentStore;

public record RecordPartThing(string Id, int Amount);

public static class PartTenantHandler
{
    // [MartenStore] redirects the handler's session to the ancillary store; under tenant routing the
    // session must be scoped to the invocation tenant. AutoApplyTransactions commits it.
    [MartenStore(typeof(IPartTenantStore))]
    public static void Handle(RecordPartThing command, IDocumentSession session)
        => session.Events.StartStream(command.Id, new PartThingRecorded(command.Amount));
}

public record PartThingRecorded(int Amount);
