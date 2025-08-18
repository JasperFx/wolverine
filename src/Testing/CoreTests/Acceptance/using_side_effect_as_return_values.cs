using JasperFx;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolverine.Runtime;
using Wolverine.Runtime.Handlers;
using Wolverine.Tracking;
using Xunit;

namespace CoreTests.Acceptance;

public class using_side_effect_as_return_values
{
    [Fact]
    public async Task using_side_effect_as_return_value()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Services.AddSingleton<Recorder>();
            }).StartAsync();

        var graph = host.Services.GetRequiredService<HandlerGraph>();
        var chain = graph.ChainFor<TriggerSideEffects>();

        // Adds the dependency from the methods
        chain.ServiceDependencies(host.Services.GetRequiredService<IServiceContainer>(), Type.EmptyTypes).ShouldContain(typeof(Recorder));

        var recorder = host.Services.GetRequiredService<Recorder>();


        await host.InvokeMessageAndWaitAsync(new TriggerSideEffects());

        recorder.Actions.ShouldContain("WriteOne");
        recorder.Actions.ShouldContain("WriteTwo");
    }
}

public record TriggerSideEffects;


public static class TriggerSideEffectsHandler
{
    public static (WriteOne, WriteTwo) Handle(TriggerSideEffects command)
    {
        return (new WriteOne(), new WriteTwo());
    }
}

public class WriteOne : ISideEffect
{
    public void Execute(Recorder recorder)
    {
        recorder.Actions.Add("WriteOne");
    }
}

public class WriteTwo : ISideEffect
{
    public Task ExecuteAsync(Recorder recorder, CancellationToken cancellationToken)
    {
        recorder.Actions.Add("WriteTwo");
        return Task.CompletedTask;
    }
}