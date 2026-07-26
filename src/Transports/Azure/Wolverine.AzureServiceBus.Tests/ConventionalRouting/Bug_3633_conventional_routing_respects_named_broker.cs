using IntegrationTests;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.Runtime;
using Xunit;

namespace Wolverine.AzureServiceBus.Tests.ConventionalRouting;

// https://github.com/JasperFx/wolverine/issues/3633
public class Bug_3633_conventional_routing_respects_named_broker : IAsyncLifetime
{
    private static readonly BrokerName theBrokerName = new("other");
    private IHost _host = null!;

    public async Task InitializeAsync()
    {
        await AzureServiceBusTesting.DeleteAllEmulatorObjectsAsync(Servers.AzureServiceBusTenantManagementConnectionString);

        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                // A default, unnamed broker is also registered so that conventional
                // routing has somewhere wrong to go if the named broker is ignored
                opts.UseAzureServiceBusTesting();

                opts.AddNamedAzureServiceBusBroker(theBrokerName, Servers.AzureServiceBusTenantConnectionString)
                    .UseConventionalRouting(x => x.IncludeTypes(t => t == typeof(RoutedMessage)))
                    .AutoProvision()
                    .AutoPurgeOnStartup();
            }).StartAsync();
    }

    public Task DisposeAsync()
    {
        _host.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public void discovers_listener_against_the_named_broker_not_the_default()
    {
        var runtime = _host.Services.GetRequiredService<IWolverineRuntime>();

        var uris = runtime.Endpoints.ActiveListeners().Select(x => x.Uri).ToArray();

        uris.ShouldContain(new Uri("other://queue/routed"));
        uris.ShouldNotContain(new Uri("asb://queue/routed"));
    }
}
