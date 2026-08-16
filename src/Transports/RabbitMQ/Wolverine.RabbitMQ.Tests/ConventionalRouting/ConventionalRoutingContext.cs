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


/// <summary>
///     GH-3965. Note the async disposal. Every conventionally routed <see cref="ConventionallyRoutedMessage" />
///     lands on one FIXED queue -- the type carries <c>[MessageIdentity("routed")]</c>, so the queue is
///     literally <c>routed</c> -- and several classes in this namespace stand up listeners on it.
///     <see cref="IHost.Dispose" /> does NOT run <c>IHostedService.StopAsync</c>, so disposing synchronously
///     left those consumers attached to the broker after the test finished, and a later test's message was
///     delivered to a leaked consumer belonging to an already-completed class. That shows up as a tracked
///     session containing <c>Sent</c> and no <c>Received</c> at all.
/// </summary>
public abstract class ConventionalRoutingContext : IDisposable, IAsyncDisposable
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

    public ValueTask DisposeAsync() => DisposeHostAsync();

    /// <summary>
    ///     Stops the host so its Rabbit consumers are actually cancelled, then disposes it. Derived classes
    ///     that implement <c>IAsyncLifetime</c> shadow the interface implementation above, so they must call
    ///     this from their own <c>DisposeAsync</c> rather than returning a completed ValueTask.
    /// </summary>
    protected async ValueTask DisposeHostAsync()
    {
        if (_host == null) return;

        await _host.StopAsync();
        _host.Dispose();
        _host = null!;
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
