using AspireWithRabbitMq;
using Wolverine;
using Wolverine.RabbitMQ;

#region sample_aspire_rabbitmq_ponger_setup
var builder = WebApplication.CreateBuilder(args);

// The shared Aspire service defaults: OpenTelemetry for Wolverine's "Wolverine" ActivitySource
// and "Wolverine*" Meters, exported to the Aspire dashboard, plus the default health checks
builder.AddServiceDefaults();

builder.UseWolverine(opts =>
{
    // UseRabbitMqUsingNamedConnection reads the "rabbitmq" connection string from
    // IConfiguration. .NET Aspire injects this automatically via the WithReference()
    // call in the AppHost when you run AddRabbitMQ("rabbitmq").
    opts.UseRabbitMqUsingNamedConnection("rabbitmq")
        // AutoProvision directs Wolverine to create any missing exchanges, queues,
        // or bindings at startup. Combined with Aspire's WaitFor(), the RabbitMQ
        // broker is guaranteed to be healthy before this runs.
        .AutoProvision()
        .DeclareExchange("pings", ex => ex.BindQueue("pings"))
        .DeclareQueue("pongs");

    // Listen for incoming ping messages
    opts.ListenToRabbitQueue("pings");

    // Send pong replies back via the pongs queue
    opts.PublishMessage<PongMessage>().ToRabbitQueue("pongs");
});

var app = builder.Build();

// /health and /alive, which the AppHost polls through WithHttpHealthCheck()
app.MapDefaultEndpoints();

await app.RunAsync();
#endregion
