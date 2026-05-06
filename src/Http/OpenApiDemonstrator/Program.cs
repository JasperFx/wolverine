using IntegrationTests;
using Marten;
using JasperFx;
using Wolverine;
using Wolverine.Http;
using Wolverine.Marten;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();

builder.Services.AddWolverineHttp();

builder.Services.AddMarten(Servers.PostgresConnectionString)
    .IntegrateWithWolverine();

builder.Host.UseWolverine();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.MapWolverineEndpoints();

await app.RunJasperFxCommands(args);

