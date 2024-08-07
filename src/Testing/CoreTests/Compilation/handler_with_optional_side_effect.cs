using Microsoft.Extensions.DependencyInjection;
using Wolverine.ComplianceTests;
using Wolverine.Runtime.Handlers;
using Xunit;
using Xunit.Abstractions;

namespace CoreTests.Compilation;

public class handler_with_optional_side_effect
{
    private readonly ITestOutputHelper _output;

    public handler_with_optional_side_effect(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task can_compile_correctly_for_handler_with_optional_side_effect_returning_null()
    {
        using var host = WolverineHost.Basic();

        var bus = host.MessageBus();
        await bus.InvokeAsync(new SomeCommand());

        var graph = host.Services.GetRequiredService<HandlerGraph>();
        var chain = graph.ChainFor<SomeCommand>();

        _output.WriteLine(chain.SourceCode);
    }

    [Fact]
    public async Task can_compile_correctly_for_handler_with_optional_side_effect_returning_the_side_effect()
    {
        using var host = WolverineHost.Basic();

        var bus = host.MessageBus();
        await bus.InvokeAsync(new SomeOtherCommand());

        var graph = host.Services.GetRequiredService<HandlerGraph>();
        var chain = graph.ChainFor<SomeOtherCommand>();

        _output.WriteLine(chain.SourceCode);
    }
}

public class SomeCommand;

public class SomeOtherCommand;

public class SomeSideEffect : ISideEffect
{
    public Task ExecuteAsync() => Task.CompletedTask;
}

public class SomeCommandHandler
{
    public SomeSideEffect? Handle(SomeCommand cmd)
    {
        return null;
    }

    public SomeSideEffect? Handle(SomeOtherCommand cmd)
    {
        return new SomeSideEffect();
    }
}