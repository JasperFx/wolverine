using EventPublisher;
using JasperFx.OpenTelemetry;
using Marten;
using Marten.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry;

var builder = Host.CreateApplicationBuilder();

// Register the NpgsqlDataSource in the IoC container using
// connection string named "marten" from IConfiguration
builder.AddNpgsqlDataSource("marten");

builder.Logging.AddConsole();

builder.Logging.AddOpenTelemetry(logging =>
{
    logging.IncludeFormattedMessage = true;
    logging.IncludeScopes = true;
});

// The following lines enable exporting Open Telemetry data to an Otlp collector
// Project Aspire pushes in the Uri for the collection through an Environment
// variable if you're wondering like I did where the "magic" comes from
builder.Services.AddOpenTelemetry()
    .UseOtlpExporter()
    
    // Enable exports of Open Telemetry activities for Marten
    .WithTracing(tracing => { tracing.AddSource("Marten"); })

    // Enable exports of metrics for Marten
    .WithMetrics(metrics => { metrics.AddMeter("Marten"); });

builder.Services.AddHostedService<HostedPublisher>();

builder.Services.AddMarten(opts =>
    {
        opts.DatabaseSchemaName = "cli";

        // Turn on Otel tracing for connection activity, and
        // also tag events to each span for all the Marten "write"
        // operations
        opts.OpenTelemetry.TrackConnections = TrackLevel.Verbose;

        opts.OpenTelemetry.TrackEventCounters();
    })
    
    // Use PostgreSQL data source from the IoC container
    .UseNpgsqlDataSource();

builder.Build().Run();