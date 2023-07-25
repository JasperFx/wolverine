using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.Runtime;
using Xunit;
using Xunit.Abstractions;

namespace Wolverine.AzureServiceBus.Tests.ConventionalRouting.New;

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
                opts.UseAzureServiceBusTesting().PrefixIdentifiers("zztop")
                    .UseConventionalRoutingWithBroadcast(c => c.UseBroadcastingFor(t => t == typeof(BroadcastedMessage), t => "test"))
                    .AutoProvision().AutoPurgeOnStartup();
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
        uris.ShouldContain(new Uri("asb://queue/zztop.newrouted"));
        uris.ShouldContain(new Uri("asb://topic/zztop.broadcasted/test"));
        uris.ShouldContain(new Uri("asb://queue/zztop.Wolverine.AzureServiceBus.Tests.AsbMessage"));
    }
}