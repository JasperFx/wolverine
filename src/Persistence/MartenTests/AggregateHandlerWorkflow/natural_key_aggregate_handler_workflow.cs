using IntegrationTests;
using JasperFx;
using JasperFx.CodeGeneration;
using JasperFx.Events.Aggregation;
using JasperFx.Resources;
using Marten;
using JasperFx.Events;
using Marten.Events;
using JasperFx.Events.Projections;
using Marten.Events.Projections;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.ComplianceTests;
using Wolverine.Marten;
using Wolverine.Tracking;

namespace MartenTests.AggregateHandlerWorkflow;

public class natural_key_aggregate_handler_workflow : PostgresqlContext, IAsyncLifetime
{
    private IHost _host = null!;
    private IDocumentStore _store = null!;

    public async ValueTask InitializeAsync()
    {
        _host = await WolverineHost.ForAsync(opts =>
        {
            opts.Services.AddMarten(m =>
                {
                    m.Connection(Servers.PostgresConnectionString);
                    m.DatabaseSchemaName = "nk_handler";
                    m.Projections.Snapshot<NkOrderAggregate>(SnapshotLifecycle.Inline);
                })
                .UseLightweightSessions()
                .IntegrateWithWolverine();

            opts.Discovery.DisableConventionalDiscovery()
                .IncludeType(typeof(NkOrderHandler))
                .IncludeType(typeof(NkOrderExclusiveHandler));
            opts.Durability.Mode = DurabilityMode.Solo;

            opts.Services.AddResourceSetupOnStartup();
            opts.CodeGeneration.TypeLoadMode = TypeLoadMode.Auto;
        });

        _store = _host.Services.GetRequiredService<IDocumentStore>();

        // Every test below claims a hardcoded natural key. A natural key identifies ONE stream, so
        // without this the fixture passes exactly once against a given database and every rerun fails
        // with DuplicateNaturalKeyException -- the guard firing correctly on a mapping the previous run
        // left behind. Invisible in CI, which gets a fresh database, and thoroughly misleading locally:
        // it reads as a regression in the natural-key workflow itself.
        //
        // DeleteAllEventDataAsync clears the mapping table too: Marten's NaturalKeyTable carries a real
        // foreign key to mt_streams, so truncating streams cascades into it.
        await _store.Advanced.Clean.DeleteAllEventDataAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    [Fact]
    public async Task handle_command_with_natural_key_returning_single_event()
    {
        var streamId = Guid.NewGuid();
        var orderNumber = new NkHandlerOrderNumber("ORD-WOL-001");

        await using var session = _store.LightweightSession();
        session.Events.StartStream<NkOrderAggregate>(streamId,
            new NkHandlerOrderCreated(orderNumber, "Alice"));
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);

        await _host.TrackActivity()
            .SendMessageAndWaitAsync(new AddNkOrderItem(orderNumber, "Widget", 9.99m));

        await using var verify = _store.LightweightSession();
        var aggregate = await verify.LoadAsync<NkOrderAggregate>(streamId, TestContext.Current.CancellationToken);

        aggregate.ShouldNotBeNull();
        aggregate!.TotalAmount.ShouldBe(9.99m);
        aggregate.CustomerName.ShouldBe("Alice");
    }

    [Fact]
    public async Task handle_command_with_natural_key_returning_multiple_events()
    {
        var streamId = Guid.NewGuid();
        var orderNumber = new NkHandlerOrderNumber("ORD-WOL-002");

        await using var session = _store.LightweightSession();
        session.Events.StartStream<NkOrderAggregate>(streamId,
            new NkHandlerOrderCreated(orderNumber, "Bob"));
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);

        await _host.TrackActivity()
            .SendMessageAndWaitAsync(new AddNkOrderItems(orderNumber,
                [("Gadget", 19.99m), ("Doohickey", 5.50m)]));

        await using var verify = _store.LightweightSession();
        var aggregate = await verify.LoadAsync<NkOrderAggregate>(streamId, TestContext.Current.CancellationToken);

