using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.ComplianceTests;
using Wolverine.Runtime;
using Wolverine.Runtime.Routing;

namespace Wolverine.Pubsub.Tests.ConventionalRouting;

public abstract class ConventionalRoutingContext : IAsyncDisposable
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

    // GH-5536. Stop the host, don't just dispose it: every host here listens on the shared emulator, and one left
    // running keeps pulling from the "routed" subscription as a competing consumer for every later test that uses it
    public async ValueTask DisposeAsync()
    {
        if (_host == null) return;

        await _host.StopAsync();
        _host.Dispose();
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
        (await RoutingFor<T>()).ShouldBeOfType<EmptyMessageRouter>();
    }

    internal async Task<IMessageRoute[]> PublishingRoutesFor<T>()
    {
        return (await RoutingFor<T>()).ShouldBeOfType<MessageRouter>().Routes;
    }
}
