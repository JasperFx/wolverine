using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.ComplianceTests;
using Wolverine.RabbitMQ.Internal;
using Wolverine.Runtime;
using Wolverine.Runtime.Routing;

namespace Wolverine.RabbitMQ.Tests.ConventionalRouting;

public static class ConventionalRoutingTestDefaults
{
    public static bool RoutingMessageOnly(Type type) => type == typeof(ConventionallyRoutedMessage);
}


public abstract class ConventionalRoutingContext : IDisposable
{
    private IHost _host = null!;

    internal bool DisableListenerDiscovery { get; set; }

    internal async Task<IWolverineRuntime> theRuntime()
    {
        if (_host == null)
        {
            _host = await WolverineHost.ForAsync(opts =>
            {
                opts.UseRabbitMq().UseConventionalRouting().AutoProvision().AutoPurgeOnStartup();

                if (DisableListenerDiscovery)
                {
                    opts.Discovery.DisableConventionalDiscovery();
                }
            });
        }

        return _host.Services.GetRequiredService<IWolverineRuntime>();
    }

    internal async Task<RabbitMqTransport> theTransport()
    {
        if (_host == null)
        {
            _host = await WolverineHost.ForAsync(opts => opts.UseRabbitMq().UseConventionalRouting());
        }

        var options = _host.Services.GetRequiredService<IWolverineRuntime>().Options;

        return options.RabbitMqTransport();
    }

    public void Dispose()
    {
        _host?.Dispose();
    }

    internal async Task ConfigureConventions(Action<RabbitMqMessageRoutingConvention> configure)
    {
        _host = await WolverineHost.ForAsync(opts =>
        {
            if (DisableListenerDiscovery)
            {
                opts.Discovery.DisableConventionalDiscovery();
            }

            opts.UseRabbitMq().UseConventionalRouting(configure).AutoProvision().AutoPurgeOnStartup();
        });
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
