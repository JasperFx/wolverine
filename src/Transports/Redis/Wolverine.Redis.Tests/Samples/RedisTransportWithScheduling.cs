using Microsoft.Extensions.Hosting;
using JasperFx.Resources;
using Wolverine;
using Wolverine.ErrorHandling;
using Wolverine.Redis;

#region sample_using_dead_letter_queue_for_redis

var builder = Host.CreateDefaultBuilder();

using var host = await builder.UseWolverine(opts =>
{
    opts.UseRedisTransport("localhost:6379").AutoProvision()
        .SystemQueuesEnabled(false) // Disable reply queues
        .DeleteStreamEntryOnAck(true); // Clean up stream entries on ack

    // Sending inline so the messages are added to the stream right away
    opts.PublishAllMessages().ToRedisStream("wolverine-messages")
        .SendInline();

    opts.ListenToRedisStream("wolverine-messages", "default")
        .EnableNativeDeadLetterQueue() // Enable DLQ for failed messages
        .UseDurableInbox(); // Use durable inbox so retry messages are persisted
    
    // schedule retry delays
    // if durable, these will be scheduled natively in Redis
    opts.OnException<Exception>()
        .ScheduleRetry(
            TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(20),
            TimeSpan.FromSeconds(30));
    
    opts.Services.AddResourceSetupOnStartup();
}).StartAsync();

#endregion
var bus = host.MessageBus();
var delay = new Random().Next(10, 50);
await bus.ScheduleAsync(
    new TestCommand("Do something"),
    TimeSpan.FromSeconds(delay));

public record TestCommand(string message);

public class TestCommandHandler
{
    public TestCommand1 Handle(TestCommand command)
    {
        Console.WriteLine(
            $"Handled command with message: {command.message}");
        return new TestCommand1(command.message + "x");
    }
}

public record TestCommand1(string message);

public class TestCommand1Handler(IMessageContext messageBus)
{
    public void Handle(TestCommand1 command)
    {
        Console.WriteLine($"Handled TestCommand1 with message: {command.message}");
    }
}