using AspireWithPubsub;
using Wolverine;
using Wolverine.Pubsub;

#region sample_aspire_pubsub_service_setup
var builder = WebApplication.CreateBuilder(args);

// The shared Aspire service defaults: OpenTelemetry for Wolverine, exported to the Aspire
// dashboard, plus the default health checks
builder.AddServiceDefaults();

builder.UseWolverine(opts =>
{
    // Reads the "pubsub" connection string, which the AppHost's AddPubsubEmulator("pubsub") +
    // WithReference(pubsub) injected as the emulator's endpoint. The project id is the one the
    // AppHost started the emulator with.
    opts.UsePubsubUsingNamedConnection("wolverine", "pubsub")
        .AutoProvision();

    opts.PublishMessage<Ping>().ToPubsubTopic("pings");
    opts.ListenToPubsubTopic("pings");
});
#endregion

builder.Services.AddSingleton<PingTally>();

var app = builder.Build();

// /health and /alive, which the AppHost polls through WithHttpHealthCheck()
app.MapDefaultEndpoints();

// Publish a ping through Pub/Sub, and report how many this service has handled
app.MapPost("/pings", async (IMessageBus bus) =>
{
    var ping = new Ping(Guid.NewGuid());
    await bus.PublishAsync(ping);
    return ping.Id;
});

app.MapGet("/pings", (PingTally tally) => tally.Count);

await app.RunAsync();
