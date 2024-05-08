using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TestingSupport;
using Wolverine.Tracking;
using Xunit;

namespace CoreTests.Bugs;

public class Bug_147_disambiguate_variables_from_multiple_handlers
{
    [Fact]
    public async Task can_return_same_type_from_multiple_handlers()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine().StartAsync();

        var tracked = await host.InvokeMessageAndWaitAsync(new StartingMessage("Creed Humphrey"));

        var messages = tracked.Executed.MessagesOf<EndingMessage>();
        messages.OrderBy(x => x.Name).Select(x => x.Name)
            .ShouldHaveTheSameElementsAs("Creed Humphrey", "Creed Humphrey-Other");
    }
}

public record StartingMessage(string Name);
public record EndingMessage(string Name);

public class StartingMessageHandler
{
    public void Handle(EndingMessage message, ILogger logger)
    {
        logger.LogDebug("Got the end for {Name}", message.Name);
    }

    public EndingMessage Handle(StartingMessage message)
    {
        return new EndingMessage(message.Name);
    }
}

public class OtherStartingMessageHandler
{
    public EndingMessage Handle(StartingMessage message)
    {
        return new EndingMessage(message.Name + "-Other");
    }
}