using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.Runtime;
using Xunit;

namespace Wolverine.RabbitMQ.Tests.ConventionalRouting;

// https://github.com/JasperFx/wolverine/issues/3633
public class Bug_3633_conventional_routing_respects_named_broker : IDisposable
{
    private static readonly BrokerName theBrokerName = new("other");
    private readonly IHost _host;

    public Bug_3633_conventional_routing_respects_named_broker()
    {
        _host = Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                // A default, unnamed broker is also registered so that conventional
                // routing has somewhere wrong to go if the named broker is ignored
                opts.UseRabbitMq();

                opts.AddNamedRabbitMqBroker(theBrokerName, factory => { })
                    .UseConventionalRouting(x => x.IncludeTypes(ConventionalRoutingTestDefaults.RoutingMessageOnly))
                    .AutoProvision()
                    .AutoPurgeOnStartup();
            }).Start();
    }

    public void Dispose()
    {
        _host.Dispose();
    }

    [Fact]
    public void discovers_listener_against_the_named_broker_not_the_default()
    {
        var runtime = _host.Services.GetRequiredService<IWolverineRuntime>();

        var uris = runtime.Endpoints.ActiveListeners().Select(x => x.Uri).ToArray();

        uris.ShouldContain(new Uri("other://queue/routed"));
        uris.ShouldNotContain(new Uri("rabbitmq://queue/routed"));
    }
}
