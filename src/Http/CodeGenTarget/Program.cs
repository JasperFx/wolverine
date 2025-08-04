using JasperFx;
using Wolverine;
using Wolverine.Http;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();

builder.Host.UseWolverine();
builder.Services.AddWolverineHttp();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.MapWolverineEndpoints(x => x.WarmUpRoutes = RouteWarmup.Eager);

return await app.RunJasperFxCommands(args);

public static class ExampleHandler
{
    [WolverineGet("/api/test")]
    public static string Handle()
    {
        return "tested";
    }
}