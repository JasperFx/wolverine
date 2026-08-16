using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.ComplianceTests;
using Wolverine.Tracking;
using Xunit;

namespace Wolverine.RabbitMQ.Tests.ConventionalRouting;

public class end_to_end_with_conventional_routing : IAsyncLifetime, IDisposable
{
    private IHost _receiver = null!;
    private IHost _sender = null!;

    public async ValueTask InitializeAsync()
    {
        _sender = await WolverineHost.ForAsync(opts =>
        {
            opts.UseRabbitMq().UseConventionalRouting(x=> x.IncludeTypes(ConventionalRoutingTestDefaults.RoutingMessageOnly)).AutoProvision().AutoPurgeOnStartup();
            opts.DisableConventionalDiscovery();
            opts.ServiceName = "Sender";
        });

        _receiver = await WolverineHost.ForAsync(opts =>
        {
            opts.UseRabbitMq().UseConventionalRouting(x=> x.IncludeTypes(ConventionalRoutingTestDefaults.RoutingMessageOnly)).AutoProvision().AutoPurgeOnStartup();
            opts.ServiceName = "Receiver";
        });
    }

    // GH-3965: IHost.Dispose() does not run StopAsync, so a synchronous teardown left this
    // class's Rabbit consumers attached to a SHARED, fixed queue name and stealing later
    // tests' messages. Stop the hosts for real.
    async ValueTask IAsyncDisposable.DisposeAsync()
    {
        if (_sender != null) await _sender.StopAsync();
        if (_receiver != null) await _receiver.StopAsync();
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
            .SendMessageAndWaitAsync(new ConventionallyRoutedMessage());

        var received = session
            .AllRecordsInOrder()
            .Where(x => x.Envelope?.Message?.GetType() == typeof(ConventionallyRoutedMessage))
            .Single(x => x.MessageEventType == MessageEventType.Received);

        received
            .ServiceName.ShouldBe("Receiver");
    }
}