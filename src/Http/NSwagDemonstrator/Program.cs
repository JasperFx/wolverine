
using IntegrationTests;
using Marten;
using JasperFx;
using JasperFx.Resources;
using Wolverine;
using Wolverine.Http;
using Wolverine.Marten;

var builder = WebApplication.CreateBuilder(args);

// Adding Marten for persistence
builder.Services.AddMarten(opts =>
{
    opts.Connection(Servers.PostgresConnectionString);
    opts.DatabaseSchemaName = "todo";
})
    .IntegrateWithWolverine();

builder.Services.AddResourceSetupOnStartup();

// Wolverine usage is required for WolverineFx.Http
builder.Host.UseWolverine(opts =>
{
    opts.Durability.Mode = DurabilityMode.Solo;
    opts.Durability.DurabilityAgentEnabled = false;

    // This middleware will apply to the HTTP
    // endpoints as well
    opts.Policies.AutoApplyTransactions();

    // Setting up the outbox on all locally handled
    // background tasks
    opts.Policies.UseDurableLocalQueues();
});

// Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddOpenApiDocument();
builder.Services.AddWolverineHttp();
//builder.Services.AddSwaggerDocument();

var app = builder.Build();

app.UseOpenApi(); // serve documents (same as app.UseSwagger())
app.UseSwaggerUi();
//app.UseReDoc(); // serve ReDoc UI


// Let's add in Wolverine HTTP endpoints to the routing tree
app.MapWolverineEndpoints();

// TODO Investigate if this is a dotnet-getdocument issue
args = args.Where(arg => !arg.StartsWith("--applicationName")).ToArray();

return await app.RunJasperFxCommands(args);

