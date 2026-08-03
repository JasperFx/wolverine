using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.Runtime;
using Xunit;
namespace Wolverine.AmazonSqs.Tests.ConventionalRouting;

public class discover_with_naming_prefix : IAsyncLifetime
{
    private IHost _host = null!;

    public async ValueTask InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseAmazonSqsTransportLocally().PrefixIdentifiers("zztop").UseConventionalRouting().AutoProvision()
                    .AutoPurgeOnStartup();
            }).StartAsync();
    }

    // StopAsync, not just Dispose -- see the note in ConventionalRoutingContext (GH-3763).
    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    [Fact]
    public void discover_listener_with_prefix()
    {
        var runtime = _host.Services.GetRequiredService<IWolverineRuntime>();

        var uris = runtime.Endpoints.ActiveListeners().Select(x => x.Uri).ToArray();
        uris.ShouldContain(new Uri("sqs://zztop-orderextension-createorder/"));
        uris.ShouldContain(new Uri("sqs://zztop-orderextension-shiporder/"));
        uris.ShouldContain(new Uri("sqs://zztop-routed/"));
    }
}