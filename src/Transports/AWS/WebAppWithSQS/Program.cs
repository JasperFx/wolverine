using JasperFx;
using Wolverine;
using Wolverine.AmazonSqs;
using Wolverine.Http;
using Wolverine.RabbitMQ;
using Wolverine.Transports.Tcp;
using Wolverine.Util;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();

builder.Services.AddWolverineHttp();

builder.Host.UseWolverine(opts =>
{
    //opts.UseAmazonSqsTransportLocally();
    
    //opts.PublishMessage<ExtLog>().ToSqsQueue("ext-logs");

    opts.UseRabbitMq().AutoProvision();

    opts.ListenAtPort(PortFinder.GetAvailablePort());

    opts.ListenToRabbitQueue("foo");
});

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();

app.MapWolverineEndpoints();


return await app.RunJasperFxCommands(args);

public record ExtLog;

