using Baseline;
using IntegrationTests;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using TestingSupport;
using Wolverine;
using Wolverine.Configuration;
using Wolverine.Marten;
using Wolverine.RabbitMQ;
using Wolverine.Runtime;
using Wolverine.Transports;
using Wolverine.Transports.Local;
using Wolverine.Transports.Tcp;

namespace PolicyTests;

public class endpoint_policy_configuration : IDisposable
{
    private IHost _host;

    private async Task<WolverineOptions> UsingOptions(Action<WolverineOptions> configure)
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Services.AddMarten(Servers.PostgresConnectionString)
                    .IntegrateWithWolverine();

                configure(opts);
            }).StartAsync();

        return _host.Services.GetRequiredService<IWolverineRuntime>().Options;
    }

    public void Dispose()
    {
        _host.Dispose();
    }

    [Fact]
    public async Task make_all_local_queues_durable()
    {
        var options = await UsingOptions(opts =>
        {
            opts.LocalQueue("one");
            opts.LocalQueue("two");
            opts.LocalQueue("three");

            opts.ListenAtPort(PortFinder.GetAvailablePort());
            opts.ListenAtPort(PortFinder.GetAvailablePort());
            
            opts.Policies.UseDurableLocalQueues();
        });

        options.Transports.AllEndpoints().OfType<LocalQueueSettings>()
            .Each(x => x.Mode.ShouldBe(EndpointMode.Durable));

        options.Transports.AllEndpoints().OfType<TcpEndpoint>()
            .Each(x => x.Mode.ShouldBe(EndpointMode.BufferedInMemory));
    }

    [Fact]
    public async Task make_all_outgoing_endpoints_durable()
    {
        var options = await UsingOptions(opts =>
        {
            opts.LocalQueue("one");
            opts.LocalQueue("two");
            
            opts.ListenToRabbitQueue("one");
            opts.ListenToRabbitQueue("two");

            opts.ListenAtPort(PortFinder.GetAvailablePort());

            opts.PublishAllMessages().ToPort(PortFinder.GetAvailablePort());
            opts.PublishAllMessages().ToPort(PortFinder.GetAvailablePort());

            opts.PublishAllMessages().ToRabbitExchange("outgoing");

            opts.UseRabbitMq().AutoProvision();
            
            opts.Policies.UseDurableOutboxOnAllSendingEndpoints();
        });

        // Don't touch local endpoints
        options.Transports.AllEndpoints().OfType<LocalQueueSettings>()
            .Where(x => x.Uri != TransportConstants.DurableLocalUri)
            .Each(x => x.Mode.ShouldBe(EndpointMode.BufferedInMemory));


        options.Transports.AllEndpoints().Each(e =>
        {
            if (e is LocalQueueSettings) return;

            if (e.IsListener)
            {
                e.Mode.ShouldNotBe(EndpointMode.Durable);
            }
            else
            {
                e.Mode.ShouldBe(EndpointMode.Durable);
            }
        });
    }
    
    [Fact]
    public async Task make_all_incoming_endpoints_durable()
    {
        var options = await UsingOptions(opts =>
        {
            opts.LocalQueue("one");
            opts.LocalQueue("two");
            
            opts.ListenToRabbitQueue("one");
            opts.ListenToRabbitQueue("two");

            opts.ListenAtPort(PortFinder.GetAvailablePort());

            opts.PublishAllMessages().ToPort(PortFinder.GetAvailablePort());
            opts.PublishAllMessages().ToPort(PortFinder.GetAvailablePort());

            opts.PublishAllMessages().ToRabbitExchange("outgoing");

            opts.UseRabbitMq().AutoProvision();
            
            opts.Policies.UseDurableInboxOnAllListeners();
        });

        // Don't touch local endpoints
        options.Transports.AllEndpoints().OfType<LocalQueueSettings>()
            .Where(x => x.Uri != TransportConstants.DurableLocalUri)
            .Each(x => x.Mode.ShouldBe(EndpointMode.BufferedInMemory));


        options.Transports.AllEndpoints().Each(e =>
        {
            if (e is LocalQueueSettings) return;

            if (e.IsListener)
            {
                e.Mode.ShouldBe(EndpointMode.Durable);
            }
            else
            {
                e.Mode.ShouldNotBe(EndpointMode.Durable);
            }
        });
    }
}