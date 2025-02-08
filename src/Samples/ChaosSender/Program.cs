using System.Diagnostics;
using ChaosSender;
using IntegrationTests;
using JasperFx.Core;
using Marten;
using JasperFx;
using Wolverine;
using Wolverine.AdminApi;
using Wolverine.ErrorHandling;
using Wolverine.Marten;
using Wolverine.RabbitMQ;

#region sample_integrating_wolverine_with_marten

var builder = WebApplication.CreateBuilder(args);
builder.Host.ApplyJasperFxExtensions();

builder.Services.AddMarten(opts =>
    {
        opts.Connection(Servers.PostgresConnectionString);
        opts.DatabaseSchemaName = "chaos2";
    })
    .IntegrateWithWolverine();

builder.Host.UseWolverine(opts =>
{
    opts.Policies.OnAnyException().RetryWithCooldown(50.Milliseconds(), 100.Milliseconds(), 250.Milliseconds());

    opts.Services.AddScoped<IMessageRecordRepository, MartenMessageRecordRepository>();

    opts.Policies.DisableConventionalLocalRouting();
    opts.UseRabbitMq().AutoProvision();

    opts.Policies.UseDurableInboxOnAllListeners();
    opts.Policies.UseDurableOutboxOnAllSendingEndpoints();

    opts.ListenToRabbitQueue("chaos2");
    opts.PublishAllMessages().ToRabbitQueue("chaos2");


    opts.Policies.AutoApplyTransactions();
});

#endregion

// Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();


var app = builder.Build();

app.MapGet("/", () => Results.Redirect("/swagger"));

app.MapPost("/send", async (MessageBatch batch, IMessageBus bus) =>
{
    await batch.PublishAsync(bus);
});

app.MapGet("/status", async (IMessageRecordRepository repository) =>
{
    var remaining = await repository.FindOutstandingMessageCount(CancellationToken.None);

    return $"{remaining} messages at {DateTime.Now}";
});

app.MapWolverineAdminApiEndpoints();

app.UseSwagger();
app.UseSwaggerUI();

// Lot of Wolverine and Marten diagnostics and administrative tools
// come through JasperFx command line support
return await app.RunJasperFxCommands(args);


public record MessageBatch(int BatchSize, int Milliseconds)
{
    public async Task PublishAsync(IMessageBus bus)
    {
        var task = Task.Factory.StartNew(async () =>
        {
            var timer = new Stopwatch();
            timer.Start();

            while (timer.ElapsedMilliseconds < Milliseconds)
            {
                await bus.PublishAsync(new SendMessages(BatchSize));
                await Task.Delay(100.Milliseconds());
            }
        });
    }
}