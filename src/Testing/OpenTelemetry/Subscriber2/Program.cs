using JasperFx;
using OpenTelemetry;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using OtelMessages;
using Subscriber1;
using Wolverine;
using Wolverine.RabbitMQ;

return await Host.CreateDefaultBuilder(args)
    .UseWolverine(opts =>
    {
        opts.ServiceName = "Subscriber2";

        opts.Discovery.DisableConventionalDiscovery().IncludeType<Subscriber2Handlers>();

        opts.UseRabbitMq().AutoProvision();

        opts.ListenToRabbitQueue(MessagingConstants.Subscriber2Queue);

        // Publish to the same subscriber
        opts.PublishMessage<RabbitMessage3>().ToRabbitQueue(MessagingConstants.Subscriber2Queue);

        opts.Services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService("Subscriber2"))
            .WithTracing(tracing => tracing.AddSource("Wolverine"))
            .UseOtlpExporter();
    })
    .RunJasperFxCommands(args);