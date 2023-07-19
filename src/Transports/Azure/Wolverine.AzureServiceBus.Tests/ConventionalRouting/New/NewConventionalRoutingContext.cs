using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using TestingSupport;
using Wolverine.Runtime;
using Wolverine.Runtime.Routing;

namespace Wolverine.AzureServiceBus.Tests.ConventionalRouting.New;

public abstract class NewConventionalRoutingContext : IDisposable
{
    private IHost _host;

    internal IWolverineRuntime theRuntime
    {
        get
        {
            _host ??= WolverineHost.For(opts =>
                opts.UseAzureServiceBusTesting()
                    .UseConventionalRouting(c => c.UsePublishingBroadcastFor(t => t == typeof(BroadcastedMessage), t => "test"))
                    .AutoProvision().AutoPurgeOnStartup());

            return _host.Services.GetRequiredService<IWolverineRuntime>();
        }
    }

    public void Dispose()
    {
        _host?.Dispose();
    }

    internal void ConfigureConventions(Action<AzureServiceBusQueueAndTopicMessageRoutingConvention> configure)
    {
        _host = Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseAzureServiceBusTesting()
                    .UseConventionalRouting(c => configure(c.UsePublishingBroadcastFor(t => t == typeof(BroadcastedMessage), t => "test")))
                    .AutoProvision().AutoPurgeOnStartup();
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

    internal MessageRoute[] PublishingRoutesFor<T>()
    {
        return RoutingFor<T>().ShouldBeOfType<MessageRouter<T>>().Routes;
    }
}