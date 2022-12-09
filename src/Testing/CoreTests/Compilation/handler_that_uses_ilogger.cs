using System;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TestingSupport;
using Xunit;

namespace CoreTests.Compilation;

public class handler_that_uses_ilogger
{
    [Fact]
    public async Task can_compile_with_ilogger_dependency_Bug_666()
    {
        using var host = WolverineHost.Basic();

        var bus = host.Services.GetRequiredService<IMessageBus>();
        await bus.InvokeAsync(new ItemCreated());
    }
}

public class ItemCreated
{
    public Guid Id { get; set; } = Guid.NewGuid();
}

public class ItemCreatedHandler
{
    private readonly ILogger _logger;

    public ItemCreatedHandler(ILogger<ItemCreatedHandler> logger)
    {
        _logger = logger;
    }

    public void Handle(ItemCreated itemCreated)
    {
        _logger.LogInformation("Item created with id {Id}", itemCreated.Id);
    }
}