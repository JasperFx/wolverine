using IntegrationTests;
using Marten;
using JasperFx;
using OpenApiDemonstrator;
using Wolverine;
using Wolverine.Http;
using Wolverine.Marten;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
// Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

builder.Services.AddWolverineHttp();

builder.Services.AddMarten(Servers.PostgresConnectionString)
    .IntegrateWithWolverine();

builder.Host.UseWolverine();

builder.Services.AddHostedService<BuildSwagger>();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.MapWolverineEndpoints();

await app.RunJasperFxCommands(args);

