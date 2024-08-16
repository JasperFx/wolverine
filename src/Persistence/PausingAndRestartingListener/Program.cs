using IntegrationTests;
using JasperFx.Core;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Oakton;
using Oakton.Resources;
using Wolverine;
using Wolverine.EntityFrameworkCore;
using Wolverine.ErrorHandling;
using Wolverine.Postgresql;
using Wolverine.Runtime;
using WolverineRepro;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var connectionString = Servers.PostgresConnectionString;
builder.Services.AddDbContextWithWolverineIntegration<WolverineReproDbContext>(options =>
    options.UseNpgsql(connectionString));

builder.Host.UseWolverine(options =>
{
    options.PersistMessagesWithPostgresql(connectionString, "public");

    options.UseEntityFrameworkCoreTransactions();

    options.Policies.AutoApplyTransactions();
    options.Policies.UseDurableLocalQueues();
    options.Policies.OnException<NpgsqlException>()
        .RetryWithCooldown(50.Milliseconds(), 100.Milliseconds(), 250.Milliseconds());
});
builder.Host.UseResourceSetupOnStartup();
builder.Host.ApplyOaktonExtensions();

builder.Services.AddHostedService<Publisher>();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.MapPost("/sendmessage", async (IMessageBus bus) =>
    {
        await bus.SendAsync(new TestMessage());
    });

await app.RunOaktonCommands(args);

internal class Publisher : IHostedService
{
    private readonly IWolverineRuntime _runtime;

    public Publisher(IWolverineRuntime runtime)
    {
        _runtime = runtime;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await new MessageBus(_runtime).PublishAsync(new TestMessage());
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}