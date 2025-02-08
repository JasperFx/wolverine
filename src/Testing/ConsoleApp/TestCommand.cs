using Microsoft.Extensions.DependencyInjection;
using JasperFx;
using JasperFx.CommandLine;
using Wolverine.ComplianceTests.Compliance;
using Wolverine;

namespace ConsoleApp;

public class TestCommand : JasperFxAsyncCommand<NetCoreInput>
{
    public override async Task<bool> Execute(NetCoreInput input)
    {
        using var host = input.BuildHost();
        await host.MessageBus().InvokeAsync(new PongMessage());

        return true;
    }
}