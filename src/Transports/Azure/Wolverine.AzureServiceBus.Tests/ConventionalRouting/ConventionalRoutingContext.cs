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

    /// <summary>
    /// The only message types these tests actually assert on. Unrestricted conventional routing
    /// discovers every handled message type in this assembly and asks Azure Service Bus for 28
    /// queues per host — and the emulator caps a namespace at 50, so two test classes in one process
    /// hit "SubCode=40300. The maximum number of resources of type Queue has been reached. Actual: 50,
    /// Max allowed: 50" and everything after that reads as a broker-initialization timeout. It is
    /// also most of the wall clock: provisioning and purging 28 queues per test ran these classes at
    /// roughly 100 seconds per test. See GH-3786.
    /// </summary>
    private static bool isNotUnderTest(Type type)
        => type != typeof(RoutedMessage) && type != typeof(Routed2Message) && type != typeof(PublishedMessage);

    /// <summary>
    /// Narrows the convention to <see cref="isNotUnderTest"/>. Deliberately an EXCLUDE rather than an
    /// include: excludes are ANDed together (CompositeFilter.Matches requires
    /// Excludes.DoesNotMatchAny) whereas includes are ORed, so an include here would silently widen
    /// a test's own IncludeTypes call and make conventional_listener_discovery.include_types pass
    /// whether the include filter works or not.
    /// </summary>
    internal static void NarrowToTypesUnderTest(AzureServiceBusMessageRoutingConvention convention)
        => convention.ExcludeTypes(isNotUnderTest);

    internal async Task<IWolverineRuntime> theRuntime()
    {
        _host ??= await WolverineHost.ForAsync(opts =>
            opts.UseAzureServiceBusTesting().UseConventionalRouting(NarrowToTypesUnderTest).AutoProvision()
                .AutoPurgeOnStartup());

        return _host.Services.GetRequiredService<IWolverineRuntime>();
    }

    public virtual ValueTask InitializeAsync()
    {
        return ValueTask.CompletedTask;
    }

    public virtual async ValueTask DisposeAsync()
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
                opts.UseAzureServiceBusTesting().UseConventionalRouting(convention =>
                {
                    NarrowToTypesUnderTest(convention);
                    configure(convention);
                }).AutoProvision().AutoPurgeOnStartup();
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
