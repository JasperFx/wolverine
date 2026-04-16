using Microsoft.Extensions.Hosting;
using Wolverine.Runtime;
using Wolverine.Runtime.Handlers;
using Wolverine.Tracking;
using Xunit;

namespace CoreTests.Bugs;

public class Bug_2326_disambiguate_outgoing_messages_from_multiple_middleware
{
    [Fact]
    public async Task can_compile_handler_with_multiple_middleware_returning_outgoing_messages()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType<Bug2326Handler>();
            }).StartAsync();

        // If code gen produces duplicate variable names, compilation will fail here
        var chain = host.GetRuntime().Handlers.ChainFor<Bug2326Command>();
        chain.ShouldNotBeNull();

        // Force compilation of the handler
        var handler = host.GetRuntime().Handlers.HandlerFor<Bug2326Command>();
        handler.ShouldNotBeNull();
    }
}

public record Bug2326Command;
public record Bug2326Cascaded(string Source);

public class Bug2326Handler
{
    public static OutgoingMessages Before(Bug2326Command command)
    {
        return [new Bug2326Cascaded("Before")];
    }

    public static OutgoingMessages Load(Bug2326Command command)
    {
        return [new Bug2326Cascaded("Load")];
    }

    public static void Handle(Bug2326Command command)
    {
    }
}
