using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.Runtime;
using Wolverine.Runtime.Routing;
using Xunit;

namespace Wolverine.AzureServiceBus.Tests.ConventionalRouting;

/// <summary>
/// One conventionally-routed host, shared by every test in a class via IClassFixture.
///
/// Most of these classes assert several things about the SAME host — four facts about one discovered
/// listener, three about one discovered sender. Building that host per test meant starting Wolverine,
/// provisioning queues and purging the emulator once per assertion, which was most of what restoring
/// these tests cost CIAzureServiceBus. Sharing is safe here precisely because the assertions only
/// read runtime state; classes whose tests each need a DIFFERENT convention (see
/// <see cref="conventional_listener_discovery"/>) still build a host per test through
/// <see cref="ConventionalRoutingContext"/>. See GH-3786.
/// </summary>
public abstract class ConventionalRoutingFixture : IAsyncLifetime
{
    private IHost _host = null!;

    /// <summary>
    /// Applied on top of <see cref="ConventionalRoutingContext.NarrowToTypesUnderTest"/>. The default
    /// is the plain convention with no overrides.
    /// </summary>
    protected virtual void configure(AzureServiceBusMessageRoutingConvention convention)
    {
    }

    public async ValueTask InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseAzureServiceBusTesting().UseConventionalRouting(convention =>
                {
                    ConventionalRoutingContext.NarrowToTypesUnderTest(convention);
                    configure(convention);
                }).AutoProvision().AutoPurgeOnStartup();
            }).StartAsync();
    }

    // Hands the next class a clean namespace: the emulator caps one at 50 queues and reports the
    // overflow as a broker-initialization timeout that never mentions a cap.
    public async ValueTask DisposeAsync()
    {
        if (_host != null) await _host.StopAsync();
        _host?.Dispose();
        await AzureServiceBusTesting.DeleteAllEmulatorObjectsAsync();
    }

    internal IWolverineRuntime theRuntime() => _host.Services.GetRequiredService<IWolverineRuntime>();

    internal IMessageRouter RoutingFor<T>() => theRuntime().RoutingFor(typeof(T));

    internal IMessageRoute[] PublishingRoutesFor<T>()
        => RoutingFor<T>().ShouldBeOfType<MessageRouter<T>>().Routes;
}

/// <summary>The convention with no overrides at all.</summary>
public class DefaultConventionalRoutingFixture : ConventionalRoutingFixture;

/// <summary>The convention with the listener queue name overridden.</summary>
public class OverriddenQueueNamingFixture : ConventionalRoutingFixture
{
    protected override void configure(AzureServiceBusMessageRoutingConvention convention)
        => convention.QueueNameForListener(t => t.Name.ToLower() + "2");
}
