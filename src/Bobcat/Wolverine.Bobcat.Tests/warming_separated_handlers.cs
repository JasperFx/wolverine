using Microsoft.Extensions.Hosting;

namespace Wolverine.Bobcat.Tests;

/// <summary>
/// GH-5133. Under MultipleHandlerBehavior.Separated a message type with more than one handler has no default
/// handler to compile -- each handler is its own chain, bound to its own local queue -- so warming used to skip
/// the message type altogether and leave every one of its handlers to compile inside the first act.
/// </summary>
public class warming_separated_handlers
{
    [Fact]
    public async Task compiles_every_separated_handler_chain()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.MultipleHandlerBehavior = MultipleHandlerBehavior.Separated;

                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType(typeof(WarmUpFirstModuleHandler))
                    .IncludeType(typeof(WarmUpSecondModuleHandler));
            }).StartAsync(TestContext.Current.CancellationToken);

        var report = host.WarmUpHandlers();

        report.Warmed.ShouldBe(2);
        report.SkippedStickyOnly.ShouldBeEmpty();
    }
}

public record WarmUpModuleEvent(Guid Id);

// Hidden from conventional discovery so it cannot leak into every other host in this assembly
[Wolverine.Attributes.WolverineIgnore]
public class WarmUpFirstModuleHandler
{
    public void Handle(WarmUpModuleEvent message)
    {
    }
}

// Hidden from conventional discovery so it cannot leak into every other host in this assembly
[Wolverine.Attributes.WolverineIgnore]
public class WarmUpSecondModuleHandler
{
    public void Handle(WarmUpModuleEvent message)
    {
    }
}
