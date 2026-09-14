using IntegrationTests;
using JasperFx;
using JasperFx.CodeGeneration;
using JasperFx.Events.Aggregation;
using JasperFx.Events.Projections;
using JasperFx.Resources;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Polecat;
using Shouldly;
using Wolverine;
using Wolverine.Polecat;
using Wolverine.Tracking;

namespace PolecatTests.AncillaryStores;

/// <summary>
///     GH-4439, the Polecat mirror of <c>MartenTests.AncillaryStores.natural_key_aggregate_on_ancillary_store</c>.
///     An aggregate identified by a <see cref="NaturalKeyAttribute" /> and registered ONLY on an ancillary
///     store has to resolve that natural key through the store the chain is routed to.
/// </summary>
/// <remarks>
///     Polecat had the identical bug for the identical reason: its
///     <c>TryDetermineNaturalKeyType</c> read the default <c>StoreOptions</c> out of the container, and
///     <c>PolecatProjectionOptions.FindNaturalKeyDefinition</c> searches only that store's registered
///     projections. Neither store's natural-key support was at fault — only which store was asked.
/// </remarks>
public class natural_key_aggregate_on_ancillary_store : IAsyncLifetime
{
    private IHost theHost = null!;

    public async ValueTask InitializeAsync()
    {
        theHost = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.MessageStorageSchemaName = "wolverine";
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.Policies.AutoApplyTransactions();

                // The main store knows NOTHING about the aggregate -- if natural-key detection asks this
                // store, it finds nothing and the workflow gives up.
                opts.Services.AddPolecat(m =>
                    {
                        m.ConnectionString = Servers.SqlServerConnectionString;
                        m.DatabaseSchemaName = "pc_nk_anc_main";
                    })
                    .UseLightweightSessions()
                    .IntegrateWithWolverine();

                opts.Services.AddPolecatStore<IAncillaryNkOrderStore>(m =>
                    {
                        m.Connection(Servers.SqlServerConnectionString);
                        m.DatabaseSchemaName = "pc_nk_anc_orders";
                        m.Projections.Snapshot<AncillaryPcNkOrder>(SnapshotLifecycle.Inline);
                    })
                    .IntegrateWithWolverine();

                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType(typeof(AncillaryPcNkOrderHandler));

                opts.Services.AddResourceSetupOnStartup(StartupAction.ResetState);
                opts.CodeGeneration.TypeLoadMode = TypeLoadMode.Auto;
            }).StartAsync();

        // Both tests claim a hardcoded natural key, and a natural key identifies exactly ONE stream.
        // StartupAction.ResetState does NOT clear this ancillary store's natural-key lookup table, so
        // without an explicit clean the fixture passes against a fresh database and then fails every rerun
        // with DuplicateNaturalKeyException -- the guard firing correctly on a mapping the previous run left
        // behind. Invisible in CI, which gets a fresh database, and thoroughly misleading locally, where it
        // reads as a regression in the natural-key workflow itself. Same reasoning as the main-store fixture
        // in PolecatTests/natural_key_aggregate_handler_workflow.cs.
        var store = theHost.Services.GetRequiredService<IAncillaryNkOrderStore>();
        await store.Advanced.CleanAllEventDataAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await theHost.StopAsync();
        theHost.Dispose();
    }

    [Fact]
    public async Task write_aggregate_resolves_the_natural_key_through_the_ancillary_store()
    {
        var streamId = Guid.NewGuid();
        var orderNumber = new AncillaryPcOrderNumber("ORD-PCANC-001");

        var store = theHost.Services.GetRequiredService<IAncillaryNkOrderStore>();
        await using (var session = store.LightweightSession())
        {
            session.Events.StartStream(streamId, new AncillaryPcNkOrderCreated(orderNumber));
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await theHost.TrackActivity()
            .SendMessageAndWaitAsync(new AddAncillaryPcNkItem(orderNumber, 12.50m));

        await using var verify = store.LightweightSession();
        var order = await verify.LoadAsync<AncillaryPcNkOrder>(streamId, TestContext.Current.CancellationToken);

        order.ShouldNotBeNull();
        order.TotalAmount.ShouldBe(12.50m);
    }

    [Fact]
    public async Task write_model_resolves_the_natural_key_through_the_ancillary_store()
    {
        var streamId = Guid.NewGuid();
        var orderNumber = new AncillaryPcOrderNumber("ORD-PCANC-002");

        var store = theHost.Services.GetRequiredService<IAncillaryNkOrderStore>();
        await using (var session = store.LightweightSession())
        {
            session.Events.StartStream(streamId, new AncillaryPcNkOrderCreated(orderNumber));
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await theHost.TrackActivity()
            .SendMessageAndWaitAsync(new CompleteAncillaryPcNkOrder(orderNumber));

        await using var verify = store.LightweightSession();
        var order = await verify.LoadAsync<AncillaryPcNkOrder>(streamId, TestContext.Current.CancellationToken);

        order.ShouldNotBeNull();
        order.IsComplete.ShouldBeTrue();
    }
}

public interface IAncillaryNkOrderStore : IDocumentStore;

public record AncillaryPcOrderNumber(string Value);

public class AncillaryPcNkOrder
{
    public Guid Id { get; set; }

    [NaturalKey]
    public AncillaryPcOrderNumber OrderNum { get; set; } = null!;

    public decimal TotalAmount { get; set; }
    public bool IsComplete { get; set; }

    [NaturalKeySource]
    public void Apply(AncillaryPcNkOrderCreated e)
    {
        OrderNum = e.OrderNumber;
    }

    public void Apply(AncillaryPcNkItemAdded e)
    {
        TotalAmount += e.Price;
    }

    public void Apply(AncillaryPcNkOrderCompleted e)
    {
        IsComplete = true;
    }
}

public record AncillaryPcNkOrderCreated(AncillaryPcOrderNumber OrderNumber);
public record AncillaryPcNkItemAdded(decimal Price);
public record AncillaryPcNkOrderCompleted;

public record AddAncillaryPcNkItem(AncillaryPcOrderNumber OrderNum, decimal Price);
public record CompleteAncillaryPcNkOrder(AncillaryPcOrderNumber OrderNum);

[PolecatStore(typeof(IAncillaryNkOrderStore))]
public static class AncillaryPcNkOrderHandler
{
    public static AncillaryPcNkItemAdded Handle(AddAncillaryPcNkItem command,
        [WriteAggregate] AncillaryPcNkOrder order)
    {
        return new AncillaryPcNkItemAdded(command.Price);
    }

    // The store-agnostic spelling of the same thing, which resolves its provider through the registered
    // persistence strategies rather than naming Polecat.
    public static AncillaryPcNkOrderCompleted Handle(CompleteAncillaryPcNkOrder command,
        [Wolverine.Persistence.EventSourcing.WriteModel] AncillaryPcNkOrder order)
    {
        return new AncillaryPcNkOrderCompleted();
    }
}
