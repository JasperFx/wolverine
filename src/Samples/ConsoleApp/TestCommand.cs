using System.Threading.Tasks;
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
        await host.Services.GetRequiredService<ICommandBus>().InvokeAsync(new PongMessage());

        return true;
    }
}