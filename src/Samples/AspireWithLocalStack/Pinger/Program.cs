using AspireWithLocalStack;
using Wolverine;
using Wolverine.AmazonSns;
using Wolverine.AmazonSqs;

#region sample_aspire_localstack_service_setup
var builder = WebApplication.CreateBuilder(args);

// The shared Aspire service defaults: OpenTelemetry for Wolverine, exported to the Aspire
// dashboard, plus the default health checks
builder.AddServiceDefaults();

builder.UseWolverine(opts =>
{
    // Both read the "aws" connection string, which the AppHost's AddLocalStack("aws") +
    // WithReference(aws) injected as LocalStack's endpoint
    opts.UseAmazonSqsTransportUsingNamedConnection("aws").AutoProvision();
    opts.UseAmazonSnsTransportUsingNamedConnection("aws").AutoProvision();

    // Ping goes through an SQS queue
    opts.PublishMessage<Ping>().ToSqsQueue("pings");
    opts.ListenToSqsQueue("pings");

    // Pong goes through an SNS topic, which delivers it to an SQS queue
    opts.PublishMessage<Pong>().ToSnsTopic("pongs").SubscribeSqsQueue("pongs");
    opts.ListenToSqsQueue("pongs").ReceiveSnsTopicMessage();
});
#endregion

builder.Services.AddSingleton<PingTally>();

var app = builder.Build();

// /health and /alive, which the AppHost polls through WithHttpHealthCheck()
app.MapDefaultEndpoints();

// Send a ping through SQS, and report how many pongs have come back through SNS
app.MapPost("/pings", async (IMessageBus bus) =>
{
    var ping = new Ping(Guid.NewGuid());
    await bus.PublishAsync(ping);
    return ping.Id;
});

app.MapGet("/pings", (PingTally tally) => tally.Count);

await app.RunAsync();
