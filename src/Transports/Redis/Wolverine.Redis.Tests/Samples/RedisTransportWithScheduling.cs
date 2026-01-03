using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using JasperFx.Resources;
using Wolverine;
using Wolverine.Redis;

for (var i = 0; i < 10; i++)
{
    var builder = Host.CreateDefaultBuilder();

    using var host = await builder.UseWolverine(opts =>
    {
        opts.UseRedisTransport("localhost:6379").AutoProvision()
            .SystemQueuesEnabled(false); // Disable reply queues

        // Sending inline is important for scheduling to work properly
        opts.PublishAllMessages().ToRedisStream("wolverine-messages")
            .SendInline();

        opts.ListenToRedisStream("wolverine-messages", "default");
        opts.Services.AddResourceSetupOnStartup();
    }).StartAsync();
    var bus = host.MessageBus();
    var delay = new Random().Next(10, 50);
    await bus.ScheduleAsync(
        new TestCommand("Do something"),
        TimeSpan.FromSeconds(delay));
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

public class TestCommand1Handler(IMessageContext messageBus)
{
    public void Handle(TestCommand1 command)
    {
        Console.WriteLine($"Handled TestCommand1 with message: {command.message}");
    }
}