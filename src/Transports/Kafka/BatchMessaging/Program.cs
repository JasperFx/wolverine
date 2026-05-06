using Confluent.Kafka;
using JasperFx;
using Wolverine;
using Wolverine.Kafka;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();

builder.Host.UseWolverine(opts =>
{
    opts.UseKafka("localhost:9092")
        .AutoProvision()
        .AutoPurgeOnStartup()
        .ConfigureConsumers(consumer =>
        {
            consumer.AutoOffsetReset = AutoOffsetReset.Earliest;
        });

    opts.PublishAllMessages().ToKafkaTopic("topic_0");

    opts.BatchMessagesOf<TestMessage>();
    opts.ListenToKafkaTopic("topic_0");
});

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.MapPost("/test", async (IMessageBus bus) =>
    {
        var message = new TestMessage();
        await bus.PublishAsync(message);
        await bus.PublishAsync(message);
        // results in:
        // No known handler for TestMessage#08dced0c-3834-b4c6-54d7-e075bf020000 from kafka://topic/topic_0
    });

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