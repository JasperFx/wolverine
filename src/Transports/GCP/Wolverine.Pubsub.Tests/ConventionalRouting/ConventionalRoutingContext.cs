using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.ComplianceTests;
using Wolverine.Runtime;
using Wolverine.Runtime.Routing;

namespace Wolverine.Pubsub.Tests.ConventionalRouting;

public abstract class ConventionalRoutingContext : IDisposable
{
    private IHost _host = default!;

    internal async Task<IWolverineRuntime> theRuntime()
    {
        _host ??= await WolverineHost.ForAsync(opts => opts
            .UsePubsubTesting()
            .AutoProvision()
            .AutoPurgeOnStartup()
            .EnableDeadLettering()
            .EnableSystemEndpoints()
            .UseConventionalRouting()
        );

        return _host.Services.GetRequiredService<IWolverineRuntime>();
    }

    public void Dispose()
    {
        _host?.Dispose();
    }

    internal async Task ConfigureConventions(Action<PubsubMessageRoutingConvention> configure)
    {
        _host = await Host
            .CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts
                    .UsePubsubTesting()
                    .AutoProvision()
                    .AutoPurgeOnStartup()
                    .EnableDeadLettering()
                    .EnableSystemEndpoints()
                    .UseConventionalRouting(configure);
            }).StartAsync();
    }

    internal async Task<IMessageRouter> RoutingFor<T>()
    {
        return (await theRuntime()).RoutingFor(typeof(T));
    }

    internal async Task AssertNoRoutes<T>()
    {
        (await RoutingFor<T>()).ShouldBeOfType<EmptyMessageRouter<T>>();
    }

    internal async Task<IMessageRoute[]> PublishingRoutesFor<T>()
    {
        return (await RoutingFor<T>()).ShouldBeOfType<MessageRouter<T>>().Routes;
    }
}
