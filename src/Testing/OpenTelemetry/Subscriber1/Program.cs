using JasperFx;
using OpenTelemetry;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using OtelMessages;
using Subscriber1;
using Wolverine;
using Wolverine.RabbitMQ;
using Wolverine.Transports.Tcp;

#region sample_bootstrapping_headless_service
return await Host.CreateDefaultBuilder(args)
    .UseWolverine(opts =>
    {
        opts.ServiceName = "Subscriber1";

        opts.Discovery.DisableConventionalDiscovery().IncludeType<Subscriber1Handlers>();

        opts.ListenAtPort(MessagingConstants.Subscriber1Port);

        opts.UseRabbitMq().AutoProvision();

        opts.ListenToRabbitQueue(MessagingConstants.Subscriber1Queue);

        // Publish to the other subscriber
        opts.PublishMessage<RabbitMessage2>().ToRabbitQueue(MessagingConstants.Subscriber2Queue);

        // Add Open Telemetry tracing
        opts.Services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService("Subscriber1"))
            .WithTracing(tracing =>
            {
                // Add Wolverine as a source
                tracing.AddSource("Wolverine");
            })
            .UseOtlpExporter();
    })

    // Executing with JasperFx as the command line parser to unlock
    // quite a few utilities and diagnostics in our Wolverine application
    .RunJasperFxCommands(args);

#endregion