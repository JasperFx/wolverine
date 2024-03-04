using Microsoft.Extensions.Hosting;
using TestingSupport.Compliance;
using Wolverine.Tracking;
using Xunit;

namespace CoreTests.Acceptance;

public class local_invoke_also_publishes_the_return_value 
{
    [Fact]
    public async Task should_also_publish_the_return_value_when_invoking_locally()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine().StartAsync();

        var name = "Chris Jones";
        var (tracked, response) = await host.InvokeMessageAndWaitAsync<CommandInvoked>(new InvokeCommand(name));
        
        response.Name.ShouldBe(name);
        
        tracked.Sent.SingleMessage<CommandInvoked>()
            .Name.ShouldBe(name);
        
        tracked.Sent.SingleMessage<Message1>().Id.ShouldBe(response.Id);
        tracked.Sent.SingleMessage<Message2>().Id.ShouldBe(response.Id);
    }
}

public static class InvokedHandler
{
    public static (CommandInvoked, Message1, Message2) Handle(InvokeCommand command)
    {
        var invoked = new CommandInvoked(Guid.NewGuid(), command.Name);
        return (invoked, new Message1 { Id = invoked.Id }, new Message2 { Id = invoked.Id });
    }
}

public record InvokeCommand(string Name);
public record CommandInvoked(Guid Id, string Name);

