using Fisher;
using JasperFx;
using JasperFx.CodeGeneration;
using JasperFx.Events.Aggregation;
using JasperFx.Events.Projections;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Fisher;
using Wolverine.Tracking;

namespace FisherTests;

/// <summary>
///     GH-4439, the Fisher mirror of the Marten and Polecat suites of the same name: an aggregate identified
///     by a <see cref="NaturalKeyAttribute" /> and registered ONLY on an ancillary store must resolve that
///     key through the store the chain is routed to.
/// </summary>
/// <remarks>
///     Each store is its own SQLite <b>file</b>, which is also what makes the negative assertion here cheap
///     and unambiguous — the main store is a different file entirely, so a fetch that fell back to the
///     default store could not accidentally find the stream.
/// </remarks>
public class natural_key_aggregate_on_ancillary_store : IAsyncLifetime
{
    private FisherTestDatabase theAncillaryDatabase = null!;
    private FisherTestDatabase theMainDatabase = null!;
    private IHost theHost = null!;

    public async ValueTask InitializeAsync()
    {
        theMainDatabase = Servers.CreateDatabase("nk_anc_main");
        theAncillaryDatabase = Servers.CreateDatabase("nk_anc_orders");

        theHost = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType(typeof(AncillaryFiNkOrderHandler));

                opts.Durability.Mode = DurabilityMode.Solo;
                opts.Policies.AutoApplyTransactions();

                // The main store knows NOTHING about the aggregate -- if natural-key detection asks this
                // store, it finds nothing and the workflow gives up.
                opts.Services.AddFisher(m =>
                    {
                        m.Connection(theMainDatabase.ConnectionString);
                        m.AutoCreateSchemaObjects = AutoCreate.All;
                    })
                    .ApplyAllDatabaseChangesOnStartup()
                    .IntegrateWithWolverine();

                opts.Services.AddFisherStore<IAncillaryFiNkOrderStore>(m =>
                    {
                        m.Connection(theAncillaryDatabase.ConnectionString);
                        m.AutoCreateSchemaObjects = AutoCreate.All;
                        m.Projections.Snapshot<AncillaryFiNkOrder>(SnapshotLifecycle.Inline);
                    })
                    .ApplyAllDatabaseChangesOnStartup()
                    .IntegrateWithWolverine();

                opts.CodeGeneration.TypeLoadMode = TypeLoadMode.Auto;
            }).StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await theHost.StopAsync();
        theHost.Dispose();
        theMainDatabase.Dispose();
        theAncillaryDatabase.Dispose();
    }

    [Fact]
    public async Task write_aggregate_resolves_the_natural_key_through_the_ancillary_store()
    {
        var streamId = Guid.NewGuid();
        var orderNumber = new AncillaryFiOrderNumber("ORD-FIANC-001");

        var store = theHost.Services.GetRequiredService<IAncillaryFiNkOrderStore>();
        await using (var session = store.LightweightSession())
        {
            session.Events.StartStream<AncillaryFiNkOrder>(streamId,
                new AncillaryFiNkOrderCreated(orderNumber));
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await theHost.TrackActivity()
            .SendMessageAndWaitAsync(new AddAncillaryFiNkItem(orderNumber, 12.50m));

        await using var verify = store.LightweightSession();
        var order = await verify.LoadAsync<AncillaryFiNkOrder>(streamId,
            TestContext.Current.CancellationToken);

        order.ShouldNotBeNull();
        order.TotalAmount.ShouldBe(12.50m);
    }

    [Fact]
    public async Task write_model_resolves_the_natural_key_through_the_ancillary_store()
    {
        var streamId = Guid.NewGuid();
        var orderNumber = new AncillaryFiOrderNumber("ORD-FIANC-002");

        var store = theHost.Services.GetRequiredService<IAncillaryFiNkOrderStore>();
        await using (var session = store.LightweightSession())
        {
            session.Events.StartStream<AncillaryFiNkOrder>(streamId,
                new AncillaryFiNkOrderCreated(orderNumber));
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await theHost.TrackActivity()
            .SendMessageAndWaitAsync(new CompleteAncillaryFiNkOrder(orderNumber));

        await using var verify = store.LightweightSession();
        var order = await verify.LoadAsync<AncillaryFiNkOrder>(streamId,
            TestContext.Current.CancellationToken);

        order.ShouldNotBeNull();
        order.IsComplete.ShouldBeTrue();
    }
}

public interface IAncillaryFiNkOrderStore : IDocumentStore;

public record AncillaryFiOrderNumber(string Value);

public class AncillaryFiNkOrder
{
    public Guid Id { get; set; }

    [NaturalKey]
    public AncillaryFiOrderNumber OrderNum { get; set; } = null!;

    public decimal TotalAmount { get; set; }
    public bool IsComplete { get; set; }

    [NaturalKeySource]
    public void Apply(AncillaryFiNkOrderCreated e)
    {
        OrderNum = e.OrderNumber;
    }

    public void Apply(AncillaryFiNkItemAdded e)
    {
        TotalAmount += e.Price;
    }

    public void Apply(AncillaryFiNkOrderCompleted e)
    {
        IsComplete = true;
    }
}

public record AncillaryFiNkOrderCreated(AncillaryFiOrderNumber OrderNumber);
public record AncillaryFiNkItemAdded(decimal Price);
public record AncillaryFiNkOrderCompleted;

public record AddAncillaryFiNkItem(AncillaryFiOrderNumber OrderNum, decimal Price);
public record CompleteAncillaryFiNkOrder(AncillaryFiOrderNumber OrderNum);

[FisherStore(typeof(IAncillaryFiNkOrderStore))]
public static class AncillaryFiNkOrderHandler
{
    public static AncillaryFiNkItemAdded Handle(AddAncillaryFiNkItem command,
        [WriteAggregate] AncillaryFiNkOrder order)
    {
        return new AncillaryFiNkItemAdded(command.Price);
    }

    // The store-agnostic spelling of the same thing, which resolves its provider through the registered
    // persistence strategies rather than naming Fisher.
    public static AncillaryFiNkOrderCompleted Handle(CompleteAncillaryFiNkOrder command,
        [Wolverine.Persistence.EventSourcing.WriteModel] AncillaryFiNkOrder order)
    {
        return new AncillaryFiNkOrderCompleted();
    }
}
