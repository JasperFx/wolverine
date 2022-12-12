using System;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TestingSupport;
using Wolverine.Runtime.Handlers;
using Xunit;
using Xunit.Abstractions;

namespace CoreTests.Compilation;

public class handler_that_uses_ilogger
{
    private readonly ITestOutputHelper _output;

    public handler_that_uses_ilogger(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task can_compile_with_ilogger_dependency_Bug_666()
    {
        using var host = WolverineHost.Basic();

        var bus = host.Services.GetRequiredService<IMessageBus>();
        await bus.InvokeAsync(new ItemCreated());

        var graph = host.Services.GetRequiredService<HandlerGraph>();
        var chain = graph.ChainFor<ItemCreated>();
        
        _output.WriteLine(chain.SourceCode);
    }
}

public class ItemCreated
{
    public Guid Id { get; set; } = Guid.NewGuid();
}

public class ItemCreatedHandler
{
    public void Handle(ItemCreated itemCreated, ILogger logger)
    {
        logger.LogInformation("Item created with id {Id}", itemCreated.Id);
    }
}