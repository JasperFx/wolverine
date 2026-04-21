var builder = DistributedApplication.CreateBuilder(args);

// Provision a RabbitMQ container. The name "rabbitmq" becomes the
// connection string key injected into all referenced projects as
// ConnectionStrings__rabbitmq (or ConnectionStrings:rabbitmq in config).
var rabbitmq = builder.AddRabbitMQ("rabbitmq")
    // Expose the RabbitMQ management UI at http://localhost:15672
    .WithManagementPlugin();

builder.AddProject<Projects.Ponger>("ponger")
    .WithReference(rabbitmq)
    // WaitFor ensures Ponger does not start until RabbitMQ is healthy,
    // which means Wolverine's AutoProvision() will reliably succeed.
    .WaitFor(rabbitmq);

builder.AddProject<Projects.Pinger>("pinger")
    .WithReference(rabbitmq)
    .WaitFor(rabbitmq);

await builder.Build().RunAsync();
