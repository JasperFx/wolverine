using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Wolverine.Tracking;
using Xunit;

namespace CoreTests.Bugs;

public class Bug_143_disambiguate_logger_variables
{
    [Fact]
    public async Task can_disentangle_the_variables()
    {
        using var host = await Host
            .CreateDefaultBuilder()
            .UseWolverine()
            .StartAsync();

        await host.InvokeMessageAndWaitAsync(new LoggedMessage("Nick Boltan"));
    }
}

public record LoggedMessage(string Name);

public class FirstHandler
{
    public static void Handle(LoggedMessage message, ILogger<FirstHandler> logger)
    {
        logger.LogInformation("Doing the first: {Name}", message.Name);
    }
}

public class SecondHandler
{
    public static void Handle(LoggedMessage message, ILogger<SecondHandler> logger)
    {
        logger.LogInformation("Doing the second: {Name}", message.Name);
    }
}