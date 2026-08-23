using Microsoft.Extensions.Hosting;
using JasperFx.Resources;
using Wolverine;
using Wolverine.ErrorHandling;
using Wolverine.Redis;
using Wolverine.Redis.Tests;

// This sample used to be written as top-level statements. That is fine for a documentation
// snippet but not inside a test assembly: under xUnit v3 the test project is an executable, and
// C# makes top-level statements THE entry point, demoting xunit's generated one. The demotion is
// reported as CS7022, which this repo had in NoWarn, so it happened silently -- the test runner
// launched the process and got this sample instead of the runner, and the whole assembly failed
// with "Test process did not return valid JSON". Keep the sample in a method.
internal static class RedisTransportWithSchedulingSample
{
    public static async Task RunAsync()
    {
        #region sample_using_dead_letter_queue_for_redis

        var builder = Host.CreateDefaultBuilder();

        using var host = await builder.UseWolverine(opts =>
        {
            opts.UseRedisTransport(RedisContainerFixture.ConnectionString).AutoProvision()
                .SystemQueuesEnabled(false) // Disable reply queues
                .DeleteStreamEntryOnAck(true); // Clean up stream entries on ack

            // Sending inline so the messages are added to the stream right away
            opts.PublishAllMessages().ToRedisStream("wolverine-messages")
                .SendInline();

            opts.ListenToRedisStream("wolverine-messages", "default")
                .EnableNativeDeadLetterQueue(); // Enable DLQ for failed messages

            // schedule retry delays. On a Buffered (the default) or Inline Redis listener these are
            // parked natively in Redis, in the stream's scheduled sorted set; a Durable listener
            // schedules them through its message store's inbox like every other transport
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
    }
}

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

public class TestCommand1Handler
{
    public void Handle(TestCommand1 command)
    {
        Console.WriteLine($"Handled TestCommand1 with message: {command.message}");
    }
}
