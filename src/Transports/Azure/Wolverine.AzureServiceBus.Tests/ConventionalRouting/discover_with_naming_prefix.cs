using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.Runtime;
using Xunit;
namespace Wolverine.AzureServiceBus.Tests.ConventionalRouting;

// GH-3786: NOT flaky -- 1 of 1 fails, 2.4m, deterministically, on a clean emulator (2.0.1) with the
// GH-3783 readiness gate. BrokerInitializationException "Unable to initialize the Broker asb in
// time". Re-tagged with the numbers rather than left bare; untag when GH-3786 is fixed.
[Trait("Category", "Flaky")]
public class discover_with_naming_prefix : IDisposable
{
    private readonly IHost _host;
    private readonly ITestOutputHelper _output;

    public discover_with_naming_prefix(ITestOutputHelper output)
    {
        _output = output;
        _host = Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseAzureServiceBusTesting().PrefixIdentifiers("zztop").UseConventionalRouting().AutoProvision()
                    .AutoPurgeOnStartup();
            }).Start();
    }

    public void Dispose()
    {
        _host.Dispose();
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