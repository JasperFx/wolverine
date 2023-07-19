using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using Shouldly;
using TestingSupport;
using Wolverine.Tracking;
using Xunit;

namespace Wolverine.AzureServiceBus.Tests.ConventionalRouting.New;

public class end_to_end_with_conventional_routing_with_prefix : IDisposable
{
    private readonly IHost _receiver;
    private readonly IHost _sender;

    public end_to_end_with_conventional_routing_with_prefix()
    {
        _sender = WolverineHost.For(opts =>
        {
            opts.UseAzureServiceBusTesting()
                .PrefixIdentifiers("shazaam")
                .UseConventionalRouting(c => c.UsePublishingBroadcastFor(t => t == typeof(BroadcastedMessage), t => "test"))
                .AutoProvision().AutoPurgeOnStartup();
            opts.DisableConventionalDiscovery();
            opts.ServiceName = "Sender";
        });

        _receiver = WolverineHost.For(opts =>
        {
            opts.UseAzureServiceBusTesting()
                .PrefixIdentifiers("shazaam")
                .UseConventionalRouting(c => c.UsePublishingBroadcastFor(t => t == typeof(BroadcastedMessage), t => "test"))
                .AutoProvision().AutoPurgeOnStartup();
            opts.ServiceName = "Receiver";
        });
    }

    public void Dispose()
    {
        _sender?.Dispose();
        _receiver?.Dispose();
    }

    [Fact]
    public async Task send_from_one_node_to_another_all_with_conventional_routing()
    {
        var session = await _sender.TrackActivity()
            .AlsoTrack(_receiver)
            .IncludeExternalTransports()
            .Timeout(30.Seconds())
            .SendMessageAndWaitAsync(new NewRoutedMessage());

        var received = session
            .AllRecordsInOrder()
            .Where(x => x.Envelope.Message?.GetType() == typeof(NewRoutedMessage))
            .Single(x => x.MessageEventType == MessageEventType.Received);

        received
            .ServiceName.ShouldBe("Receiver");

        received.Envelope.Destination
            .ShouldBe(new Uri("asb://queue/shazaam.newrouted"));
    }

    [Fact]
    public async Task send_from_one_node_to_another_through_topic_all_with_conventional_routing()
    {
        var session = await _sender.TrackActivity()
            .AlsoTrack(_receiver)
            .IncludeExternalTransports()
            .Timeout(30.Seconds())
            .SendMessageAndWaitAsync(new BroadcastedMessage());

        var received = session
            .AllRecordsInOrder()
            .Where(x => x.Envelope.Message?.GetType() == typeof(BroadcastedMessage))
            .Single(x => x.MessageEventType == MessageEventType.Received);

        received.ServiceName.ShouldBe("Receiver");

        received.Envelope.Destination
            .ShouldBe(new Uri("asb://topic/shazaam.broadcasted/test"));
    }
}