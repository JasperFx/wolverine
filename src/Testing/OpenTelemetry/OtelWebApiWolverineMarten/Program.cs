using JasperFx;
using JasperFx.CodeGeneration;
using Marten;
using OpenTelemetry;
using Wolverine;
using Wolverine.Http;
using Wolverine.Marten;

var builder = WebApplication.CreateBuilder(args);

builder.Host.ApplyJasperFxExtensions();

builder.Host.UseWolverine(opts =>
{
    opts.Policies.AutoApplyTransactions();
    opts.CodeGeneration.TypeLoadMode = !builder.Environment.IsDevelopment() ? TypeLoadMode.Static : TypeLoadMode.Auto;

    // Disable the "wolverine_node_assignments" traces entirely
    opts.Durability.NodeAssignmentHealthCheckTracingEnabled = false;

    // Or, sample those traces to only once every 10 minutes
    // opts.Durability.NodeAssignmentHealthCheckTraceSamplingPeriod = TimeSpan.FromMinutes(10);
});

builder.Services.AddMarten(opts => { opts.Connection("host=localhost;Port=5433;Database=postgres;Username=postgres;password=postgres"); })
    .ApplyAllDatabaseChangesOnStartup()
    .UseLightweightSessions()
    .IntegrateWithWolverine();

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddWolverineHttp();


#region sample_enabling_open_telemetry

builder.Services.AddOpenTelemetry()
    .WithTracing(tracing => { tracing.AddSource("Wolverine"); })
    .WithMetrics(metrics => { metrics.AddMeter("Wolverine"); })
    .UseOtlpExporter();

#endregion

// Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// Doing this just to get JSON formatters in here
builder.Services.AddControllers();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.MapWolverineEndpoints(opts =>
{
    opts.RequireAuthorizeOnAll();
    opts.WarmUpRoutes = RouteWarmup.Eager; // https://wolverinefx.net/guide/http/#eager-warmup
});

return await app.RunJasperFxCommands(args);

public partial class Program
{
}