using IntegrationTests;
using Oakton;
using Oakton.Resources;
using Wolverine;
using Wolverine.SqlServer;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

builder.Host.UseWolverine(opts =>
{
    opts.PersistMessagesWithSqlServer(Servers.SqlServerConnectionString, "wolverine");

    opts.Policies.UseDurableLocalQueues();
});

builder.Host.UseResourceSetupOnStartup();
builder.Host.ApplyOaktonExtensions();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();


app.MapPost("/invoke", async (IMessageBus bus) =>
    {
        // invoke command and publish event inside the command handler
        var command = new TestCommand();
        await bus.InvokeAsync(command);
    })
    .WithName("Invoke")
    .WithOpenApi();


app.MapPost("/publish", async (IMessageBus bus) =>
    {
        // this works fine - the event is saved to the database
        var @event = new TestEvent();
        await bus.PublishAsync(@event);
    })
    .WithName("Publish")
    .WithOpenApi();

return await app.RunOaktonCommands(args);


public record TestCommand;

public class TestCommandHandler
{
    public async Task Handle(TestCommand command, IMessageBus bus)
    {
        Console.WriteLine("Handling TestCommand");

        // desirable behavior: the event is saved to the database (Outbox pattern)
        // actual behavior: the event is handled in-memory only
        var @event = new TestEvent();
        await bus.PublishAsync(@event);
    }
}

public record TestEvent;

public class TestEventHandler
{
    public Task Handle(TestEvent @event)
    {
        Console.WriteLine("Handling TestEvent. But it is not save to database (if the event is published from command handler). " +
                          "So if the application crashes now, the event will be lost.");

        Thread.Sleep(10000);

        Console.WriteLine("TestEvent handled.");

        return Task.CompletedTask;
    }
}