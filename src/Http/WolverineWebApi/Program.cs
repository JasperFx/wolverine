using IntegrationTests;
using Marten;
using Microsoft.AspNetCore.Http.HttpResults;
using Oakton;
using Oakton.Resources;
using Wolverine;
using Wolverine.Http;
using Wolverine.Marten;
using WolverineWebApi;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.

builder.Services.AddControllers();
// Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

builder.Services.AddMarten(opts =>
{
    opts.Connection(Servers.PostgresConnectionString);
    opts.DatabaseSchemaName = "http";
}).IntegrateWithWolverine();

builder.Services.AddResourceSetupOnStartup();

// Need this.
builder.Host.UseWolverine();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseAuthorization();

app.MapControllers();

app.MapGet("/orders/{orderId}", Results<BadRequest, Ok<Order>> (int orderId) 
    => orderId > 999 ? TypedResults.BadRequest() : TypedResults.Ok(new Order(orderId)));

app.MapPost("/orders", Results<BadRequest, Ok<Order>> (CreateOrder command) 
    => command.OrderId > 999 ? TypedResults.BadRequest() : TypedResults.Ok(new Order(command.OrderId)));

app.MapWolverineEndpoints();

await app.RunOaktonCommands(args);