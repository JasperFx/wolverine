using AspireWithKafka;
using Wolverine;
using Wolverine.Kafka;

#region sample_aspire_kafka_publisher_setup
var builder = WebApplication.CreateBuilder(args);

// The shared Aspire service defaults: OpenTelemetry for Wolverine's "Wolverine" ActivitySource
// and "Wolverine*" Meters, exported to the Aspire dashboard, plus the default health checks
builder.AddServiceDefaults();

builder.UseWolverine(opts =>
{
    // UseKafkaUsingNamedConnection reads the "kafka" connection string from
    // IConfiguration. .NET Aspire injects this automatically via the WithReference()
    // call in the AppHost when you run AddKafka("kafka"). The value is the
    // Kafka bootstrap servers address.
    opts.UseKafkaUsingNamedConnection("kafka")
        // AutoProvision directs Wolverine to create any missing Kafka topics at startup.
        // Combined with Aspire's WaitFor(), the Kafka broker is guaranteed healthy first.
        .AutoProvision();

    // Publish OrderPlaced messages to the "orders" topic
    opts.PublishMessage<OrderPlaced>().ToKafkaTopic("orders");
});

builder.Services.AddHostedService<PublisherService>();

var app = builder.Build();

// /health and /alive, which the AppHost polls through WithHttpHealthCheck()
app.MapDefaultEndpoints();

await app.RunAsync();
#endregion
