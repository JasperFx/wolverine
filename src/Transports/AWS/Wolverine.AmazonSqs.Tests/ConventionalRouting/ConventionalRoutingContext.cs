using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.ComplianceTests;
using Wolverine.Runtime;
using Wolverine.Runtime.Routing;

namespace Wolverine.AmazonSqs.Tests.ConventionalRouting;

public abstract class ConventionalRoutingContext : IDisposable
{
    private IHost _host;

    internal IWolverineRuntime theRuntime
    {
        get
        {
            _host ??= WolverineHost.For(opts =>
                opts.UseAmazonSqsTransport().UseConventionalRouting().AutoProvision().AutoPurgeOnStartup());

            return _host.Services.GetRequiredService<IWolverineRuntime>();
        }
    }

    public void Dispose()
    {
        _host?.Dispose();
    }

    internal void ConfigureConventions(Action<AmazonSqsMessageRoutingConvention> configure)
    {
        _host = Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseAmazonSqsTransport().UseConventionalRouting(configure).AutoProvision()
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