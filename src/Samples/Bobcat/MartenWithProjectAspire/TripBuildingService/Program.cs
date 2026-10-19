using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry;

var builder = Host.CreateApplicationBuilder();

// Register the NpgsqlDataSource in the IoC container using
// connection string named "marten" from IConfiguration
builder.AddNpgsqlDataSource("marten");

builder.Logging.AddOpenTelemetry(logging =>
{
    logging.IncludeFormattedMessage = true;
    logging.IncludeScopes = true;
});

builder.Services.AddOpenTelemetry().UseOtlpExporter();

builder.Services.AddOpenTelemetry()
    .WithTracing(tracing => { tracing.AddSource("Marten"); })
    .WithMetrics(metrics => { metrics.AddMeter("Marten"); });

builder.Services.AddMarten(opts =>
{
    opts.DatabaseSchemaName = "cli";

    // Register all event store projections ahead of time
    opts.Projections
        .Add(new TripProjection(), ProjectionLifecycle.Async);

    opts.Projections
        .Add(new DayProjection(), ProjectionLifecycle.Async);

    opts.Projections
        .Add(new DistanceProjection(), ProjectionLifecycle.Async);
}).AddAsyncDaemon(DaemonMode.Solo)
    
    // Use PostgreSQL data source from the IoC container
    .UseNpgsqlDataSource();

await builder.Build().RunAsync();