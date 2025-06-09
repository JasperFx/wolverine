using System.Diagnostics;
using IntegrationTests;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using JasperFx.Resources;
using Wolverine.ComplianceTests;
using Wolverine.Marten;
using Wolverine.Tracking;
using Xunit;

namespace Wolverine.RabbitMQ.Tests.Bugs;

public class Bug_475_durable_outbox_sending_out_of_order
{
    [Fact]
    public async Task try_messages()
    {
        var queueName = RabbitTesting.NextQueueName();

        var tracker = new OrderTracker();

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Services.AddSingleton(tracker);

                opts.Services.AddMarten(Servers.PostgresConnectionString)
                    .IntegrateWithWolverine();

                opts.UseRabbitMq().AutoProvision().AutoPurgeOnStartup();

                opts.PublishAllMessages().ToRabbitQueue(queueName).SendInline();
                opts.ListenToRabbitQueue(queueName).Sequential();

                opts.Policies.UseDurableInboxOnAllListeners();
                opts.Policies.UseDurableOutboxOnAllSendingEndpoints();
            }).StartAsync();

        await host.ResetResourceState();

        Func<IMessageBus, Task> publishing = async bus =>
        {
            await bus.PublishAsync(new OrderedMessage(1));
            await bus.PublishAsync(new OrderedMessage(2));
            await bus.PublishAsync(new OrderedMessage(3));
            await bus.PublishAsync(new OrderedMessage(4));
            await bus.PublishAsync(new OrderedMessage(5));
            await bus.PublishAsync(new OrderedMessage(6));
        };

        await host.TrackActivity().IncludeExternalTransports().ExecuteAndWaitAsync(publishing);

        tracker.Encountered.ShouldHaveTheSameElementsAs(1, 2,3 ,4,5,6);
    }
}

public static class OrderedMessageHandler
{
    public static void Handle(OrderedMessage message, OrderTracker tracker)
    {
        tracker.Encountered.Add(message.Order);
    }
}

public record OrderedMessage(int Order);

public class OrderTracker
{
    public OrderTracker()
    {
        Debug.WriteLine("foo");
    }

    public List<int> Encountered { get; } = new();
}