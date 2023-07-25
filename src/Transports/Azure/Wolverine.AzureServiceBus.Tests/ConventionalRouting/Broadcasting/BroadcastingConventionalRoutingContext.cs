using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using TestingSupport;
using Wolverine.Runtime;
using Wolverine.Runtime.Routing;

namespace Wolverine.AzureServiceBus.Tests.ConventionalRouting.Broadcasting;

public abstract class BroadcastingConventionalRoutingContext : IDisposable
{
    private IHost _host;

    internal IWolverineRuntime theRuntime
    {
        get
        {
            _host ??= WolverineHost.For(opts =>
                opts.UseAzureServiceBusTesting()
                    .UseBroadcastingConventionRouting(c => c.IncludeTypes(t => t == typeof(BroadcastedMessage))
                        .SubscriptionNameForListener(t => "tests"))
                    .AutoProvision().AutoPurgeOnStartup());

            return _host.Services.GetRequiredService<IWolverineRuntime>();
        }
    }

    public void Dispose()
    {
        _host?.Dispose();
    }

    internal void ConfigureConventions(Action<AzureServiceBusBroadcastingMessageRoutingConvention> configure)
    {
        _host = Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseAzureServiceBusTesting()
                    .UseBroadcastingConventionRouting(c => configure(c.IncludeTypes(t => t == typeof(BroadcastedMessage))
                        .SubscriptionNameForListener(t => "tests")))
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