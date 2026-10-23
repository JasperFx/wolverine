using AspireWithRabbitMq;
using Wolverine;
using Wolverine.RabbitMQ;

#region sample_aspire_rabbitmq_pinger_setup
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
        .DeclareExchange("pings", ex =>
        {
            // Declare the exchange and bind the queue so Wolverine auto-creates
            // both at startup via AutoProvision()
            ex.BindQueue("pings");
        });

    // Listen for pong replies coming back from the Ponger service
    opts.ListenToRabbitQueue("pongs");

    // Publish PingMessage to the "pings" exchange
    opts.PublishMessage<PingMessage>().ToRabbitExchange("pings");
});

builder.Services.AddHostedService<PingerService>();

var app = builder.Build();

// /health and /alive, which the AppHost polls through WithHttpHealthCheck()
app.MapDefaultEndpoints();

await app.RunAsync();
#endregion
