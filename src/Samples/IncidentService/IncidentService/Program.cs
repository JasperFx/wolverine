using IncidentService;
using Marten;
using Marten.Events.Daemon.Resiliency;
using Marten.Events.Projections;
using JasperFx;
using Wolverine;
using Wolverine.Http;
using Wolverine.Marten;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();

builder.Services.AddMarten(opts =>
{
    var connectionString = builder.Configuration.GetConnectionString("Marten");
    opts.Connection(connectionString);
    opts.DatabaseSchemaName = "incidents";
    
    opts.Projections.Snapshot<Incident>(SnapshotLifecycle.Inline);

    // Use PostgreSQL partitioning
    opts.Events.UseArchivedStreamPartitioning = true;
    
    // Recent optimization
    opts.Projections.UseIdentityMapForAggregates = true;
})
    
// Another performance optimization if you're starting from
// scratch
.UseLightweightSessions()
    
// Run projections in the background
.AddAsyncDaemon(DaemonMode.HotCold)

// This adds configuration with Wolverine's transactional outbox and
// Marten middleware support to Wolverine
.IntegrateWithWolverine();

builder.Host.UseWolverine(opts =>
{
    // This is almost an automatic default to have
    // Wolverine apply transactional middleware to any
    // endpoint or handler that uses persistence services
    opts.Policies.AutoApplyTransactions();
});

// To add Wolverine.HTTP services to the IoC container
builder.Services.AddWolverineHttp();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.MapWolverineEndpoints();

// Using the expanded command line options for the Critter Stack
// that are helpful for code generation, database migrations, and diagnostics
return await app.RunJasperFxCommands(args);


#region sample_Program_marker

// Adding this just makes it easier to bootstrap your
// application in a test harness project. Only a convenience
public partial class Program{}

#endregion
