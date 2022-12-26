using Microsoft.Extensions.DependencyInjection;
using Oakton;
using TestingSupport.Compliance;
using Wolverine;

namespace MyApp;

public class TestCommand : OaktonAsyncCommand<NetCoreInput>
{
    public override async Task<bool> Execute(NetCoreInput input)
    {
        using var host = input.BuildHost();
        await host.Services.GetRequiredService<IMessageBus>().InvokeAsync(new PongMessage());

        return true;
    }
}