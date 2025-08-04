using JasperFx.Core.Reflection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.Runtime;
using Xunit;

namespace Wolverine.Pulsar.Tests;

public class endpoint_configuration : IDisposable
{
    private readonly IHost _host;
    private readonly IWolverineRuntime theRuntime;

    public endpoint_configuration()
    {
        _host = Host.CreateDefaultBuilder().UseWolverine(opts =>
        {
            opts.UsePulsar();
            opts.DisablePulsarRequeue();
            opts.UnsubscribePulsarOnClose(PulsarUnsubscribeOnClose.Disabled);

            opts.ListenToPulsarTopic("persistent://public/default/one");
        }).Build();

        var theOptions = _host.Get<WolverineOptions>();
        theRuntime = _host.Get<IWolverineRuntime>();

        foreach (var endpoint in theOptions.Transports.AllEndpoints())
        {
            endpoint.Compile(theRuntime);
        }
    }

    public void Dispose()
    {
        _host.Dispose();
    }

    [Fact]
    public void requeue_disabled()
    {
        var uri = PulsarEndpoint.UriFor("persistent://public/default/one");
        var endpoint = theRuntime.Endpoints.EndpointFor(uri)?.As<PulsarEndpoint>();

        endpoint.EnableRequeue.ShouldBeFalse();
    }

    [Fact]
    public void unsubscribe_on_close_disabled()
    {
        var uri = PulsarEndpoint.UriFor("persistent://public/default/one");
        var endpoint = theRuntime.Endpoints.EndpointFor(uri)?.As<PulsarEndpoint>();

        endpoint.UnsubscribeOnClose.ShouldBeFalse();
    }
}
