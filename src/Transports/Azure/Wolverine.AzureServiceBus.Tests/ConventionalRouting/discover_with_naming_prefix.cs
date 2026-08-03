using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.Runtime;
using Xunit;
namespace Wolverine.AzureServiceBus.Tests.ConventionalRouting;

public class discover_with_naming_prefix : IAsyncLifetime
{
    private IHost _host = null!;

    public async ValueTask InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseAzureServiceBusTesting().PrefixIdentifiers("zztop")
                    // Only the two types this test asserts on. Unrestricted conventional routing asks
                    // for 28 queues and the emulator caps a namespace at 50 -- see the note on
                    // ConventionalRoutingContext.isNotUnderTest. GH-3786.
                    .UseConventionalRouting(x =>
                        x.ExcludeTypes(t => t != typeof(RoutedMessage) && t != typeof(AsbMessage1)))
                    .AutoProvision()
                    .AutoPurgeOnStartup();
            }).StartAsync();
    }

    // This class provisions PREFIXED queues, so its entities collide with no other test's names and
    // every one of them counts separately against the emulator's namespace cap. It previously only
    // disposed the host and left all of them behind, which is what pushed the classes running after
    // it past 50 queues -- reported as a broker-initialization timeout with no mention of a cap.
    public async ValueTask DisposeAsync()
    {
        if (_host != null) await _host.StopAsync();
        _host?.Dispose();
        await AzureServiceBusTesting.DeleteAllEmulatorObjectsAsync();
    }

    [Fact]
    public void discover_listener_with_prefix()
    {
        var runtime = _host.Services.GetRequiredService<IWolverineRuntime>();

        var uris = runtime.Endpoints.ActiveListeners().Select(x => x.Uri).ToArray();
        uris.ShouldContain(new Uri("asb://queue/zztop.routed"));
        uris.ShouldContain(new Uri("asb://queue/zztop.wolverine.azureservicebus.tests.asbmessage1"));
    }
}
