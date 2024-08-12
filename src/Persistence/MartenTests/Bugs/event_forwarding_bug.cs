using IntegrationTests;
using Marten;
using Marten.Events;
using MassTransit;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Marten;
using Wolverine.Tracking;

namespace MartenTests.Bugs;

public class event_forwarding_bug
{
    [Fact]
    public async Task publish_ievent_of_t()
    {
        // The "Arrange"
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Policies.AutoApplyTransactions();

                opts.Durability.Mode = DurabilityMode.Solo;

                opts.Services.AddMarten(m =>
                {
                    m.Connection(Servers.PostgresConnectionString);
                    m.DatabaseSchemaName = "forwarding";

                    m.Events.StreamIdentity = StreamIdentity.AsString;
                    m.Projections.LiveStreamAggregation<ShoppingList>();
                }).UseLightweightSessions()
                .IntegrateWithWolverine()
                .EventForwardingToWolverine();;
            }).StartAsync();

        var runtime = host.GetRuntime();
        var routing = runtime.RoutingFor(typeof(Event<ShoppingListCreated>));
        
        // The "Act". This method is an extension method in Wolverine
        // specifically for facilitating integration testing that should
        // invoke the given message with Wolverine, then wait until all
        // additional "work" is complete
        var session = await host.InvokeMessageAndWaitAsync(new CreateShoppingList());

        // And finally, just assert that a single message of
        // type IEvent<ShoppingListCreated> was executed
        session.Executed.SingleMessage<IEvent<ShoppingListCreated>>()
            .ShouldNotBeNull();
        
        session.Executed.SingleEnvelope<IEvent<ShoppingListCreated>>()
            .Destination.ShouldBe(new Uri("local://ieventshoppinglistcreated/"));
    }
}

public record AddShoppingListItem(string ShoppingListId, string ItemName);

public static class AddShoppingListItemHandler
{
    public static async Task Handle(AddShoppingListItem command, IDocumentSession session)
    {
        var stream = await session.Events.FetchForWriting<ShoppingList>(command.ShoppingListId);
        var shoppingList = stream.Aggregate;
        if (shoppingList is null)
            throw new InvalidOperationException("Shopping list does not exist");

        if (shoppingList.Contains(command.ItemName))
            throw new InvalidOperationException("Item is already in shopping list");
        
        stream.AppendOne(new ShoppingListItemAdded(command.ItemName));
    }
}

public record CreateShoppingList();

public static class CreateShoppingListHandler
{
    public static string Handle(CreateShoppingList _, IDocumentSession session)
    {
        var shoppingListId = NewId.NextSequentialGuid().ToString();
        session.Events.StartStream<ShoppingList>(shoppingListId, new ShoppingListCreated(shoppingListId));
        return shoppingListId;
    }
}

public static class IntegrationHandler
{
    public static void Handle(IEvent<ShoppingListCreated> _)
    {
        // Don't need a body here, and I'll show why not
        // next
    }
}

public record ShoppingListCreated(string Id);

public record ShoppingListItemAdded(string ItemName);

public class ShoppingList
{
    public string Id { get; init; } = null!;
    private List<ShoppingListItem> Items { get; init; } = null!;

    public bool Contains(string itemName) => Items.Any(item => item.Name == itemName);

    public static ShoppingList Create(ShoppingListCreated _)
    {
        return new ShoppingList
        {
            Items = [],
        };
    }

    public void Apply(ShoppingListItemAdded @event)
    {
        Items.Add(new ShoppingListItem
        {
            Name = @event.ItemName
        });
    }
}

public record ShoppingListItem
{
    public required string Name { get; init; }
}