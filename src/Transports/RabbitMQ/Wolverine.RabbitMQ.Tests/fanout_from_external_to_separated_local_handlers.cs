using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.Attributes;
using Wolverine.Tracking;
using Xunit;
using Xunit.Abstractions;

namespace Wolverine.RabbitMQ.Tests;

public class fanout_from_external_to_separated_local_handlers(ITestOutputHelper output)
{
    [Fact]
    public async Task should_fanout_to_all_local_handlers_from_external_endpoint()
    {
        var queueName = RabbitTesting.NextQueueName();

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseRabbitMq()
                    .AutoProvision()
                    .AutoPurgeOnStartup();

                opts.ListenToRabbitQueue(queueName);
                opts.PublishMessage<FanoutTestMessage>().ToRabbitQueue(queueName);

                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType<FanoutHandlerOne>()
                    .IncludeType<FanoutHandlerTwo>()
                    .IncludeType<FanoutHandlerThree>();

                opts.MultipleHandlerBehavior = MultipleHandlerBehavior.Separated;
            })
            .StartAsync();

        var message = new FanoutTestMessage(Guid.NewGuid());

        var tracked = await host.TrackActivity()
            .IncludeExternalTransports()
            .Timeout(30.Seconds())
            .SendMessageAndWaitAsync(message);

        var all = tracked.AllRecordsInOrder().ToArray();
        foreach (var record in all)
        {
            output.WriteLine(record.ToString());
        }

        // 1 execution for the fanout handler at the RabbitMQ endpoint +
        // 3 executions for each local handler
        var executed = tracked.Executed.MessagesOf<FanoutTestMessage>().ToArray();
        executed.Length.ShouldBe(4);
    }
}

public record FanoutTestMessage(Guid Id);

[WolverineIgnore]
public class FanoutHandlerOne
{
    public void Handle(FanoutTestMessage message)
    {
        // handled
    }
}

[WolverineIgnore]
public class FanoutHandlerTwo
{
    public void Handle(FanoutTestMessage message)
    {
        // handled
    }
}

[WolverineIgnore]
public class FanoutHandlerThree
{
    public void Handle(FanoutTestMessage message)
    {
        // handled
    }
}
