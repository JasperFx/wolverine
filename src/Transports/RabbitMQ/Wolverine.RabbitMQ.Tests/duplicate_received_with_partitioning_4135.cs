using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.Configuration;
using Wolverine.Tracking;
using Xunit;

namespace Wolverine.RabbitMQ.Tests;

// GH-4135. A listener with PartitionProcessingByGroupId recorded TWO Received events for one
// envelope while executing it exactly once, because ShardedExecutionBlock pre-deserializes through
// HandlerPipeline.TryDeserializeEnvelope -- whose finally block records Received -- and the envelope
// then reaches executeAsync already carrying a Message, taking the branch that records Received
// again. Beyond turning Received.SingleMessage<T>() red against correct behaviour, that
// double-counted the received metric and double-logged every receipt on any partitioned listener.
public class duplicate_received_with_partitioning_4135 : IAsyncLifetime
{
    private IHost _sender = null!;
    private IHost _receiver = null!;

    public async ValueTask InitializeAsync()
    {
        _receiver = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.ServiceName = "Receiver";
                opts.UseRabbitMq().AutoProvision().AutoPurgeOnStartup();
                opts.ListenToRabbitQueue("dup_received_4135")
                    .BufferedInMemory()
                    .PartitionProcessingByGroupId(PartitionSlots.Five);
                opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(DupProbeHandler));
            }).StartAsync();

        _sender = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.ServiceName = "Sender";
                opts.UseRabbitMq().AutoProvision();
                opts.PublishAllMessages().ToRabbitQueue("dup_received_4135");
                opts.Discovery.DisableConventionalDiscovery();
            }).StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _sender.StopAsync();
        await _receiver.StopAsync();
    }

    [Fact]
    public async Task records_exactly_one_received_per_envelope()
    {
        var session = await _sender.TrackActivity()
            .AlsoTrack(_receiver)
            .IncludeExternalTransports()
            .Timeout(30.Seconds())
            .SendMessageAndWaitAsync(new DupProbe("only-once"));

        var records = session.AllRecordsInOrder()
            .Where(x => x.Envelope?.Message is DupProbe)
            .ToArray();

        records.Count(x => x.MessageEventType == MessageEventType.ExecutionStarted).ShouldBe(1);
        records.Count(x => x.MessageEventType == MessageEventType.Received).ShouldBe(1);

        session.Received.SingleMessage<DupProbe>().Name.ShouldBe("only-once");
    }
}

public record DupProbe(string Name);

public static class DupProbeHandler
{
    public static void Handle(DupProbe message)
    {
    }
}
