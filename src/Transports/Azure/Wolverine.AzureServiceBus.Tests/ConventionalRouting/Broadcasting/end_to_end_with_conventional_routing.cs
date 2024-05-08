using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using Shouldly;
using TestingSupport;
using Wolverine.Tracking;
using Xunit;

namespace Wolverine.AzureServiceBus.Tests.ConventionalRouting.Broadcasting;

public class end_to_end_with_conventional_routing : IDisposable
{
    private readonly IHost _receiver;
    private readonly IHost _sender;

    public end_to_end_with_conventional_routing()
    {
        _sender = WolverineHost.For(opts =>
        {
            opts.UseAzureServiceBusTesting().UseTopicAndSubscriptionConventionalRouting().AutoProvision().AutoPurgeOnStartup();
            opts.DisableConventionalDiscovery();
            opts.ServiceName = "Sender";
        });

        _receiver = WolverineHost.For(opts =>
        {
            #region sample_using_topic_and_subscription_conventional_routing_with_azure_service_bus

            opts.UseAzureServiceBusTesting()
                .UseTopicAndSubscriptionConventionalRouting(convention =>
                {
                    // Optionally control every aspect of the convention and
                    // its applicability to types
                    // as well as overriding any listener, sender, topic, or subscription
                    // options
                })

                .AutoProvision()
                .AutoPurgeOnStartup();

            #endregion

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
            .SendMessageAndWaitAsync(new Routed2Message());

        var received = session
            .AllRecordsInOrder()
            .Where(x => x.Envelope.Message?.GetType() == typeof(Routed2Message))
            .Single(x => x.MessageEventType == MessageEventType.Received);

        received
            .ServiceName.ShouldBe("Receiver");
    }
}