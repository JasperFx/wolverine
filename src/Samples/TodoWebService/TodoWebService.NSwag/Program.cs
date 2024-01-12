using Marten;
using Oakton;
using Oakton.Resources;
using Wolverine;
using Wolverine.Http;
using Wolverine.Marten;

var builder = WebApplication.CreateBuilder(args);

// Adding Marten for persistence
builder.Services.AddMarten(opts =>
    {
        opts.Connection(builder.Configuration.GetConnectionString("Marten"));
        opts.DatabaseSchemaName = "todo";
    })
    .IntegrateWithWolverine();

builder.Services.AddResourceSetupOnStartup();

// Wolverine usage is required for WolverineFx.Http
builder.Host.UseWolverine(opts =>
{
    // This middleware will apply to the HTTP
    // endpoints as well
    opts.Policies.AutoApplyTransactions();

    // Setting up the outbox on all locally handled
    // background tasks
    opts.Policies.UseDurableLocalQueues();
});


// Learn more about configuring NSwag at https://learn.microsoft.com/en-us/aspnet/core/tutorials/getting-started-with-nswag
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddOpenApiDocument();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseOpenApi();
    app.UseSwaggerUi();
}

// Let's add in Wolverine HTTP endpoints to the routing tree
app.MapWolverineEndpoints();

// TODO Investigate if this is a dotnet-getdocument issue
args = args.Where(arg => !arg.StartsWith("--applicationName")).ToArray();

return await app.RunOaktonCommands(args);
