using IntegrationTests;
using Marten;
using Microsoft.Extensions.Hosting;
using Wolverine;
using Wolverine.Marten;
using Wolverine.Tracking;
using Shouldly;
using Wolverine.Attributes;

namespace MartenTests.Bugs;

public class Bug_305_invoke_async_with_return_not_publishing_with_tuple_return_value : PostgresqlContext
{
    [Fact]
    public async Task should_publish_the_return_value()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Services.AddMarten(Servers.PostgresConnectionString).IntegrateWithWolverine();
                // Add the auto transaction middleware attachment policy
                opts.Policies.AutoApplyTransactions();
            }).StartAsync();

        var (tracked, created) = await host.InvokeMessageAndWaitAsync<ItemCreated>(new CreateItemCommand { Name = "Trevor" });

        created.Name.ShouldBe("Trevor");

        tracked.Sent.SingleMessage<ItemCreated>().Name.ShouldBe("Trevor");
        tracked.Sent.SingleMessage<SecondItemCreated>().Name.ShouldBe("Trevor");
    }
}

public class CreateItemCommand
{
    public string Name { get; set; } = string.Empty;
}

#region sample_using_AlwaysPublishResponse

public class CreateItemCommandHandler
{
    // Using this attribute will force Wolverine to also publish the ItemCreated event even if
    // this is called by IMessageBus.InvokeAsync<ItemCreated>()
    [AlwaysPublishResponse]
    public async Task<(ItemCreated, SecondItemCreated)> Handle(CreateItemCommand command, IDocumentSession session)
    {
        var item = new Item
        {
            Id = Guid.NewGuid(),
            Name = command.Name
        };

        session.Store(item);

        return (new ItemCreated(item.Id, item.Name), new SecondItemCreated(item.Id, item.Name));
    }
}

#endregion

public record ItemCreated(Guid Id, string Name);

public record SecondItemCreated(Guid Id, string Name);

public class ItemCreatedHandler
{
    public Task Handle(ItemCreated message)
    {
        Console.WriteLine($"Item created {message.Id} {message.Name}");
        return Task.CompletedTask;
    }
}

public class SecondItemCreatedHandler
{
    public Task Handle(SecondItemCreated message)
    {
        Console.WriteLine($"Second Item created {message.Id} {message.Name}");
        return Task.CompletedTask;
    }
}

public class Item
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
}