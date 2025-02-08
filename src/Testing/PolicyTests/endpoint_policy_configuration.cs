using IntegrationTests;
using JasperFx.Core;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using JasperFx.Resources;
using Shouldly;
using Wolverine.ComplianceTests;
using Wolverine.ComplianceTests.Compliance;
using Wolverine;
using Wolverine.Configuration;
using Wolverine.Marten;
using Wolverine.RabbitMQ;
using Wolverine.Runtime;
using Wolverine.Transports;
using Wolverine.Transports.Local;
using Wolverine.Transports.Tcp;
using Wolverine.Util;

namespace PolicyTests;

public class endpoint_policy_configuration : IAsyncDisposable
{
    private IHost _host;

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    private async Task<WolverineOptions> UsingOptions(Action<WolverineOptions> configure)
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Services.AddMarten(Servers.PostgresConnectionString)
                    .IntegrateWithWolverine();

                opts.Services.AddResourceSetupOnStartup();

                configure(opts);
            }).StartAsync();

        return _host.Services.GetRequiredService<IWolverineRuntime>().Options;
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

        options.Transports.AllEndpoints().OfType<LocalQueue>().Where(x => x.Role == EndpointRole.Application)
            .Each(x => x.Mode.ShouldBe(EndpointMode.Durable));

        options.Transports.AllEndpoints().OfType<TcpEndpoint>()
            .Each(x => x.Mode.ShouldBe(EndpointMode.BufferedInMemory));


    }

    [Fact]
    public async Task make_all_local_queues_durable_does_not_impact_system_queues()
    {
        var options = await UsingOptions(opts =>
        {
            opts.LocalQueue("one");
            opts.LocalQueue("two");
            opts.LocalQueue("three");


            opts.Policies.UseDurableLocalQueues();
        });

        options.Transports.AllEndpoints().OfType<LocalQueue>().Where(x => x.Role == EndpointRole.Application)
            .Each(x => x.Mode.ShouldBe(EndpointMode.Durable));

        var queues = options.Transports.AllEndpoints().OfType<LocalQueue>().ToDictionary(x => x.EndpointName);

        queues[TransportConstants.Agents].Mode.ShouldBe(EndpointMode.BufferedInMemory);
        queues[TransportConstants.Durable].Mode.ShouldBe(EndpointMode.Durable);


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
        options.Transports.AllEndpoints().OfType<LocalQueue>()
            .Where(x => x.Role == EndpointRole.Application)
            .Each(x => x.Mode.ShouldBe(EndpointMode.BufferedInMemory));


        options.Transports.AllEndpoints().Each(e =>
        {
            if (e is LocalQueue)
            {
                return;
            }

            if (e.Role == EndpointRole.System) return;

            if (e.EndpointName.Contains("dead-letter-queue")) return;

            if (e.IsListener)
            {
                e.Mode.ShouldNotBe(EndpointMode.Durable);
            }
            else
            {
                e.Mode.ShouldBe(EndpointMode.Durable, $"Endpoint: {e}, Uri: {e.Uri}");
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
        options.Transports.AllEndpoints().OfType<LocalQueue>()
            .Where(x => x.Role == EndpointRole.Application)
            .Each(x => x.Mode.ShouldBe(EndpointMode.BufferedInMemory));


        options.Transports.AllEndpoints().Each(e =>
        {
            if (e is LocalQueue)
            {
                return;
            }

            if (e.IsListener && e.Role == EndpointRole.Application)
            {
                e.Mode.ShouldBe(EndpointMode.Durable, e.Uri.ToString());
            }
            else
            {
                e.Mode.ShouldNotBe(EndpointMode.Durable);
            }
        });
    }

    [Fact]
    public async Task apply_to_rabbit_mq_listeners()
    {
        var options = await UsingOptions(opts =>
        {
            opts.ListenAtPort(PortFinder.GetAvailablePort());
            opts.PublishAllMessages().ToPort(PortFinder.GetAvailablePort());

            opts.UseRabbitMq().AutoProvision()
                .ConfigureSenders(x => x.UseDurableOutbox());

            opts.ListenToRabbitQueue("one");
            opts.PublishAllMessages().ToRabbitExchange("ex2");
        });

        options.Transports.GetOrCreateEndpoint("rabbitmq://queue/one".ToUri())
            .Mode.ShouldBe(EndpointMode.Inline);

        options.Transports.GetOrCreateEndpoint("rabbitmq://exchange/ex2".ToUri())
            .Mode.ShouldBe(EndpointMode.Durable);

        foreach (var endpoint in options.Transports.AllEndpoints().OfType<TcpEndpoint>())
            endpoint.Mode.ShouldBe(EndpointMode.BufferedInMemory);
    }

    [Fact]
    public async Task apply_to_rabbit_mq_senders()
    {
        var options = await UsingOptions(opts =>
        {
            opts.ListenAtPort(PortFinder.GetAvailablePort());
            opts.PublishAllMessages().ToPort(PortFinder.GetAvailablePort());

            opts.UseRabbitMq().AutoProvision()
                .ConfigureSenders(x => x.UseDurableOutbox());

            opts.ListenToRabbitQueue("one");
            opts.PublishAllMessages().ToRabbitExchange("ex2");
        });

        options.Transports.GetOrCreateEndpoint("rabbitmq://queue/one".ToUri())
            .Mode.ShouldBe(EndpointMode.Inline);

        options.Transports.GetOrCreateEndpoint("rabbitmq://exchange/ex2".ToUri())
            .Mode.ShouldBe(EndpointMode.Durable);

        foreach (var endpoint in options.Transports.AllEndpoints().OfType<TcpEndpoint>())
            endpoint.Mode.ShouldBe(EndpointMode.BufferedInMemory);
    }

    [Fact]
    public async Task discover_local_endpoints_with_default_name_pattern()
    {
        var options = await UsingOptions(opts =>
        {
            opts.Policies.ConfigureConventionalLocalRouting()
                .CustomizeQueues((type, listener) => { listener.UseDurableInbox(); });
        });

        var runtime = _host.Services.GetRequiredService<IWolverineRuntime>()
            .ShouldBeOfType<WolverineRuntime>();

        var endpoint1 = runtime.DetermineLocalSendingAgent(typeof(Message1))
            .Endpoint.ShouldBeOfType<LocalQueue>();
        endpoint1.EndpointName.ShouldBe(typeof(Message1).ToMessageTypeName().ToLowerInvariant());
        endpoint1.Mode.ShouldBe(EndpointMode.Durable);

        var endpoint2 = runtime.DetermineLocalSendingAgent(typeof(Message2))
            .Endpoint.ShouldBeOfType<LocalQueue>();
        endpoint2.EndpointName.ShouldBe(typeof(Message2).ToMessageTypeName().ToLowerInvariant());
        endpoint2.Mode.ShouldBe(EndpointMode.Durable);

        runtime.DetermineLocalSendingAgent(typeof(Message3))
            .Endpoint.ShouldBeOfType<LocalQueue>().EndpointName
            .ShouldBe(typeof(Message3).ToMessageTypeName().ToLowerInvariant());

        runtime.DetermineLocalSendingAgent(typeof(Message4))
            .Endpoint.ShouldBeOfType<LocalQueue>().EndpointName
            .ShouldBe(typeof(Message4).ToMessageTypeName().ToLowerInvariant());
    }

    [Fact]
    public async Task discover_local_endpoints_with_custom_name_pattern()
    {
        var options = await UsingOptions(opts =>
        {
            opts.Policies.ConfigureConventionalLocalRouting()
                .Named(t => t.ToMessageTypeName() + "_more")
                .CustomizeQueues((type, listener) => { listener.UseDurableInbox(); });
        });

        var runtime = _host.Services.GetRequiredService<IWolverineRuntime>()
            .ShouldBeOfType<WolverineRuntime>();

        var endpoint1 = runtime.DetermineLocalSendingAgent(typeof(Message1))
            .Endpoint.ShouldBeOfType<LocalQueue>();
        endpoint1.EndpointName.ShouldBe(typeof(Message1).ToMessageTypeName().ToLowerInvariant() + "_more");
        endpoint1.Mode.ShouldBe(EndpointMode.Durable);

        var endpoint2 = runtime.DetermineLocalSendingAgent(typeof(Message2))
            .Endpoint.ShouldBeOfType<LocalQueue>();
        endpoint2.EndpointName.ShouldBe(typeof(Message2).ToMessageTypeName().ToLowerInvariant() + "_more");
        endpoint2.Mode.ShouldBe(EndpointMode.Durable);

        runtime.DetermineLocalSendingAgent(typeof(Message3))
            .Endpoint.ShouldBeOfType<LocalQueue>().EndpointName
            .ShouldBe(typeof(Message3).ToMessageTypeName().ToLowerInvariant() + "_more");

        runtime.DetermineLocalSendingAgent(typeof(Message4))
            .Endpoint.ShouldBeOfType<LocalQueue>().EndpointName
            .ShouldBe(typeof(Message4).ToMessageTypeName().ToLowerInvariant() + "_more");
    }

    [Fact]
    public async Task overriding_message_success_log_level()
    {
        await UsingOptions(opts =>
        {
            opts.Policies.MessageSuccessLogLevel(LogLevel.Debug);
        });

        var runtime = _host.Services.GetRequiredService<IWolverineRuntime>()
            .ShouldBeOfType<WolverineRuntime>();

        foreach (var chain in runtime.Handlers.Chains)
        {
            chain.SuccessLogLevel.ShouldBe(LogLevel.Debug);
        }
    }

    [Fact]
    public async Task overriding_message_execution_log_level()
    {
        await UsingOptions(opts =>
        {
            opts.Policies.MessageSuccessLogLevel(LogLevel.None);
        });

        var runtime = _host.Services.GetRequiredService<IWolverineRuntime>()
            .ShouldBeOfType<WolverineRuntime>();

        foreach (var chain in runtime.Handlers.Chains)
        {
            chain.SuccessLogLevel.ShouldBe(LogLevel.None);
        }
    }
}

public class MessageHandler
{
    public void Handle(Message1 message)
    {
    }

    public void Handle(Message2 message)
    {
    }

    public void Handle(Message3 message)
    {
    }

    public void Handle(Message4 message)
    {
    }
}