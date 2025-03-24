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

    [Fact]
    public async Task honor_the_attribute()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Services.AddMarten(Servers.PostgresConnectionString).IntegrateWithWolverine();
                // Add the auto transaction middleware attachment policy
                opts.Policies.AutoApplyTransactions();
            }).StartAsync();

        Func<IMessageContext, Task> execute = async c =>
        {
            var m2 = await c.InvokeAsync<AlwaysMessage2>(new AlwaysMessage1());
            m2.ShouldNotBeNull();
        };

        var tracked = await host
            .TrackActivity()
            .WaitForMessageToBeReceivedAt<AlwaysMessage3>(host)
            .ExecuteAndWaitAsync(execute);

        tracked.Executed.SingleMessage<AlwaysMessage2>().ShouldNotBeNull();
        tracked.Executed.SingleMessage<AlwaysMessage3>().ShouldNotBeNull();
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

public record AlwaysMessage1;
public record AlwaysMessage2;
public record AlwaysMessage3;

public static class AlwaysMessageHandler
{
    [AlwaysPublishResponse]
    public static AlwaysMessage2 Handle(AlwaysMessage1 request, IMessageBus messageBus)
    {
        Console.WriteLine("Message1Handler");

        return new AlwaysMessage2();
    }
}

public static class Message2Handler
{
    public static AlwaysMessage3 Handle(AlwaysMessage2 request)
    {
        Console.WriteLine("Message2Handler");

        return new AlwaysMessage3();
    }
}

public static class Message3Handler
{
    public static void Handle(AlwaysMessage3 request)
    {
        Console.WriteLine("Message3Handler");
    }
}

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