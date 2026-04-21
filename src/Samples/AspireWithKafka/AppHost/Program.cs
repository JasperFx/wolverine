var builder = DistributedApplication.CreateBuilder(args);

// Provision a Kafka container. The name "kafka" becomes the
// connection string key (the bootstrap servers address) injected into
// all referenced projects as ConnectionStrings__kafka.
var kafka = builder.AddKafka("kafka")
    // Expose the Kafka UI at http://localhost:8080
    .WithKafkaUI();

builder.AddProject<Projects.Consumer>("consumer")
    .WithReference(kafka)
    // WaitFor ensures Consumer does not start until Kafka is healthy,
    // which means Wolverine's AutoProvision() will reliably succeed.
    .WaitFor(kafka);

builder.AddProject<Projects.Publisher>("publisher")
    .WithReference(kafka)
    .WaitFor(kafka);

await builder.Build().RunAsync();
