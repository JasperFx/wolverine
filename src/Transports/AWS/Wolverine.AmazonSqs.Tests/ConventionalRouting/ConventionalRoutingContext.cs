using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.ComplianceTests;
using Wolverine.Runtime;
using Wolverine.Runtime.Routing;

namespace Wolverine.AmazonSqs.Tests.ConventionalRouting;

/// <summary>
/// Every class in this namespace listens to the same small set of LocalStack queues -- most of them
/// to <c>sqs://routed</c>. A host that is disposed but never stopped keeps long-polling that queue,
/// so it steals messages from whichever class runs next and the theft looks like flakiness. See
/// GH-3763.
///
/// This type owns disposal for that reason: derived classes override <see cref="InitializeAsync"/>
/// rather than re-declaring <see cref="IAsyncLifetime"/> themselves. Re-declaring it is what let a
/// no-op <c>DisposeAsync</c> shadow the real one here and in the Azure Service Bus fixtures
/// (GH-3758) -- an explicit interface implementation on a derived class silently wins.
/// </summary>
public abstract class ConventionalRoutingContext : IAsyncLifetime
{
    private IHost _host = null!;

    public virtual ValueTask InitializeAsync() => ValueTask.CompletedTask;

    internal async Task<IWolverineRuntime> theRuntime()
    {
        _host ??= await WolverineHost.ForAsync(opts =>
            opts.UseAmazonSqsTransportLocally().UseConventionalRouting(leaveTheEndToEndQueueAlone).AutoProvision()
                .AutoPurgeOnStartup());

        return _host.Services.GetRequiredService<IWolverineRuntime>();
    }

    /// <summary>
    /// These classes only assert on configuration, but they still stand up real listeners for every
    /// handler in the assembly -- which would include the queue end_to_end_with_conventional_routing
    /// is trying to receive on, in a different worker process. An ExcludeTypes rather than an
    /// IncludeTypes: excludes are ANDed and includes are ORed, so an include would silently widen a
    /// test's own filter in ConfigureConventions. See GH-3763.
    /// </summary>
    private static void leaveTheEndToEndQueueAlone(AmazonSqsMessageRoutingConvention convention)
    {
        convention.ExcludeTypes(t => t == typeof(EndToEndRoutedMessage));
    }

    public async ValueTask DisposeAsync()
    {
        if (_host == null) return;

        // StopAsync, not just Dispose: IHost.Dispose() tears down the container without ever
        // running IHostedService.StopAsync, which leaves the SQS listeners polling.
        await _host.StopAsync();
        _host.Dispose();
        _host = null!;
    }

    internal async Task ConfigureConventions(Action<AmazonSqsMessageRoutingConvention> configure)
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseAmazonSqsTransportLocally().UseConventionalRouting(c =>
                    {
                        leaveTheEndToEndQueueAlone(c);
                        configure(c);
                    }).AutoProvision()
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
