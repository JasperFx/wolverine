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
    private IHost _host;

    internal IWolverineRuntime theRuntime
    {
        get
        {
            _host ??= WolverineHost.For(opts =>
                opts.UseAzureServiceBusTesting().UseConventionalRouting().AutoProvision().AutoPurgeOnStartup());

            return _host.Services.GetRequiredService<IWolverineRuntime>();
        }
    }

    public Task InitializeAsync()
    {
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (_host != null) await _host.StopAsync();
        _host?.Dispose();
        await AzureServiceBusTesting.DeleteAllEmulatorObjectsAsync();
    }

    internal void ConfigureConventions(Action<AzureServiceBusMessageRoutingConvention> configure)
    {
        _host = Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseAzureServiceBusTesting().UseConventionalRouting(configure).AutoProvision()
                    .AutoPurgeOnStartup();
            }).Start();
    }

    internal IMessageRouter RoutingFor<T>()
    {
        return theRuntime.RoutingFor(typeof(T));
    }

    internal void AssertNoRoutes<T>()
    {
        RoutingFor<T>().ShouldBeOfType<EmptyMessageRouter<T>>();
    }

    internal IMessageRoute[] PublishingRoutesFor<T>()
    {
        return RoutingFor<T>().ShouldBeOfType<MessageRouter<T>>().Routes;
    }
}