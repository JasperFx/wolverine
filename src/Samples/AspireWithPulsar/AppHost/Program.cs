#region sample_aspire_pulsar_apphost
var builder = DistributedApplication.CreateBuilder(args);

// A Pulsar broker, run as `bin/pulsar standalone` in a container. The name "pulsar" becomes the
// connection string injected into every project that references it, ConnectionStrings__pulsar,
// and its value is the broker's service URL, pulsar://{host}:{port}
var pulsar = builder.AddPulsar("pulsar");

builder.AddProject<Projects.AspireWithPulsar_Pinger>("pinger")
    .WithReference(pulsar)
    // The pinger does not start until the broker's health endpoint answers
    .WaitFor(pulsar)
    .WithHttpHealthCheck("/health");

await builder.Build().RunAsync();
#endregion
