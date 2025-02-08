using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using JasperFx.Resources;
using Shouldly;
using Wolverine.ComplianceTests;
using Wolverine.Tracking;
using Xunit;

namespace Wolverine.Pubsub.Tests.ConventionalRouting;

public class end_to_end_with_conventional_routing : IAsyncLifetime
{
    private IHost _receiver = default!;
    private IHost _sender = default!;

    public async Task InitializeAsync()
    {
        _sender = await Host
            .CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts
                    .UsePubsubTesting()
                    .AutoProvision()
                    .AutoPurgeOnStartup()
                    .EnableDeadLettering()
                    .EnableSystemEndpoints()
                    .UseConventionalRouting();

                opts.DisableConventionalDiscovery();

                opts.ServiceName = "Sender";

                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync();

        _receiver = await Host
            .CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts
                    .UsePubsubTesting()
                    .AutoProvision()
                    .AutoPurgeOnStartup()
                    .EnableDeadLettering()
                    .EnableSystemEndpoints()
                    .UseConventionalRouting();

                opts.ServiceName = "Receiver";

                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _sender.StopAsync();
        await _receiver.StopAsync();
    }

    [Fact]
    public async Task send_from_one_node_to_another_all_with_conventional_routing()
    {
        var session = await _sender
            .TrackActivity()
            .AlsoTrack(_receiver)
            .IncludeExternalTransports()
            .Timeout(30.Seconds())
            .SendMessageAndWaitAsync(new RoutedMessage());

        var received = session
            .AllRecordsInOrder()
            .Where(x => x.Envelope.Message?.GetType() == typeof(RoutedMessage))
            .Single(x => x.MessageEventType == MessageEventType.Received);

        received.ServiceName.ShouldBe("Receiver");
    }
}