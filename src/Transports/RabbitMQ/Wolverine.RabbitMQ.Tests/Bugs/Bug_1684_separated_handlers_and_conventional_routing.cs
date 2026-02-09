using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.Attributes;
using Wolverine.Runtime;
using Wolverine.Tracking;
using Xunit;
using Xunit.Abstractions;

namespace Wolverine.RabbitMQ.Tests.Bugs;

public class Bug_1684_separated_handlers_and_conventional_routing(ITestOutputHelper Output)
{
    [Fact]
    public async Task try_it_and_send_to_multiple_topic_subscriptions()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseRabbitMq()
                    .AutoProvision()
                    .AutoPurgeOnStartup()
                    .UseConventionalRouting(x =>
                    {
                        x.IncludeTypes(type => type == typeof(Msg));
                    });
                
                opts.Policies.DisableConventionalLocalRouting();

                opts.MultipleHandlerBehavior = MultipleHandlerBehavior.Separated;
            })
            .ConfigureServices(services =>
            {
                //services.AddHostedService<BackgroundJob>();
            })
            
            .StartAsync();

        var message = new Msg(Guid.NewGuid());
        var tracked = await host.TrackActivity().IncludeExternalTransports().SendMessageAndWaitAsync(message);

        var all = tracked.AllRecordsInOrder().ToArray();
        foreach (var record in all)
        {
            Output.WriteLine(record.ToString());
        }

        var received = tracked.Received.MessagesOf<Msg>().ToArray();
        received.Length.ShouldBe(2);
    }
}

public class BackgroundJob(IWolverineRuntime runtime) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var bus = new MessageBus(runtime);
        var message = new Msg(Guid.NewGuid());
        await bus.PublishAsync(message);
    }
}

public record Msg(Guid Id);

[StickyHandler(nameof(ConsumerOne))]
public class ConsumerOne : IWolverineHandler
{
    public Task Consume(Msg message)
    {
        Console.ForegroundColor = ConsoleColor.Blue;
        Console.WriteLine($"Consumed by One: {message.Id}");
        return Task.CompletedTask;
    }
}

[StickyHandler(nameof(ConsumerTwo))]
public class ConsumerTwo : IWolverineHandler
{
    public void Consume(Msg message)
    {
        Console.ForegroundColor = ConsoleColor.Magenta;
        Console.WriteLine($"Consumed by Two: {message.Id}");
    }
}