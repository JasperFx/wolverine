using Confluent.Kafka;
using JasperFx;
using Wolverine;
using Wolverine.Kafka;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();

builder.Host.UseWolverine(opts =>
{
    // GH-3763: this app is booted by Wolverine.Kafka.Tests through AlbaHost.For<Program>, and without this
    // pin Wolverine resolves the application assembly to the *test* assembly instead of this one. Handler
    // discovery then never sees TestMessagesHandler, so BatchMessagesOf<TestMessage>() below has no
    // TestMessage[] handler to bind to and every batch fails at runtime with "there is no known handler for
    // TestMessage[]". Same pin, for the same reason, as WolverineWebApi's Program. See GH-3521.
    opts.ApplicationAssembly = typeof(Program).Assembly;

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