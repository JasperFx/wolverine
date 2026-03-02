using JasperFx.Core;
using JasperFx.Resources;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.Tracking;

namespace Wolverine.Kafka.Tests;

public class propagate_group_id_to_partition_key : IAsyncLifetime
{
    private IHost _host;

    public async Task InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.ServiceName = "PropagateGroupIdTests";

                opts.UseKafka("localhost:9092").AutoProvision();

                // Enable the feature under test
                opts.Policies.PropagateGroupIdToPartitionKey();

                opts.Policies.DisableConventionalLocalRouting();

                // Listen to source topic with an explicit GroupId
                opts.ListenToKafkaTopic("groupid-source")
                    .ProcessInline()
                    .ConfigureConsumer(config =>
                    {
                        config.GroupId = "source-group-123";
                    });

                // Listen to target topic where cascaded messages arrive
                opts.ListenToKafkaTopic("groupid-target")
                    .ProcessInline();

                // Route cascaded TargetFromGroupId messages to the target topic
                opts.PublishMessage<TargetFromGroupId>()
                    .ToKafkaTopic("groupid-target")
                    .SendInline();

                opts.Discovery.IncludeAssembly(GetType().Assembly);
                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync();
    }

    [Fact]
    public async Task cascaded_message_receives_partition_key_from_originating_group_id()
    {
        var session = await _host.TrackActivity()
            .IncludeExternalTransports()
            .Timeout(30.Seconds())
            .WaitForMessageToBeReceivedAt<TargetFromGroupId>(_host)
            .PublishMessageAndWaitAsync(new TriggerFromGroupId("hello"));

        var envelope = session.Received.SingleEnvelope<TargetFromGroupId>();
        envelope.PartitionKey.ShouldBe("source-group-123");
    }

    public async Task DisposeAsync()
    {
        await _host.StopAsync();
    }
}

public record TriggerFromGroupId(string Name);

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
