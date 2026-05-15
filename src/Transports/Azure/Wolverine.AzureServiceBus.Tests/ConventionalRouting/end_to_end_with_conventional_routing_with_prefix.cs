using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.ComplianceTests;
using Wolverine.Tracking;
using Xunit;

namespace Wolverine.AzureServiceBus.Tests.ConventionalRouting;

[Trait("Category", "Flaky")]
public class end_to_end_with_conventional_routing_with_prefix : IAsyncLifetime
{
    private IHost _receiver = null!;
    private IHost _sender = null!;

    public async Task InitializeAsync()
    {
        _sender = await WolverineHost.ForAsync(opts =>
        {
            opts.UseAzureServiceBusTesting()
                .PrefixIdentifiers("shazaam")
                .UseConventionalRouting().AutoProvision().AutoPurgeOnStartup();
            opts.DisableConventionalDiscovery();
            opts.ServiceName = "Sender";
        });

        _receiver = await WolverineHost.ForAsync(opts =>
        {
            opts.UseAzureServiceBusTesting()
                .PrefixIdentifiers("shazaam")
                .UseConventionalRouting().AutoProvision().AutoPurgeOnStartup();
            opts.ServiceName = "Receiver";
        });
    }

    public async Task DisposeAsync()
    {
        if (_sender != null) await _sender.StopAsync();
        if (_receiver != null) await _receiver.StopAsync();
        _sender?.Dispose();
        _receiver?.Dispose();
        await AzureServiceBusTesting.DeleteAllEmulatorObjectsAsync();
    }

    [Fact]
    public async Task send_from_one_node_to_another_all_with_conventional_routing()
    {
        var session = await _sender.TrackActivity()
            .AlsoTrack(_receiver)
            .IncludeExternalTransports()
            .Timeout(30.Seconds())
            .SendMessageAndWaitAsync(new RoutedMessage());

        var received = session
            .AllRecordsInOrder()
            .Where(x => x.Envelope!.Message?.GetType() == typeof(RoutedMessage))
            .Single(x => x.MessageEventType == MessageEventType.Received);

        received
            .ServiceName.ShouldBe("Receiver");

        received.Envelope!.Destination!
            .ShouldBe(new Uri("asb://queue/shazaam.routed"));
    }
}