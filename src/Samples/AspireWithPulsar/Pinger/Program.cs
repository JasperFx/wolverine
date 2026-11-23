using AspireWithPulsar;
using Wolverine;
using Wolverine.Pulsar;

#region sample_aspire_pulsar_service_setup
var builder = WebApplication.CreateBuilder(args);

// The shared Aspire service defaults: OpenTelemetry for Wolverine, exported to the Aspire
// dashboard, plus the default health checks
builder.AddServiceDefaults();

builder.UseWolverine(opts =>
{
    // Reads the "pulsar" connection string, which the AppHost's AddPulsar("pulsar") +
    // WithReference(pulsar) injected as the broker's service URL
    opts.UsePulsarUsingNamedConnection("pulsar");

    opts.PublishMessage<Ping>().ToPulsarTopic("persistent://public/default/pings");

    // This service publishes to the topic it listens to. A brand-new subscription starts at the
    // latest message by default, so a ping published before the subscription is established on
    // the broker would never be delivered to it. Wolverine waits a bounded 10 seconds for that at
    // startup, and a broker that has only just come up can take longer, so start from the earliest
    // retained message instead. This only matters the first time the subscription is created.
    opts.ListenToPulsarTopic("persistent://public/default/pings").BeginAtEarliest();
});
#endregion

builder.Services.AddSingleton<PingTally>();

var app = builder.Build();

// /health and /alive, which the AppHost polls through WithHttpHealthCheck()
app.MapDefaultEndpoints();

// Publish a ping through Pulsar, and report how many this service has handled
app.MapPost("/pings", async (IMessageBus bus) =>
{
    var ping = new Ping(Guid.NewGuid());
    await bus.PublishAsync(ping);
    return ping.Id;
});

app.MapGet("/pings", (PingTally tally) => tally.Count);

await app.RunAsync();
