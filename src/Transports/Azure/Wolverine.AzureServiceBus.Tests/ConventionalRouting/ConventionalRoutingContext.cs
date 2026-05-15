using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.ComplianceTests;
using Wolverine.Runtime;
using Wolverine.Runtime.Routing;
using Xunit;

namespace Wolverine.AzureServiceBus.Tests.ConventionalRouting;

public abstract class ConventionalRoutingContext : IAsyncLifetime
{
    private IHost _host = null!;

    internal async Task<IWolverineRuntime> theRuntime()
    {
        _host ??= await WolverineHost.ForAsync(opts =>
            opts.UseAzureServiceBusTesting().UseConventionalRouting().AutoProvision().AutoPurgeOnStartup());

        return _host.Services.GetRequiredService<IWolverineRuntime>();
    }

    public virtual Task InitializeAsync()
    {
        return Task.CompletedTask;
    }

    public virtual async Task DisposeAsync()
    {
        if (_host != null) await _host.StopAsync();
        _host?.Dispose();
        await AzureServiceBusTesting.DeleteAllEmulatorObjectsAsync();
    }

    internal async Task ConfigureConventions(Action<AzureServiceBusMessageRoutingConvention> configure)
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseAzureServiceBusTesting().UseConventionalRouting(configure).AutoProvision()
                    .AutoPurgeOnStartup();
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
