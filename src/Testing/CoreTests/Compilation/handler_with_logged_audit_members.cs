using System;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TestingSupport;
using Wolverine.Attributes;
using Wolverine.Logging;
using Wolverine.Runtime.Handlers;
using Xunit;
using Xunit.Abstractions;

namespace CoreTests.Compilation;

public class handler_with_logged_audit_members
{
    private readonly ITestOutputHelper _output;

    public handler_with_logged_audit_members(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task can_compile()
    {
        using var host = await WolverineHost.ForAsync(o => o.AddAuditLogging());

        var bus = host.Services.GetRequiredService<IMessageBus>();
        await bus.InvokeAsync(new SomeMessage());

        var graph = host.Services.GetRequiredService<HandlerGraph>();
        var chain = graph.ChainFor<SomeMessage>();
        
        _output.WriteLine(chain.SourceCode);
    }
}

public class SomeMessage
{
    [Audit]public Guid Id { get; set; } = Guid.NewGuid();
    [Audit]public string SomeString { get; set; } = "test";
}

public class SomeMessageHandler
{
    public void Handle(SomeMessage itemCreated, ILogger logger)
    {
        logger.LogInformation("Some message id {Id}", itemCreated.Id);
    }
}