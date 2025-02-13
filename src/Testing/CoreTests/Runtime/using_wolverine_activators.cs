using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NSubstitute;
using Wolverine.Runtime;
using Wolverine.Tracking;
using Xunit;

namespace CoreTests.Runtime;

public class using_wolverine_activators
{
    [Fact]
    public async Task an_activator_is_called()
    {
        var activator1 = Substitute.For<IWolverineActivator>();
        var activator2 = Substitute.For<IWolverineActivator>();
        var activator3 = Substitute.For<IWolverineActivator>();

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Services.AddSingleton(activator1);
                opts.Services.AddSingleton(activator2);
                opts.Services.AddSingleton(activator3);
            }).StartAsync();

        var runtime = host.GetRuntime();
        
        activator1.Received().Apply(runtime);
        activator2.Received().Apply(runtime);
        activator3.Received().Apply(runtime);
    }
}