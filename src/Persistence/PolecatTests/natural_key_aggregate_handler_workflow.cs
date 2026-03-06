using IntegrationTests;
using JasperFx;
using JasperFx.CodeGeneration;
using JasperFx.Events.Aggregation;
using Polecat.Events;
using JasperFx.Resources;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Polecat;
using Polecat.Projections;
using Shouldly;
using Wolverine;
using Wolverine.Polecat;
using Wolverine.Tracking;
using Xunit;

namespace PolecatTests;

public class natural_key_aggregate_handler_workflow : IAsyncLifetime
{
    private IHost _host = null!;
    private IDocumentStore _store = null!;

    public async Task InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Services.AddPolecat(m =>
                    {
                        m.ConnectionString = Servers.SqlServerConnectionString;
                        m.DatabaseSchemaName = "nk_handler";
                        m.Projections.Snapshot<PcNkOrderAggregate>(SnapshotLifecycle.Inline);
                    })
                    .UseLightweightSessions()
                    .IntegrateWithWolverine();

                opts.Services.AddResourceSetupOnStartup();
                opts.CodeGeneration.TypeLoadMode = TypeLoadMode.Auto;
            }).StartAsync();

        _store = _host.Services.GetRequiredService<IDocumentStore>();

        // Ensure the Polecat event store schema is created
        var store = (DocumentStore)_store;
        await store.Database.ApplyAllConfiguredChangesToDatabaseAsync();
    }

    public async Task DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    [Fact]
    public async Task handle_command_with_natural_key_returning_single_event()
    {
        var streamId = Guid.NewGuid();
        var orderNumber = new PcNkOrderNumber("ORD-PC-001");

        await using var session = _store.LightweightSession();
        session.Events.StartStream(streamId,
            new PcNkOrderCreated(orderNumber, "Alice"));
        await session.SaveChangesAsync();

        await _host.TrackActivity()
            .SendMessageAndWaitAsync(new AddPcNkOrderItem(orderNumber, "Widget", 9.99m));

        await using var verify = _store.LightweightSession();
        var aggregate = await verify.LoadAsync<PcNkOrderAggregate>(streamId);

        aggregate.ShouldNotBeNull();
        aggregate!.TotalAmount.ShouldBe(9.99m);
        aggregate.CustomerName.ShouldBe("Alice");
    }

    [Fact]
    public async Task handle_command_with_natural_key_returning_multiple_events()
    {
        var streamId = Guid.NewGuid();
        var orderNumber = new PcNkOrderNumber("ORD-PC-002");

        await using var session = _store.LightweightSession();
        session.Events.StartStream(streamId,
            new PcNkOrderCreated(orderNumber, "Bob"));
        await session.SaveChangesAsync();

        await _host.TrackActivity()
            .SendMessageAndWaitAsync(new AddPcNkOrderItems(orderNumber,
                [("Gadget", 19.99m), ("Doohickey", 5.50m)]));

        await using var verify = _store.LightweightSession();
        var aggregate = await verify.LoadAsync<PcNkOrderAggregate>(streamId);

        aggregate.ShouldNotBeNull();
        aggregate!.TotalAmount.ShouldBe(25.49m);
    }

    [Fact]
    public async Task handle_command_with_natural_key_using_event_stream()
    {
        var streamId = Guid.NewGuid();
        var orderNumber = new PcNkOrderNumber("ORD-PC-003");

        await using var session = _store.LightweightSession();
        session.Events.StartStream(streamId,
            new PcNkOrderCreated(orderNumber, "Charlie"),
            new PcNkItemAdded("Widget", 10.00m));
        await session.SaveChangesAsync();

        await _host.TrackActivity()
            .SendMessageAndWaitAsync(new CompletePcNkOrder(orderNumber));

        await using var verify = _store.LightweightSession();
        var aggregate = await verify.LoadAsync<PcNkOrderAggregate>(streamId);

        aggregate.ShouldNotBeNull();
        aggregate!.IsComplete.ShouldBeTrue();
        aggregate.TotalAmount.ShouldBe(10.00m);
    }
}

#region sample_wolverine_polecat_natural_key_aggregate

public record PcNkOrderNumber(string Value);

public class PcNkOrderAggregate
{
    public Guid Id { get; set; }

    [NaturalKey]
    public PcNkOrderNumber OrderNum { get; set; } = null!;

    public decimal TotalAmount { get; set; }
    public string CustomerName { get; set; } = string.Empty;
    public bool IsComplete { get; set; }

    [NaturalKeySource]
    public void Apply(PcNkOrderCreated e)
    {
        OrderNum = e.OrderNumber;
        CustomerName = e.CustomerName;
    }

    public void Apply(PcNkItemAdded e)
    {
        TotalAmount += e.Price;
    }

    public void Apply(PcNkOrderCompleted e)
    {
        IsComplete = true;
    }
}

public record PcNkOrderCreated(PcNkOrderNumber OrderNumber, string CustomerName);
public record PcNkItemAdded(string ItemName, decimal Price);
public record PcNkOrderCompleted;

#endregion

#region sample_wolverine_polecat_natural_key_commands

public record AddPcNkOrderItem(PcNkOrderNumber OrderNum, string ItemName, decimal Price);
public record AddPcNkOrderItems(PcNkOrderNumber OrderNum, (string Name, decimal Price)[] Items);
public record CompletePcNkOrder(PcNkOrderNumber OrderNum);

#endregion

#region sample_wolverine_polecat_natural_key_handlers

public static class PcNkOrderHandler
{
    public static PcNkItemAdded Handle(AddPcNkOrderItem command,
        [WriteAggregate] PcNkOrderAggregate aggregate)
    {
        return new PcNkItemAdded(command.ItemName, command.Price);
    }

    public static IEnumerable<object> Handle(AddPcNkOrderItems command,
        [WriteAggregate] PcNkOrderAggregate aggregate)
    {
        foreach (var (name, price) in command.Items)
        {
            yield return new PcNkItemAdded(name, price);
        }
    }

    public static void Handle(CompletePcNkOrder command,
        [WriteAggregate] IEventStream<PcNkOrderAggregate> stream)
    {
        stream.AppendOne(new PcNkOrderCompleted());
    }
}

#endregion
