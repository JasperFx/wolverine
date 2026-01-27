using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using Wolverine.Tracking;
using Xunit;

namespace Wolverine.Redis.Tests.Bugs;

public class Bug_1970_issue_with_scheduling
{
    [Fact]
    public async Task send_scheduled_message()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseRedisTransport("localhost:6379").AutoProvision()
                    .ConfigureDefaultConsumerName((runtime, endpoint) =>
                        $"{runtime.Options.ServiceName}-test-{runtime.DurabilitySettings.AssignedNodeNumber}");
                opts.PublishAllMessages().ToRedisStream("wolverine-messages");
                opts.ListenToRedisStream("wolverine-messages", "test-consumers")
                    .StartFromNewMessages();
            }).StartAsync();

        var bus = host.MessageBus();
        
        await bus.ScheduleAsync(new TestCommand("Do something"), TimeSpan.FromSeconds(5));
    }
}

public record TestCommand(string message);

public class TestCommandHandler
{
    public void Handle(TestCommand command) => Console.WriteLine(
        $"Handled command with message: {command.message}");
}