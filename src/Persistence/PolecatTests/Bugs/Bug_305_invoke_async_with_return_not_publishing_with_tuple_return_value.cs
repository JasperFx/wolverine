using IntegrationTests;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Polecat;
using Wolverine;
using Wolverine.Polecat;
using Wolverine.Tracking;
using Shouldly;
using Wolverine.Attributes;

namespace PolecatTests.Bugs;

public class Bug_305_invoke_async_with_return_not_publishing_with_tuple_return_value
{
    [Fact]
    public async Task should_publish_the_return_value()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Services.AddPolecat(m =>
                {
                    m.ConnectionString = Servers.SqlServerConnectionString;
                    m.DatabaseSchemaName = "bugs_305";
                }).IntegrateWithWolverine();

                opts.Policies.AutoApplyTransactions();
            }).StartAsync();

        await ((DocumentStore)host.Services.GetRequiredService<IDocumentStore>()).Database
            .ApplyAllConfiguredChangesToDatabaseAsync();

        var (tracked, created) =
            await host.InvokeMessageAndWaitAsync<PcItemCreated>(new PcCreateItemCommand { Name = "Trevor" });

        created!.Name.ShouldBe("Trevor");

        tracked.Sent.SingleMessage<PcItemCreated>().Name.ShouldBe("Trevor");
        tracked.Sent.SingleMessage<PcSecondItemCreated>().Name.ShouldBe("Trevor");
    }

    [Fact]
    public async Task honor_the_attribute()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Services.AddPolecat(m =>
                {
                    m.ConnectionString = Servers.SqlServerConnectionString;
                    m.DatabaseSchemaName = "bugs_305";
                }).IntegrateWithWolverine();

                opts.Policies.AutoApplyTransactions();
            }).StartAsync();

        await ((DocumentStore)host.Services.GetRequiredService<IDocumentStore>()).Database
            .ApplyAllConfiguredChangesToDatabaseAsync();

        Func<IMessageContext, Task> execute = async c =>
        {
            var m2 = await c.InvokeAsync<PcAlwaysMessage2>(new PcAlwaysMessage1());
            m2.ShouldNotBeNull();
        };

        var tracked = await host
            .TrackActivity()
            .WaitForMessageToBeReceivedAt<PcAlwaysMessage3>(host)
            .ExecuteAndWaitAsync(execute);

        tracked.Executed.SingleMessage<PcAlwaysMessage2>().ShouldNotBeNull();
        tracked.Executed.SingleMessage<PcAlwaysMessage3>().ShouldNotBeNull();
    }
}

public class PcCreateItemCommand
{
    public string Name { get; set; } = string.Empty;
}

public class PcCreateItemCommandHandler
{
    [AlwaysPublishResponse]
    public async Task<(PcItemCreated, PcSecondItemCreated)> Handle(PcCreateItemCommand command,
        IDocumentSession session)
    {
        var item = new PcItem
        {
            Id = Guid.NewGuid(),
            Name = command.Name
        };

        session.Store(item);

        return (new PcItemCreated(item.Id, item.Name), new PcSecondItemCreated(item.Id, item.Name));
    }
}

public record PcAlwaysMessage1;
public record PcAlwaysMessage2;
public record PcAlwaysMessage3;

public static class PcAlwaysMessageHandler
{
    [AlwaysPublishResponse]
    public static PcAlwaysMessage2 Handle(PcAlwaysMessage1 request, IMessageBus messageBus)
    {
        Console.WriteLine("Message1Handler");
        return new PcAlwaysMessage2();
    }
}

public static class PcMessage2Handler
{
    public static PcAlwaysMessage3 Handle(PcAlwaysMessage2 request)
    {
        Console.WriteLine("Message2Handler");
        return new PcAlwaysMessage3();
    }
}

public static class PcMessage3Handler
{
    public static void Handle(PcAlwaysMessage3 request)
    {
        Console.WriteLine("Message3Handler");
    }
}

public record PcItemCreated(Guid Id, string Name);
public record PcSecondItemCreated(Guid Id, string Name);

public class PcItemCreatedHandler
{
    public Task Handle(PcItemCreated message)
    {
        Console.WriteLine($"Item created {message.Id} {message.Name}");
        return Task.CompletedTask;
    }
}

public class PcSecondItemCreatedHandler
{
    public Task Handle(PcSecondItemCreated message)
    {
        Console.WriteLine($"Second Item created {message.Id} {message.Name}");
        return Task.CompletedTask;
    }
}

public class PcItem
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
}
