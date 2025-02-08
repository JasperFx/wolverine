using Confluent.Kafka;
using JasperFx;
using Wolverine;
using Wolverine.Kafka;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

builder.Host.UseWolverine(opts =>
{
    opts.UseKafka("localhost:9092").AutoProvision();

    opts.PublishAllMessages().ToKafkaTopic("topic_0");

    opts.BatchMessagesOf<TestMessage>();
    opts.ListenToKafkaTopic("topic_0");
});

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.MapPost("/test", async (IMessageBus bus) =>
    {
        var message = new TestMessage();
        await bus.PublishAsync(message);
        await bus.PublishAsync(message);
        // results in:
        // No known handler for TestMessage#08dced0c-3834-b4c6-54d7-e075bf020000 from kafka://topic/topic_0
    })
    .WithOpenApi();

return await app.RunJasperFxCommands(args);

public partial class Program {}


public record TestMessage;

public class TestMessagesHandler
{
    public void Handle(TestMessage[] messages)
    {
        Console.WriteLine("Messages received");
    }
}