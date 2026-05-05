#region sample_bootstrapping_order_saga_sample
using Marten;
using JasperFx;
using JasperFx.Resources;
using OrderSagaSample;
using Wolverine;
using Wolverine.Marten;

var builder = WebApplication.CreateBuilder(args);

// Not 100% necessary, but enables some extra command line diagnostics
builder.Host.ApplyJasperFxExtensions();

// Adding Marten
builder.Services.AddMarten(opts =>
    {
        var connectionString = builder.Configuration.GetConnectionString("Marten");
        opts.Connection(connectionString!);
        opts.DatabaseSchemaName = "orders";
    })

    // Adding the Wolverine integration for Marten.
    .IntegrateWithWolverine();


builder.Services.AddOpenApi();

// Do all necessary database setup on startup
builder.Services.AddResourceSetupOnStartup();

// The defaults are good enough here
builder.Host.UseWolverine();

var app = builder.Build();

// Just delegating to Wolverine's local command bus for all
app.MapPost("/start", (StartOrder start, IMessageBus bus) => bus.InvokeAsync(start));
app.MapPost("/complete", (CompleteOrder complete, IMessageBus bus) => bus.InvokeAsync(complete));
app.MapGet("/all", (IQuerySession session) => session.Query<Order>().ToListAsync());
app.MapGet("/", () => Results.Redirect("/openapi/v1.json"));

app.MapOpenApi();

return await app.RunJasperFxCommands(args);

#endregion