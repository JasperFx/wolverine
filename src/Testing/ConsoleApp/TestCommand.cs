using Microsoft.Extensions.DependencyInjection;
using Oakton;
using TestingSupport.Compliance;
using Wolverine;

namespace ConsoleApp;

public class TestCommand : OaktonAsyncCommand<NetCoreInput>
{
    public override async Task<bool> Execute(NetCoreInput input)
    {
        using var host = input.BuildHost();
        await host.MessageBus().InvokeAsync(new PongMessage());

        return true;
    }
}