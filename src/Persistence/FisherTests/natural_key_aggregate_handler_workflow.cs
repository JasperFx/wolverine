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
///     The aggregate handler workflow driven by a <see cref="NaturalKeyAttribute" /> rather than a stream id,
///     against Fisher's primary store — the Fisher twin of the Marten and Polecat suites of the same name.
/// </summary>
/// <remarks>
///     GH-4439. Fisher had <b>no</b> natural-key coverage in Wolverine at all, and
///     <c>FisherEventSourcingFrameProvider</c> declared that Fisher has no natural-key concept, so
///     <c>TryDetermineNaturalKeyType</c> returned null and this path was never once exercised — even though
///     <c>Wolverine.Fisher.Codegen.LoadAggregateFrame</c> has carried an <c>IsNaturalKey</c> branch the whole
///     time. This test is what proves that branch's generated
///     <c>session.Events.FetchForWriting&lt;T, TKey&gt;(key, token)</c> actually binds to Fisher's natural-key
///     path: Fisher's own overload routes to <c>FetchForWritingByNaturalKey</c> when the aggregate declares a
///     key, which is why core's single spelling works across all three stores.
/// </remarks>
public class natural_key_aggregate_handler_workflow : IAsyncLifetime
{
    private FisherTestDatabase theDatabase = null!;
    private IHost theHost = null!;

    public async ValueTask InitializeAsync()
    {
        theDatabase = Servers.CreateDatabase("nk_handler");

        theHost = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType(typeof(FiNkOrderHandler));

                opts.Durability.Mode = DurabilityMode.Solo;
                opts.Policies.AutoApplyTransactions();

                opts.Services.AddFisher(m =>
                    {
                        m.Connection(theDatabase.ConnectionString);
                        m.AutoCreateSchemaObjects = AutoCreate.All;
                        m.Projections.Snapshot<FiNkOrder>(SnapshotLifecycle.Inline);
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
        theDatabase.Dispose();
    }

    [Fact]
    public async Task write_aggregate_by_natural_key()
    {
        var streamId = Guid.NewGuid();
        var orderNumber = new FiOrderNumber("ORD-FI-001");

        var store = theHost.Services.GetRequiredService<IDocumentStore>();
        await using (var session = store.LightweightSession())
        {
            session.Events.StartStream<FiNkOrder>(streamId, new FiNkOrderCreated(orderNumber));
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await theHost.TrackActivity()
            .SendMessageAndWaitAsync(new AddFiNkItem(orderNumber, 12.50m));

        await using var verify = store.LightweightSession();
        var order = await verify.LoadAsync<FiNkOrder>(streamId, TestContext.Current.CancellationToken);

        order.ShouldNotBeNull();
        order.TotalAmount.ShouldBe(12.50m);
    }

    [Fact]
    public async Task write_model_by_natural_key()
    {
        var streamId = Guid.NewGuid();
        var orderNumber = new FiOrderNumber("ORD-FI-002");

        var store = theHost.Services.GetRequiredService<IDocumentStore>();
        await using (var session = store.LightweightSession())
        {
            session.Events.StartStream<FiNkOrder>(streamId, new FiNkOrderCreated(orderNumber));
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await theHost.TrackActivity()
            .SendMessageAndWaitAsync(new CompleteFiNkOrder(orderNumber));

        await using var verify = store.LightweightSession();
        var order = await verify.LoadAsync<FiNkOrder>(streamId, TestContext.Current.CancellationToken);

        order.ShouldNotBeNull();
        order.IsComplete.ShouldBeTrue();
    }
}

public record FiOrderNumber(string Value);

public class FiNkOrder
{
    public Guid Id { get; set; }

    [NaturalKey]
    public FiOrderNumber OrderNum { get; set; } = null!;

    public decimal TotalAmount { get; set; }
    public bool IsComplete { get; set; }

    [NaturalKeySource]
    public void Apply(FiNkOrderCreated e)
    {
        OrderNum = e.OrderNumber;
    }

    public void Apply(FiNkItemAdded e)
    {
        TotalAmount += e.Price;
    }

    public void Apply(FiNkOrderCompleted e)
    {
        IsComplete = true;
    }
}

public record FiNkOrderCreated(FiOrderNumber OrderNumber);
public record FiNkItemAdded(decimal Price);
public record FiNkOrderCompleted;

public record AddFiNkItem(FiOrderNumber OrderNum, decimal Price);
public record CompleteFiNkOrder(FiOrderNumber OrderNum);

public static class FiNkOrderHandler
{
    public static FiNkItemAdded Handle(AddFiNkItem command, [WriteAggregate] FiNkOrder order)
    {
        return new FiNkItemAdded(command.Price);
    }

    // The store-agnostic spelling of the same thing.
    public static FiNkOrderCompleted Handle(CompleteFiNkOrder command,
        [Wolverine.Persistence.EventSourcing.WriteModel] FiNkOrder order)
    {
        return new FiNkOrderCompleted();
    }
}
