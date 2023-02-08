using Alba;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using OtelMessages;
using Subscriber1;
using Wolverine;
using Wolverine.RabbitMQ;
using Wolverine.Transports.Tcp;

namespace TracingTests;

public class HostsFixture : IAsyncLifetime
{
    public IHost FirstSubscriber { get; private set; }
    public IHost SecondSubscriber { get; private set; }
    public IAlbaHost WebApi { get; private set; }

    public async Task InitializeAsync()
    {
        WebApi = await AlbaHost.For<Program>(x => { });

        FirstSubscriber = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.ServiceName = "Subscriber1";
                opts.ApplicationAssembly = GetType().Assembly;

                opts.Policies.Discovery(source =>
                {
                    source.DisableConventionalDiscovery();
                    source.IncludeType<Subscriber1Handlers>();
                });

                opts.ListenAtPort(MessagingConstants.Subscriber1Port);

                opts.UseRabbitMq().AutoProvision();

                opts.ListenToRabbitQueue(MessagingConstants.Subscriber1Queue);

                // Publish to the other subscriber
                opts.PublishMessage<RabbitMessage2>().ToRabbitQueue(MessagingConstants.Subscriber2Queue);


                opts.Services.AddOpenTelemetryTracing(builder =>
                {
                    builder
                        .SetResourceBuilder(ResourceBuilder
                            .CreateDefault()
                            .AddService("Subscriber1"))
                        .AddJaegerExporter()
                        .AddSource("Wolverine");
                });
            }).StartAsync();

        SecondSubscriber = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.ServiceName = "Subscriber2";
                opts.ApplicationAssembly = GetType().Assembly;

                opts.Policies.Discovery(source =>
                {
                    source.DisableConventionalDiscovery();
                    source.IncludeType<Subscriber2Handlers>();
                });

                opts.UseRabbitMq().AutoProvision();

                opts.ListenToRabbitQueue(MessagingConstants.Subscriber2Queue);

                // Publish to the same subscriber
                opts.PublishMessage<RabbitMessage3>().ToRabbitQueue(MessagingConstants.Subscriber2Queue);

                opts.Services.AddOpenTelemetryTracing(builder =>
                {
                    builder.SetResourceBuilder(ResourceBuilder
                            .CreateDefault()
                            .AddService("Subscriber2"))
                        .AddJaegerExporter()
                        .AddSource("Wolverine");
                });
            }).StartAsync();
    }

    public async Task DisposeAsync()
    {
        // await WebApi.DisposeAsync();
        // await FirstSubscriber.StopAsync();
        await SecondSubscriber.StopAsync();
    }
}