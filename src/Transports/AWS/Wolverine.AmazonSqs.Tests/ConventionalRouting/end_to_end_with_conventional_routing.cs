using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.ComplianceTests;
using Wolverine.Tracking;

namespace Wolverine.AmazonSqs.Tests.ConventionalRouting;

public class end_to_end_with_conventional_routing : IAsyncLifetime
{
    private IHost _receiver = null!;
    private IHost _sender = null!;

    public async ValueTask InitializeAsync()
    {
        _sender = await WolverineHost.ForAsync(opts =>
        {
            opts.UseAmazonSqsTransportLocally().UseConventionalRouting().AutoProvision().AutoPurgeOnStartup();
            opts.DisableConventionalDiscovery();
            opts.ServiceName = "Sender";
        });

        _receiver = await WolverineHost.ForAsync(opts =>
        {
            opts.UseAmazonSqsTransportLocally().UseConventionalRouting().AutoProvision().AutoPurgeOnStartup();
            opts.ServiceName = "Receiver";
        });
    }

    // StopAsync, not just Dispose: IHost.Dispose() tears down the container without ever running
    // IHostedService.StopAsync, so the SQS listeners keep polling and steal messages from the next
    // class in this namespace. See GH-3763.
    public async ValueTask DisposeAsync()
    {
        await _sender.StopAsync();
        _sender.Dispose();

        await _receiver.StopAsync();
        _receiver.Dispose();
    }

    [Fact]
    public async Task send_from_one_node_to_another_all_with_conventional_routing()
    {
        var session = await _sender.TrackActivity()
            .AlsoTrack(_receiver)
            .IncludeExternalTransports()
            .Timeout(30.Seconds())
            .SendMessageAndWaitAsync(new EndToEndRoutedMessage());

        var received = session
            .AllRecordsInOrder()
            .Where(x => x.Envelope!.Message!.GetType() == typeof(EndToEndRoutedMessage))
            .Single(x => x.MessageEventType == MessageEventType.Received);

        received
            .ServiceName.ShouldBe("Receiver");
    }
}