        aggregate.ShouldNotBeNull();
        aggregate!.TotalAmount.ShouldBe(25.49m);
    }

    [Fact]
    public async Task handle_command_with_natural_key_using_event_stream()
    {
        var streamId = Guid.NewGuid();
        var orderNumber = new NkHandlerOrderNumber("ORD-WOL-003");

        await using var session = _store.LightweightSession();
        session.Events.StartStream<NkOrderAggregate>(streamId,
            new NkHandlerOrderCreated(orderNumber, "Charlie"),
            new NkHandlerItemAdded("Widget", 10.00m));
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);

        await _host.TrackActivity()
            .SendMessageAndWaitAsync(new CompleteNkOrder(orderNumber));

        await using var verify = _store.LightweightSession();
        var aggregate = await verify.LoadAsync<NkOrderAggregate>(streamId, TestContext.Current.CancellationToken);

        aggregate.ShouldNotBeNull();
        aggregate!.IsComplete.ShouldBeTrue();
        aggregate.TotalAmount.ShouldBe(10.00m);
    }

    [Fact]
    public async Task handle_command_with_natural_key_using_an_exclusive_load()
    {
        var streamId = Guid.NewGuid();
        var orderNumber = new NkHandlerOrderNumber("ORD-WOL-004");

        await using var session = _store.LightweightSession();
        session.Events.StartStream<NkOrderAggregate>(streamId,
            new NkHandlerOrderCreated(orderNumber, "Dana"),
            new NkHandlerItemAdded("Widget", 10.00m));
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);

        await _host.TrackActivity()
            .SendMessageAndWaitAsync(new CompleteNkOrderExclusively(orderNumber));

        await using var verify = _store.LightweightSession();
        var aggregate = await verify.LoadAsync<NkOrderAggregate>(streamId, TestContext.Current.CancellationToken);

        aggregate.ShouldNotBeNull();
        aggregate!.IsComplete.ShouldBeTrue();
    }
}

#region sample_wolverine_marten_natural_key_aggregate
public record NkHandlerOrderNumber(string Value);

public class NkOrderAggregate
{
    public Guid Id { get; set; }

    [NaturalKey]
    public NkHandlerOrderNumber OrderNum { get; set; } = null!;

    public decimal TotalAmount { get; set; }
    public string CustomerName { get; set; } = string.Empty;
    public bool IsComplete { get; set; }

    [NaturalKeySource]
    public void Apply(NkHandlerOrderCreated e)
    {
        OrderNum = e.OrderNumber;
        CustomerName = e.CustomerName;
    }

    public void Apply(NkHandlerItemAdded e)
    {
        TotalAmount += e.Price;
    }

    public void Apply(NkHandlerOrderCompleted e)
    {
        IsComplete = true;
    }
}

public record NkHandlerOrderCreated(NkHandlerOrderNumber OrderNumber, string CustomerName);
public record NkHandlerItemAdded(string ItemName, decimal Price);
public record NkHandlerOrderCompleted;

#endregion

#region sample_wolverine_marten_natural_key_commands
public record AddNkOrderItem(NkHandlerOrderNumber OrderNum, string ItemName, decimal Price);
public record AddNkOrderItems(NkHandlerOrderNumber OrderNum, (string Name, decimal Price)[] Items);
public record CompleteNkOrder(NkHandlerOrderNumber OrderNum);

#endregion

#region sample_wolverine_marten_natural_key_handlers
public static class NkOrderHandler
{
    public static NkHandlerItemAdded Handle(AddNkOrderItem command,
        [WriteAggregate] NkOrderAggregate aggregate)
    {
        return new NkHandlerItemAdded(command.ItemName, command.Price);
    }

    public static IEnumerable<object> Handle(AddNkOrderItems command,
        [WriteAggregate] NkOrderAggregate aggregate)
    {
        foreach (var (name, price) in command.Items)
        {
            yield return new NkHandlerItemAdded(name, price);
        }
    }

    public static void Handle(CompleteNkOrder command,
        [WriteAggregate] IEventStream<NkOrderAggregate> stream)
    {
        stream.AppendOne(new NkHandlerOrderCompleted());
    }
}

#endregion

#region sample_wolverine_marten_exclusive_load_by_natural_key
public record CompleteNkOrderExclusively(NkHandlerOrderNumber OrderNum);

public static class NkOrderExclusiveHandler
{
    // Loaded with FetchForExclusiveWriting<NkOrderAggregate, NkHandlerOrderNumber>, which locks the
    // stream until the handler's changes are saved
    public static NkHandlerOrderCompleted Handle(CompleteNkOrderExclusively command,
        [WriteAggregate(LoadStyle = Wolverine.Marten.ConcurrencyStyle.Exclusive)] NkOrderAggregate aggregate)
        => new();
}

#endregion
