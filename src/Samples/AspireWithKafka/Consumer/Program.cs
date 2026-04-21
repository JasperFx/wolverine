using Wolverine;
using Wolverine.Kafka;

#region sample_aspire_kafka_consumer_setup
var builder = Host.CreateApplicationBuilder(args);

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

    // Listen for OrderPlaced messages on the "orders" topic
    opts.ListenToKafkaTopic("orders")
        .ProcessInline();
});

await builder.Build().RunAsync();
#endregion
