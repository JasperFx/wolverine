using Microsoft.Extensions.Hosting;
using TestingSupport;
using Wolverine.Attributes;
using Wolverine.Middleware;
using Wolverine.Runtime.Routing;
using Wolverine.Tracking;
using Xunit;

namespace CoreTests.Bugs;

public class Bug_267_throw_descriptive_message_on_multiple_variables
{
    [Fact]
    public async Task descriptive_message_somehow()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.DisableConventionalDiscovery().IncludeType<Bug267Handler>();
            }).StartAsync();

        
        
        await Should.ThrowAsync<InvalidWolverineMiddlewareException>(async () =>
        {
            await host.InvokeMessageAndWaitAsync(new Bug267("boom"));
        });
    }
}

public record Bug267(string Name);

[WolverineIgnore]
public class Bug267Handler
{
    public (string, string, int, int) Load(Bug267 command)
    {
        return ("one", "two", 3, 4);
    }

    public void Handle(Bug267 command, string text, int number)
    {
        
    }
}