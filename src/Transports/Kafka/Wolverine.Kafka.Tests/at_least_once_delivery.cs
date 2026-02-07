using Confluent.Kafka;
using JasperFx.Core;
using JasperFx.Resources;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.ComplianceTests;
using Wolverine.ComplianceTests.Compliance;
using Wolverine.Tracking;

namespace Wolverine.Kafka.Tests;

public class AtLeastOnceDeliveryComplianceFixture : TransportComplianceFixture, IAsyncLifetime
{
    public AtLeastOnceDeliveryComplianceFixture() : base(new Uri("kafka://topic/receiver"), 120)
    {
    }

    public async Task InitializeAsync()
    {
        var receiverTopic = "atleastonce.receiver";
        var senderTopic = "atleastonce.sender";

        OutboundAddress = new Uri("kafka://topic/" + receiverTopic);

        await SenderIs(opts =>
        {
            opts.UseKafka("localhost:9092").AutoProvision();

            opts.ListenToKafkaTopic(senderTopic).UseForReplies().ConfigureConsumer(consumer =>
            {
                consumer.GroupId = "atleastonce-sender";
                consumer.AutoOffsetReset = AutoOffsetReset.Earliest;
            });

            opts.PublishAllMessages().ToKafkaTopic(receiverTopic).SendInline();

            opts.Services.AddResourceSetupOnStartup();
        });

        await ReceiverIs(opts =>
        {
            opts.UseKafka("localhost:9092").AutoProvision();

            opts.ListenToKafkaTopic(receiverTopic)
                .Named("receiver")
                .ProcessInline()
                .EnableAtLeastOnceDelivery();

            opts.Services.AddResourceSetupOnStartup();
        });
    }

    public new Task DisposeAsync()
    {
        return Task.CompletedTask;
    }
}

public class AtLeastOnceDeliverySendingAndReceivingCompliance : TransportCompliance<AtLeastOnceDeliveryComplianceFixture>;

public class at_least_once_delivery_configuration
{
    [Fact]
    public async Task at_least_once_delivery_with_inline_processing()
    {
        var receiverTopic = "atleastonce-inline-receiver-" + Guid.NewGuid().ToString("N")[..8];

        using var receiver = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.ServiceName = "AtLeastOnceReceiver";
                opts.UseKafka("localhost:9092").AutoProvision()
                    .ConfigureConsumers(c =>
                    {
                        c.GroupId = "atleastonce-inline-receiver";
                        c.AutoOffsetReset = AutoOffsetReset.Earliest;
                    });

                opts.ListenToKafkaTopic(receiverTopic)
                    .ProcessInline()
                    .EnableAtLeastOnceDelivery();

                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync();

        using var sender = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.ServiceName = "AtLeastOnceSender";
                opts.UseKafka("localhost:9092").AutoProvision();

                opts.PublishAllMessages().ToKafkaTopic(receiverTopic).SendInline();

                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync();

        var message = new AtLeastOnceTestMessage(Guid.NewGuid());

        var tracked = await sender.TrackActivity()
            .AlsoTrack(receiver)
            .Timeout(30.Seconds())
            .SendMessageAndWaitAsync(message);

        tracked.Received.SingleMessage<AtLeastOnceTestMessage>()
            .Id.ShouldBe(message.Id);
    }
}

public record AtLeastOnceTestMessage(Guid Id);

public class AtLeastOnceTestMessageHandler
{
    public void Handle(AtLeastOnceTestMessage message)
    {
        // Just process it
    }
}
