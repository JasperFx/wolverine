using Marten;
using Oakton;
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
    })

    // This adds configuration with Wolverine's transactional outbox and
    // Marten middleware support to Wolverine
    .IntegrateWithWolverine();

builder.Host.UseWolverine(opts =>
{
    opts.Policies.AutoApplyTransactions();
});

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
return await app.RunOaktonCommands(args);

public partial class Program{}
