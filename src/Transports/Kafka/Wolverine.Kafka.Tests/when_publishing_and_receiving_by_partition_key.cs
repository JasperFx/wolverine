using Microsoft.Extensions.Hosting;
using NSubstitute;
using NSubstitute.ReceivedExtensions;
using Oakton.Resources;
using Shouldly;
using Wolverine.ComplianceTests.Compliance;
using Wolverine.Tracking;

namespace Wolverine.Kafka.Tests;

public class when_publishing_and_receiving_by_partition_key : IAsyncLifetime
{
    #region sample_publish_to_kafka_by_partition_key

    public static ValueTask publish_by_partition_key(IMessageBus bus)
    {
        return bus.PublishAsync(new Message1(), new DeliveryOptions { PartitionKey = "one" });
    }

    #endregion
    
    private IHost _sender;
    private IHost _receiver;
    public async Task InitializeAsync()
    {
        _sender = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseKafka("localhost:9092").AutoProvision();
                opts.Policies.DisableConventionalLocalRouting();

                opts.Services.AddResourceSetupOnStartup();

                opts.PublishMessage<ColorMessage>()
                .ToKafkaTopic("colorswithkey")
                .BufferedInMemory();              
                
            }).StartAsync();

        _receiver = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseKafka("localhost:9092").AutoProvision();
                opts.ListenToKafkaTopic("colorswithkey")
                .ProcessInline();

                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync();
    }

    [Fact]
    public async Task can_receive_message_with_delivery_option_key()
    {
        var session = await _sender.TrackActivity()
            .AlsoTrack(_receiver)
            .WaitForMessageToBeReceivedAt<ColorMessage>(_receiver)
            .PublishMessageAndWaitAsync(new ColorMessage("tortoise"), new DeliveryOptions()
            {
                PartitionKey = "key1"
            });
        session.Received.SingleMessage<ColorMessage>()
            .Color.ShouldBe("tortoise");
    }
    
    [Fact]
    public async Task received_message_with_key_and_offset()
    {
        var session = await _sender.TrackActivity()
            .AlsoTrack(_receiver)
            .WaitForMessageToBeReceivedAt<ColorMessage>(_receiver)
            .PublishMessageAndWaitAsync(new ColorMessage("tortoise"), new DeliveryOptions()
            {
                PartitionKey = "key1"
            });
        var singleEnvelope = session.Received.SingleEnvelope<ColorMessage>();
        singleEnvelope.PartitionKey.ShouldBe("key1");
        singleEnvelope.Offset.ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task receive_message_with_group_id()
    {

    }

    public async Task DisposeAsync()
    {
        await _sender.StopAsync();
        await _receiver.StopAsync();
    }
}
