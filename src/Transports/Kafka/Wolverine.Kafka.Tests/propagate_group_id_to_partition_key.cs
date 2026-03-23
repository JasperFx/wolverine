using Confluent.Kafka;
using JasperFx.Core;
using JasperFx.Resources;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.Attributes;
using Wolverine.Tracking;

namespace Wolverine.Kafka.Tests;

public class propagate_group_id_to_partition_key : IAsyncLifetime
{
    private IHost _host = null!;

    public async Task InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.ServiceName = "PropagateGroupIdTests";

                opts.UseKafka(KafkaContainerFixture.ConnectionString).AutoProvision();

                // Enable the feature under test
                opts.Policies.PropagateGroupIdToPartitionKey();

                opts.Policies.DisableConventionalLocalRouting();

                // Listen to source topic with an explicit GroupId
                opts.ListenToKafkaTopic("groupid-source")
                    .ProcessInline()
                    .ConfigureConsumer(config =>
                    {
                        config.GroupId = "source-group-123";
                        config.AutoOffsetReset = AutoOffsetReset.Earliest;
                    });

                // Listen to target topic where cascaded messages arrive
                opts.ListenToKafkaTopic("groupid-target")
                    .ProcessInline();

                // Route TriggerFromGroupId to the source topic
                opts.PublishMessage<TriggerFromGroupId>()
                    .ToKafkaTopic("groupid-source")
                    .SendInline();

                // Route cascaded TargetFromGroupId messages to the target topic
                opts.PublishMessage<TargetFromGroupId>()
                    .ToKafkaTopic("groupid-target")
                    .SendInline();

                opts.Discovery.IncludeAssembly(GetType().Assembly);
                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync();
    }

    [Fact]
    public async Task cascaded_message_inherits_partition_key_from_originating_message()
    {
        var session = await _host.TrackActivity()
            .IncludeExternalTransports()
            .Timeout(30.Seconds())
            .WaitForMessageToBeReceivedAt<TargetFromGroupId>(_host)
            .PublishMessageAndWaitAsync(new TriggerFromGroupId("hello"),
                new DeliveryOptions { PartitionKey = "fixture-abc" });

        var envelope = session.Received.SingleEnvelope<TargetFromGroupId>();
        envelope.PartitionKey.ShouldBe("fixture-abc");
    }

    public async Task DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }
}

[Topic("groupid-source")]
public record TriggerFromGroupId(string Name);

[Topic("groupid-target")]
public record TargetFromGroupId(string Name);

public static class TriggerFromGroupIdHandler
{
    public static TargetFromGroupId Handle(TriggerFromGroupId message)
    {
        return new TargetFromGroupId(message.Name);
    }
}

public static class TargetFromGroupIdHandler
{
    public static void Handle(TargetFromGroupId message)
    {
        // no-op, just receive
    }
}

/// <summary>
/// Verifies that when ByPropertyNamed is configured, the message property is used as the
/// partition key on outgoing messages, even when the originating message came from Kafka
/// (where the Kafka consumer GroupId is unrelated to the business partition key).
/// </summary>
public class propagate_group_id_via_property_name : IAsyncLifetime
{
    private IHost _host = null!;

    public async Task InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.ServiceName = "PropagateGroupIdByPropertyTests";

                opts.UseKafka(KafkaContainerFixture.ConnectionString).AutoProvision();

                opts.Policies.PropagateGroupIdToPartitionKey();
                opts.MessagePartitioning.UseInferredMessageGrouping().ByPropertyNamed("Id");

                opts.Policies.DisableConventionalLocalRouting();

                opts.ListenToKafkaTopic("groupid-property-source")
                    .ProcessInline()
                    .ConfigureConsumer(config =>
                    {
                        config.GroupId = "my-application-name";
                        config.AutoOffsetReset = AutoOffsetReset.Earliest;
                    });

                opts.ListenToKafkaTopic("groupid-property-target")
                    .ProcessInline();

                opts.PublishMessage<TriggerWithId>()
                    .ToKafkaTopic("groupid-property-source")
                    .SendInline();

                opts.PublishMessage<TargetWithId>()
                    .ToKafkaTopic("groupid-property-target")
                    .SendInline();

                opts.Discovery.IncludeAssembly(GetType().Assembly);
                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync();
    }

    [Fact]
    public async Task cascaded_message_uses_message_property_as_partition_key_not_consumer_group()
    {
        var fixtureId = "fixture-789";

        var session = await _host.TrackActivity()
            .IncludeExternalTransports()
            .Timeout(30.Seconds())
            .WaitForMessageToBeReceivedAt<TargetWithId>(_host)
            .PublishMessageAndWaitAsync(new TriggerWithId(fixtureId));

        var envelope = session.Received.SingleEnvelope<TargetWithId>();

        // Must be the fixture id from the message property, not the Kafka consumer GroupId
        envelope.PartitionKey.ShouldBe(fixtureId);
        envelope.PartitionKey.ShouldNotBe("my-application-name");
    }

    public async Task DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }
}

[Topic("groupid-property-source")]
public record TriggerWithId(string Id);

[Topic("groupid-property-target")]
public record TargetWithId(string Id);

public static class TriggerWithIdHandler
{
    public static TargetWithId Handle(TriggerWithId message)
    {
        return new TargetWithId(message.Id);
    }
}

public static class TargetWithIdHandler
{
    public static void Handle(TargetWithId message)
    {
        // no-op, just receive
    }
}
